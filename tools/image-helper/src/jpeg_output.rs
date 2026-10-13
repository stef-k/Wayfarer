//! Bounded native JPEG encoding of already admitted, normalized RGB pixels.
use crate::{Failure, Result};

/// Reserve canonical ICC space inside one capped caller buffer; native output cannot reallocate it.
/// Only the trusted generated sRGB profile is embedded, never source metadata.
pub fn encode(
    pixels: &[u8],
    width: u32,
    height: u32,
    quality: u8,
    profile: &[u8],
    limit: usize,
) -> Result<Vec<u8>> {
    crate::dimensions(width, height)?;
    if pixels.len() != width as usize * height as usize * 3
        || !(1..=100).contains(&quality)
        || profile.is_empty()
        || profile.len() > 65519
    {
        return Err(Failure::Unavailable);
    }
    let overhead = 18 + profile.len();
    let capacity = limit.checked_sub(overhead).ok_or(Failure::Unavailable)?;
    if capacity < 4 {
        return Err(Failure::Unavailable);
    }
    let subsampling = if quality <= 90 {
        turbojpeg::Subsamp::Sub2x2
    } else {
        turbojpeg::Subsamp::None
    };
    let bound = turbojpeg::compressed_buf_len(width as usize, height as usize, subsampling)
        .map_err(|_| Failure::Unavailable)?;
    let mut output = crate::zeroed(overhead + capacity.min(bound))?;
    let mut compressor = turbojpeg::Compressor::new().map_err(|_| Failure::Unavailable)?;
    compressor
        .set_quality(quality.into())
        .map_err(|_| Failure::Unavailable)?;
    compressor
        .set_subsamp(subsampling)
        .map_err(|_| Failure::Unavailable)?;
    compressor
        .set_optimize(false)
        .map_err(|_| Failure::Unavailable)?;
    compressor
        .set_progressive(false)
        .map_err(|_| Failure::Unavailable)?;
    let image = turbojpeg::Image {
        pixels,
        width: width as usize,
        pitch: width as usize * 3,
        height: height as usize,
        format: turbojpeg::PixelFormat::RGB,
    };
    // compress_to_slice sets TJPARAM_NOREALLOC for this borrowed caller-controlled extent.
    let length = compressor
        .compress_to_slice(image, &mut output[overhead..])
        .map_err(|_| Failure::Unavailable)?;
    if length < 4
        || length > output.len() - overhead
        || output[overhead..overhead + 2] != [0xff, 0xd8]
        || output[overhead + length - 2..overhead + length] != [0xff, 0xd9]
    {
        return Err(Failure::Unavailable);
    }
    // A single standard APP2 segment fits the generated 612-byte profile. The JPEG body stays put.
    output[..4].copy_from_slice(&[0xff, 0xd8, 0xff, 0xe2]);
    output[4..6].copy_from_slice(&((profile.len() + 16) as u16).to_be_bytes());
    output[6..18].copy_from_slice(b"ICC_PROFILE\0");
    output[18..20].copy_from_slice(&[1, 1]);
    output[20..overhead + 2].copy_from_slice(profile);
    output.truncate(overhead + length);
    Ok(output)
}
