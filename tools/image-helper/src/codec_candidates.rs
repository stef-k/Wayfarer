//! Scratch-only decoder substitutions; existing admission and metadata authorities stay in place.
use crate::{Failure, Result};

/// Only admitted eight-bit RGB JPEGs use strict candidate decoding; recovery stays with the original codec.
#[cfg(feature = "jpeg-candidate-decode")]
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
#[cfg(feature = "jpeg-candidate-decode")]
fn jpeg_error(error: libjpeg_turbo_rs::JpegError) -> Failure {
    match error {
        libjpeg_turbo_rs::JpegError::LimitExceeded {
            what: "estimated decode memory",
            ..
        } => Failure::Unavailable,
        _ => Failure::Source,
    }
}

/// libwebp's advanced API writes into admitted fallibly allocated pixels and distinguishes OOM.
/// The webp 0.3.1 convenience decoder has the same default chroma behavior but only returns Option.
#[cfg(feature = "webp-candidate-decode")]
pub(crate) fn webp_pixels(bytes: &[u8], width: u32, height: u32, alpha: bool) -> Result<Vec<u8>> {
    use libwebp_sys::{VP8StatusCode, WEBP_CSP_MODE, WebPDecBuffer__bindgen_ty_1, WebPRGBABuffer};
    let features = webp::BitstreamFeatures::new(bytes).ok_or(Failure::Source)?;
    if features.has_animation()
        || (features.width(), features.height()) != (width, height)
        || features.has_alpha() != alpha
    {
        return Err(Failure::Source);
    }
    let channels = if alpha { 4 } else { 3 };
    let mut pixels = crate::zeroed(crate::dimensions(width, height)? * channels)?;
    let mut config = libwebp_sys::WebPDecoderConfig::new().map_err(|_| Failure::Unavailable)?;
    config.output.colorspace = if alpha {
        WEBP_CSP_MODE::MODE_RGBA
    } else {
        WEBP_CSP_MODE::MODE_RGB
    };
    config.output.is_external_memory = 1;
    config.output.u = WebPDecBuffer__bindgen_ty_1 {
        RGBA: WebPRGBABuffer {
            rgba: pixels.as_mut_ptr(),
            stride: (width as usize * channels) as i32,
            size: pixels.len(),
        },
    };
    // SAFETY: input and admitted external pixels stay alive; stride/extent match the format.
    // FreeDecBuffer never frees external memory and releases any decoder-owned state on failure.
    let status = unsafe {
        let status = libwebp_sys::WebPDecode(bytes.as_ptr(), bytes.len(), &mut config);
        libwebp_sys::WebPFreeDecBuffer(&mut config.output);
        status
    };
    match status {
        VP8StatusCode::VP8_STATUS_OK => Ok(pixels),
        VP8StatusCode::VP8_STATUS_OUT_OF_MEMORY => Err(Failure::Unavailable),
        _ => Err(Failure::Source),
    }
}
