//! Bounded row CMS produces one RGBA8 raster, preserving straight alpha separately.
use crate::{
    Failure, Result,
    raster::{Raster, Samples},
    zeroed,
};
use moxcms::{ColorProfile, DataColorSpace, Layout, RenderingIntent, TransformOptions};

/// Maximum live normalization rasters: 320-MB RGBA16 source plus 160-MB RGBA8.
pub(crate) fn normalize(source: &Raster) -> Result<image::RgbaImage> {
    let channels = source.layout.channels();
    let pixels = source.width as usize * source.height as usize;
    let mut output = zeroed(pixels * 4)?;
    if let Some(profile) = &source.profile {
        let expected = if source.cmyk {
            DataColorSpace::Cmyk
        } else if matches!(source.layout, Layout::Gray | Layout::GrayAlpha) {
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
        match &source.samples {
            Samples::Eight(data) => {
                let transform = profile
                    .create_transform_8bit(source.layout, &destination, Layout::Rgb, options)
                    .map_err(|_| Failure::Source)?;
                let mut row = zeroed(source.width as usize * 3)?;
                for (input, out) in data
                    .chunks_exact(source.width as usize * channels)
                    .zip(output.chunks_exact_mut(source.width as usize * 4))
                {
                    transform
                        .transform(input, &mut row)
                        .map_err(|_| Failure::Source)?;
                    fill(out, input, &row, source, 255);
                }
            }
            Samples::Sixteen(data) => {
                let transform = profile
                    .create_transform_16bit(source.layout, &destination, Layout::Rgb, options)
                    .map_err(|_| Failure::Source)?;
                let mut row = zeroed(source.width as usize * 3)?;
                for (input, out) in data
                    .chunks_exact(source.width as usize * channels)
                    .zip(output.chunks_exact_mut(source.width as usize * 4))
                {
                    transform
                        .transform(input, &mut row)
                        .map_err(|_| Failure::Source)?;
                    fill(out, input, &row, source, 65535);
                }
            }
        }
    } else {
        match &source.samples {
            Samples::Eight(data) => untagged(&mut output, data, source, 255),
            Samples::Sixteen(data) => untagged(&mut output, data, source, 65535),
        }
    }
    image::RgbaImage::from_raw(source.width, source.height, output).ok_or(Failure::Unavailable)
}

/// Quantize only after CMS; RGB output never supplies alpha for CMYK.
fn fill<T: Copy + Into<u32>>(
    out: &mut [u8],
    input: &[T],
    rgb: &[T],
    source: &Raster,
    maximum: u32,
) {
    for ((out, pixel), rgb) in out
        .chunks_exact_mut(4)
        .zip(input.chunks_exact(source.layout.channels()))
        .zip(rgb.chunks_exact(3))
    {
        for i in 0..3 {
            out[i] = quantize(rgb[i].into(), maximum);
        }
        out[3] = alpha(pixel, source, maximum);
    }
}

/// Untagged gray/RGB is sRGB; untagged CMYK matches the existing multiplicative convention.
fn untagged<T: Copy + Into<u32>>(out: &mut [u8], input: &[T], source: &Raster, maximum: u32) {
    for (out, pixel) in out
        .chunks_exact_mut(4)
        .zip(input.chunks_exact(source.layout.channels()))
    {
        for i in 0..3 {
            let value = if source.cmyk {
                (maximum - pixel[i].into()) * (maximum - pixel[3].into()) / maximum
            } else if matches!(source.layout, Layout::Gray | Layout::GrayAlpha) {
                pixel[0].into()
            } else {
                pixel[i].into()
            };
            out[i] = quantize(value, maximum);
        }
        out[3] = alpha(pixel, source, maximum);
    }
}

/// Rounded integer conversion retains the full 16-bit input through color processing.
fn quantize(value: u32, maximum: u32) -> u8 {
    ((value * 255 + maximum / 2) / maximum) as u8
}

/// Preserve alpha independently; a fully transparent output has no visible hidden color.
fn alpha<T: Copy + Into<u32>>(pixel: &[T], source: &Raster, maximum: u32) -> u8 {
    if !source.cmyk && matches!(source.layout, Layout::GrayAlpha | Layout::Rgba) {
        quantize(pixel[pixel.len() - 1].into(), maximum)
    } else {
        255
    }
}
