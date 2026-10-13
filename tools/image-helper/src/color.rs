//! Conditional relative-colorimetric conversion to sRGB, preserving source precision and alpha.
use crate::{
    Failure, Result,
    raster::{Raster, Samples},
    zeroed,
};
use image::{Rgba, Rgba32FImage};
use moxcms::{
    Chromaticity, ColorPrimaries, ColorProfile, DataColorSpace, Layout, ParsingOptions,
    RenderingIntent, TransformOptions, XyY,
};

/// Individual CMS structures are bounded; inherited process limits bound aggregate memory.
pub(crate) fn parse_profile(bytes: &[u8]) -> Result<ColorProfile> {
    ColorProfile::new_from_slice_with_options(
        bytes,
        ParsingOptions {
            max_profile_size: 4 * 1024 * 1024,
            max_allowed_clut_size: 16 * 1024 * 1024,
            max_allowed_trc_size: 65_536,
        },
    )
    .map_err(|_| Failure::Source)
}

/// PNG precedence: supported SDR cICP, ICC, sRGB, then gAMA/cHRM, then untagged sRGB.
pub(crate) fn png_profile(info: &png::Info<'_>, layout: Layout) -> Result<Option<ColorProfile>> {
    // A higher-precedence color declaration does not excuse an explicitly invalid ICC profile.
    let icc = info.icc_profile.as_deref().map(parse_profile).transpose()?;
    if let Some(profile) = &icc {
        let expected = if matches!(layout, Layout::Gray | Layout::GrayAlpha) {
            DataColorSpace::Gray
        } else {
            DataColorSpace::Rgb
        };
        if profile.color_space != expected {
            return Err(Failure::Source);
        }
    }
    let mut profile = ColorProfile::new_srgb();
    if let Some(cicp) = info.coding_independent_code_points {
        if cicp.matrix_coefficients != 0
            || !cicp.is_video_full_range_image
            || matches!(cicp.transfer_function, 16 | 18)
        {
            return Err(Failure::Source);
        }
        let primaries = moxcms::CicpColorPrimaries::try_from(cicp.color_primaries)
            .map_err(|_| Failure::Source)?;
        let transfer = moxcms::TransferCharacteristics::try_from(cicp.transfer_function)
            .map_err(|_| Failure::Source)?;
        let _: ColorPrimaries = primaries.try_into().map_err(|_| Failure::Source)?;
        let _: moxcms::ToneReprCurve = transfer.try_into().map_err(|_| Failure::Source)?;
        profile = ColorProfile::new_from_cicp(moxcms::CicpProfile {
            color_primaries: primaries,
            transfer_characteristics: transfer,
            matrix_coefficients: moxcms::MatrixCoefficients::Identity,
            full_range: true,
        });
    } else if let Some(icc) = icc {
        return Ok(Some(icc));
    } else if info.srgb.is_some() {
        return Ok(None);
    } else {
        // The fallback declarations must replace the constructor's sRGB transfer hints.
        profile.cicp = None;
        if let Some(chroma) = info.chrm_chunk {
            let xy = |point: (png::ScaledFloat, png::ScaledFloat)| -> Result<Chromaticity> {
                let (x, y) = (point.0.into_value(), point.1.into_value());
                if x < 0.0 || y <= 0.0 || x + y > 1.0 {
                    return Err(Failure::Source);
                }
                Ok(Chromaticity::new(x, y))
            };
            let white = xy(chroma.white)?;
            profile.update_rgb_colorimetry(
                XyY::new(white.x.into(), white.y.into(), 1.0),
                ColorPrimaries {
                    red: xy(chroma.red)?,
                    green: xy(chroma.green)?,
                    blue: xy(chroma.blue)?,
                },
            );
        }
        if let Some(gamma) = info.gama_chunk {
            let gamma = gamma.into_value();
            if gamma <= 0.0 {
                return Err(Failure::Source);
            }
            let exponent = 1.0 / gamma;
            if !(1.0 / 256.0..=65535.0 / 256.0).contains(&exponent) {
                return Err(Failure::Source);
            }
            profile.red_trc = Some(moxcms::curve_from_gamma(exponent));
            profile.green_trc = profile.red_trc.clone();
            profile.blue_trc = profile.red_trc.clone();
        }
        if info.gama_chunk.is_none() && info.chrm_chunk.is_none() {
            return Ok(None);
        }
    }
    if matches!(layout, Layout::Gray | Layout::GrayAlpha) {
        profile.color_space = DataColorSpace::Gray;
        profile.gray_trc = profile.red_trc.clone();
    }
    if profile
        .rgb_to_xyz_matrix()
        .v
        .iter()
        .flatten()
        .any(|value| !value.is_finite())
    {
        return Err(Failure::Source);
    }
    Ok(Some(profile))
}

/// Opaque 8-bit sources need no float raster or alpha arithmetic. Tagged colors still use CMS.
pub(crate) fn opaque_rgb8(raster: &mut Raster) -> Result<Option<image::RgbImage>> {
    if raster.cmyk {
        return Ok(None);
    }
    let Samples::Eight(data) = &mut raster.samples else {
        return Ok(None);
    };
    let pixels = raster.width as usize * raster.height as usize;
    let channels = raster.layout.channels();
    if data.len() != pixels * channels {
        return Err(Failure::Source);
    }
    if matches!(raster.layout, Layout::GrayAlpha | Layout::Rgba)
        && data
            .chunks_exact(channels)
            .any(|pixel| pixel[channels - 1] != 255)
    {
        return Ok(None);
    }
    let rgb = if let Some(profile) = &raster.profile {
        let expected = if matches!(raster.layout, Layout::Gray | Layout::GrayAlpha) {
            DataColorSpace::Gray
        } else {
            DataColorSpace::Rgb
        };
        if profile.color_space != expected {
            return Err(Failure::Source);
        }
        let mut converted = zeroed(pixels * 3)?;
        profile
            .create_transform_8bit(
                raster.layout,
                &ColorProfile::new_srgb(),
                Layout::Rgb,
                TransformOptions {
                    rendering_intent: RenderingIntent::RelativeColorimetric,
                    ..Default::default()
                },
            )
            .map_err(|_| Failure::Source)?
            .transform(data, &mut converted)
            .map_err(|_| Failure::Source)?;
        converted
    } else if raster.layout == Layout::Rgb {
        std::mem::take(data)
    } else if raster.layout == Layout::Rgba {
        // Compact proven opaque RGBA in its owned buffer; copying forward cannot overwrite future pixels.
        for index in 0..pixels {
            let pixel = [data[index * 4], data[index * 4 + 1], data[index * 4 + 2]];
            data[index * 3..index * 3 + 3].copy_from_slice(&pixel);
        }
        data.truncate(pixels * 3);
        std::mem::take(data)
    } else {
        let mut converted = zeroed(pixels * 3)?;
        for (out, pixel) in converted
            .chunks_exact_mut(3)
            .zip(data.chunks_exact(channels))
        {
            out.fill(pixel[0]);
        }
        converted
    };
    image::RgbImage::from_raw(raster.width, raster.height, rgb)
        .map(Some)
        .ok_or(Failure::Unavailable)
}

/// Layout Rgba means C/M/Y/K for CMYK profiles; K must never be mistaken for alpha.
pub(crate) fn to_srgb(raster: &Raster) -> Result<Rgba32FImage> {
    let pixels = (raster.width as usize) * raster.height as usize;
    let channels = raster.layout.channels();
    let source_length = match &raster.samples {
        Samples::Eight(data) => data.len(),
        Samples::Sixteen(data) => data.len(),
    };
    if source_length != pixels * channels {
        return Err(Failure::Source);
    }
    let mut rgba = zeroed(pixels * 4)?;
    if let Some(profile) = &raster.profile {
        let expected = if raster.cmyk {
            DataColorSpace::Cmyk
        } else if matches!(raster.layout, Layout::Gray | Layout::GrayAlpha) {
            DataColorSpace::Gray
        } else {
            DataColorSpace::Rgb
        };
        if profile.color_space != expected {
            return Err(Failure::Source);
        }
        let options = TransformOptions {
            rendering_intent: RenderingIntent::RelativeColorimetric,
            ..Default::default()
        };
        let destination = ColorProfile::new_srgb();
        match &raster.samples {
            Samples::Eight(data) => {
                let mut converted = zeroed(pixels * 3)?;
                profile
                    .create_transform_8bit(raster.layout, &destination, Layout::Rgb, options)
                    .map_err(|_| Failure::Source)?
                    .transform(data, &mut converted)
                    .map_err(|_| Failure::Source)?;
                fill(
                    &mut rgba,
                    &converted,
                    data,
                    raster.layout,
                    raster.cmyk,
                    255.0,
                );
            }
            Samples::Sixteen(data) => {
                let mut converted = zeroed(pixels * 3)?;
                profile
                    .create_transform_16bit(raster.layout, &destination, Layout::Rgb, options)
                    .map_err(|_| Failure::Source)?
                    .transform(data, &mut converted)
                    .map_err(|_| Failure::Source)?;
                fill(
                    &mut rgba,
                    &converted,
                    data,
                    raster.layout,
                    raster.cmyk,
                    65535.0,
                );
            }
        }
    } else {
        match &raster.samples {
            Samples::Eight(data) => untagged(&mut rgba, data, raster.layout, raster.cmyk, 255.0),
            Samples::Sixteen(data) => {
                untagged(&mut rgba, data, raster.layout, raster.cmyk, 65535.0)
            }
        }
    }
    Rgba32FImage::from_raw(raster.width, raster.height, rgba).ok_or(Failure::Unavailable)
}

/// Preserve original straight alpha independently of CMS output.
fn fill<T: Copy + Into<f32>>(
    rgba: &mut [f32],
    rgb: &[T],
    input: &[T],
    layout: Layout,
    cmyk: bool,
    maximum: f32,
) {
    for ((out, color), original) in rgba
        .chunks_exact_mut(4)
        .zip(rgb.chunks_exact(3))
        .zip(input.chunks_exact(layout.channels()))
    {
        for index in 0..3 {
            out[index] = color[index].into() / maximum;
        }
        out[3] = alpha(original, layout, cmyk, maximum);
    }
}

/// Untagged gray/RGB are sRGB; untagged CMYK uses conventional multiplicative conversion.
fn untagged<T: Copy + Into<f32>>(
    rgba: &mut [f32],
    input: &[T],
    layout: Layout,
    cmyk: bool,
    maximum: f32,
) {
    for (out, original) in rgba
        .chunks_exact_mut(4)
        .zip(input.chunks_exact(layout.channels()))
    {
        for index in 0..3 {
            out[index] = if cmyk {
                (1.0 - original[index].into() / maximum) * (1.0 - original[3].into() / maximum)
            } else if matches!(layout, Layout::Gray | Layout::GrayAlpha) {
                original[0].into() / maximum
            } else {
                original[index].into() / maximum
            };
        }
        out[3] = alpha(original, layout, cmyk, maximum);
    }
}

/// A true alpha channel occurs only in non-CMYK GrayAlpha/Rgba.
fn alpha<T: Copy + Into<f32>>(pixel: &[T], layout: Layout, cmyk: bool, maximum: f32) -> f32 {
    if !cmyk && matches!(layout, Layout::GrayAlpha | Layout::Rgba) {
        pixel[pixel.len() - 1].into() / maximum
    } else {
        1.0
    }
}

/// Convert straight sRGB pixels to the linear-light premultiplied resize contract.
pub(crate) fn premultiply(image: &mut Rgba32FImage) {
    for Rgba(pixel) in image.pixels_mut() {
        for index in 0..3 {
            let value = pixel[index];
            let linear = if value <= 0.04045 {
                value / 12.92
            } else {
                ((value + 0.055) / 1.055).powf(2.4)
            };
            pixel[index] = linear * pixel[3];
        }
    }
}

/// Clamp filter overshoot, remove hidden zero-alpha color, and quantize in sRGB.
pub(crate) fn quantize(image: Rgba32FImage) -> Result<image::RgbaImage> {
    let mut output = zeroed(image.width() as usize * image.height() as usize * 4)?;
    for (pixel, out) in image.pixels().zip(output.chunks_exact_mut(4)) {
        let alpha = pixel[3].clamp(0.0, 1.0);
        for index in 0..3 {
            let linear = if alpha == 0.0 {
                0.0
            } else {
                (pixel[index] / alpha).clamp(0.0, 1.0)
            };
            let encoded = if linear <= 0.0031308 {
                linear * 12.92
            } else {
                1.055 * linear.powf(1.0 / 2.4) - 0.055
            };
            out[index] = (encoded * 255.0).round() as u8;
        }
        out[3] = (alpha * 255.0).round() as u8;
        if out[3] == 0 {
            out[..3].fill(0);
        }
    }
    image::RgbaImage::from_raw(image.width(), image.height(), output).ok_or(Failure::Unavailable)
}
