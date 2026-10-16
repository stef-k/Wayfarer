//! Selected Rust JPEG decoder; admission and metadata authorities stay in place.
use crate::{Failure, Result};

/// Only admitted eight-bit RGB JPEGs use strict candidate decoding; recovery stays with the original codec.
pub(crate) fn jpeg_rgb(bytes: &[u8], width: u32, height: u32) -> Result<libjpeg_turbo_rs::Image> {
    use libjpeg_turbo_rs::{DecodeLimits, Decoder, PixelFormat};
    let limits = DecodeLimits {
        max_width: 8192,
        max_height: 8192,
        max_pixels: 12_000_000,
        max_memory: Some(128 * 1024 * 1024),
        ..DecodeLimits::default()
    };
    let decoded = Decoder::new_with_limits(bytes, limits)
        .map_err(jpeg_error)?
        .with_output_format(PixelFormat::Rgb)
        .decode_image()
        .map_err(jpeg_error)?;
    if decoded.precision != 8
        || (decoded.width, decoded.height) != (width as usize, height as usize)
        || decoded.data.len() != width as usize * height as usize * 3
    {
        return Err(Failure::Source);
    }
    Ok(decoded)
}

/// Decoder scratch refusal is infrastructure failure; admitted geometry has already been checked.
fn jpeg_error(error: libjpeg_turbo_rs::JpegError) -> Failure {
    match error {
        libjpeg_turbo_rs::JpegError::LimitExceeded {
            what: "estimated decode memory",
            ..
        } => Failure::Unavailable,
        _ => Failure::Source,
    }
}
