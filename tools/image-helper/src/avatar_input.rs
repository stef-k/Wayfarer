//! Strict avatar decoders. Scratch is dropped before row-wise normalization.
use crate::{
    Failure, Result, color,
    raster::{Raster, Samples, orientation},
    zeroed,
};
use image::ImageFormat;
use moxcms::Layout;
use std::io::Cursor;

/// Source samples use at most 320 MB (40M RGBA16); no full-frame float expansion.
pub(crate) fn dimensions(width: u32, height: u32) -> Result<usize> {
    if width == 0 || height == 0 {
        return Err(Failure::Source);
    }
    let pixels = (width as usize)
        .checked_mul(height as usize)
        .ok_or(Failure::TooLarge)?;
    if width > 16384 || height > 16384 || pixels > 40_000_000 {
        return Err(Failure::TooLarge);
    }
    Ok(pixels)
}

/// Byte signatures select the decoder; MIME and filenames are never authoritative.
pub(crate) fn decode(bytes: &[u8]) -> Result<Raster> {
    match image::guess_format(bytes).map_err(|_| Failure::Unsupported)? {
        ImageFormat::Jpeg => jpeg(bytes),
        ImageFormat::Png => png(bytes),
        // This best-effort WebP API excludes caller samples; leave 160 MB for RGBA8
        // and 128 MiB for input, profiles and runtime within the 1-GiB child ceiling.
        ImageFormat::WebP => crate::webp_input::decode_with_geometry(
            bytes,
            dimensions,
            768 * 1024 * 1024 - 160_000_000,
        ),
        _ => Err(Failure::Unsupported),
    }
}

/// Strict JPEG includes packed output in its 768-MiB decoder estimate; no recovery fallback.
fn jpeg(bytes: &[u8]) -> Result<Raster> {
    use libjpeg_turbo_rs::{ColorSpace, DecodeLimits, Decoder, PixelFormat};
    if !bytes.ends_with(&[0xff, 0xd9]) {
        return Err(Failure::Source);
    }
    let limits = DecodeLimits {
        max_width: 16384,
        max_height: 16384,
        max_pixels: 40_000_000,
        max_memory: Some(768 * 1024 * 1024),
        ..Default::default()
    };
    let mut decoder = Decoder::new_with_limits(bytes, limits).map_err(jpeg_error)?;
    let cmyk = matches!(
        decoder.jpeg_color_space(),
        ColorSpace::Cmyk | ColorSpace::Ycck
    );
    decoder = decoder.with_output_format(if cmyk {
        PixelFormat::Cmyk
    } else {
        PixelFormat::Rgb
    });
    decoder.set_stop_on_warning(true);
    // The selected Rust decoder accepts one retained truncated-entropy fixture without
    // a warning. The already-shipped native decoder supplies a strict completion pass
    // at 1/8 scale; its coefficient scratch is released before the full decode.
    strict_jpeg_completion(bytes)?;
    let mut decoded = decoder.decode_image().map_err(jpeg_error)?;
    dimensions(decoded.width as u32, decoded.height as u32)?;
    if decoded.precision != 8 || !decoded.warnings.is_empty() {
        return Err(Failure::Source);
    }
    // Match the retained JPEG adapter's conventional C/M/Y/K representation, including K.
    if cmyk {
        decoded.data.iter_mut().for_each(|v| *v = 255 - *v);
    }
    Ok(Raster {
        width: decoded.width as u32,
        height: decoded.height as u32,
        samples: Samples::Eight(decoded.data),
        layout: if cmyk { Layout::Rgba } else { Layout::Rgb },
        cmyk,
        profile: decoded
            .icc_profile
            .as_deref()
            .map(color::parse_profile)
            .transpose()?,
        orientation: orientation(decoded.exif_data.as_deref()),
        format: ImageFormat::Jpeg,
    })
}

/// Geometry limits are positive rejection evidence; decoder allocation limits are availability.
fn jpeg_error(error: libjpeg_turbo_rs::JpegError) -> Failure {
    match error {
        libjpeg_turbo_rs::JpegError::LimitExceeded {
            what: "estimated decode memory",
            ..
        } => Failure::Unavailable,
        libjpeg_turbo_rs::JpegError::LimitExceeded { .. } => Failure::TooLarge,
        _ => Failure::Source,
    }
}

/// Preflight's immutable filtered witness is freed before allocating expanded samples.
fn png(bytes: &[u8]) -> Result<Raster> {
    crate::png_input::admit_with_geometry(bytes, dimensions)?;
    let mut decoder =
        png::Decoder::new_with_options(Cursor::new(bytes), crate::png_input::options());
    // png::Limits excludes caller-owned output (up to 320 MB). Reserve that first.
    decoder.set_limits(png::Limits {
        bytes: 768 * 1024 * 1024 - 320_000_000,
    });
    decoder.set_transformations(png::Transformations::EXPAND);
    let mut reader = decoder.read_info().map_err(crate::png_input::png_error)?;
    let length = reader.output_buffer_size().ok_or(Failure::Unavailable)?;
    let sixteen = reader.output_color_type().1 == png::BitDepth::Sixteen;
    let (samples, output) = if sixteen {
        let mut values: Vec<u16> = zeroed(length / 2)?;
        // SAFETY: u16 storage is aligned and initialized; its complete byte extent is
        // writable, with no aliases. Convert big-endian PNG words in place afterwards.
        let bytes =
            unsafe { std::slice::from_raw_parts_mut(values.as_mut_ptr().cast::<u8>(), length) };
        let output = reader
            .next_frame(bytes)
            .map_err(crate::png_input::png_error)?;
        for value in &mut values {
            *value = u16::from_be(*value);
        }
        (Samples::Sixteen(values), output)
    } else {
        let mut values = zeroed(length)?;
        let output = reader
            .next_frame(&mut values)
            .map_err(crate::png_input::png_error)?;
        (Samples::Eight(values), output)
    };
    reader.finish().map_err(crate::png_input::png_error)?;
    let layout = match output.color_type {
        png::ColorType::Grayscale => Layout::Gray,
        png::ColorType::GrayscaleAlpha => Layout::GrayAlpha,
        png::ColorType::Rgb => Layout::Rgb,
        png::ColorType::Rgba => Layout::Rgba,
        _ => return Err(Failure::Source),
    };
    Ok(Raster {
        width: output.width,
        height: output.height,
        samples,
        layout,
        cmyk: false,
        profile: color::png_profile(reader.info(), layout)?,
        orientation: orientation(reader.info().exif_metadata.as_deref()),
        format: ImageFormat::Png,
    })
}

/// Existing native codec validates entropy completion; no filesystem or recovery path.
fn strict_jpeg_completion(bytes: &[u8]) -> Result<()> {
    use turbojpeg_sys as tj;
    // SAFETY: fresh native handle, checked before use, and destroyed on every branch.
    let handle = unsafe { tj::tj3Init(tj::TJINIT_TJINIT_DECOMPRESS as i32) };
    if handle.is_null() {
        return Err(Failure::Unavailable);
    }
    let result = (|| {
        // All limits are trusted constants. MAXMEMORY includes progressive coefficients;
        // caller thumbnail samples are <=2.5 MB and are not counted by that native API.
        unsafe {
            if tj::tj3Set(handle, tj::TJPARAM_TJPARAM_STOPONWARNING as i32, 1) != 0
                || tj::tj3Set(handle, tj::TJPARAM_TJPARAM_MAXMEMORY as i32, 768) != 0
                || tj::tj3Set(handle, tj::TJPARAM_TJPARAM_MAXPIXELS as i32, 40_000_000) != 0
            {
                return Err(Failure::Unavailable);
            }
            if tj::tj3DecompressHeader(handle, bytes.as_ptr(), bytes.len() as _) != 0 {
                return Err(Failure::Source);
            }
            let width = tj::tj3Get(handle, tj::TJPARAM_TJPARAM_JPEGWIDTH as i32);
            let height = tj::tj3Get(handle, tj::TJPARAM_TJPARAM_JPEGHEIGHT as i32);
            dimensions(width as u32, height as u32)?;
            let cmyk = matches!(
                tj::tj3Get(handle, tj::TJPARAM_TJPARAM_COLORSPACE as i32),
                3 | 4
            );
            if tj::tj3SetScalingFactor(handle, tj::tjscalingfactor { num: 1, denom: 8 }) != 0 {
                return Err(Failure::Source);
            }
            let channels = if cmyk { 4 } else { 3 };
            let pitch = (width + 7) / 8 * channels;
            let mut output: Vec<u8> = zeroed(pitch as usize * ((height + 7) / 8) as usize)?;
            if tj::tj3Decompress8(
                handle,
                bytes.as_ptr(),
                bytes.len() as _,
                output.as_mut_ptr(),
                pitch,
                if cmyk {
                    tj::TJPF_TJPF_CMYK as i32
                } else {
                    tj::TJPF_TJPF_RGB as i32
                },
            ) != 0
            {
                return Err(Failure::Source);
            }
        }
        Ok(())
    })();
    // SAFETY: this invocation exclusively owns the handle and uses it no further.
    unsafe { tj::tj3Destroy(handle) };
    result
}
