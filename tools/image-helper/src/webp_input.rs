//! Bounded RIFF authority extends Wayfarer's existing walk; codecs still own payload headers.
use crate::{
    Failure, Result, color, dimensions,
    raster::{Raster, Samples, orientation},
    zeroed,
};
use image::ImageFormat;
use moxcms::Layout;
use std::{io::Cursor, ops::Range};

/// Require one authoritative image payload, consistent metadata flags and complete decoding.
pub(crate) fn decode(bytes: &[u8]) -> Result<Raster> {
    let authority = admit(bytes)?;
    let mut decoder = image_webp::WebPDecoder::new(Cursor::new(bytes)).map_err(webp_error)?;
    decoder.set_memory_limit(128 * 1024 * 1024);
    let (width, height) = decoder.dimensions();
    dimensions(width, height)?;
    if decoder.is_animated()
        || (width, height) != authority.geometry
        || decoder.has_alpha() != authority.alpha
    {
        return Err(Failure::Source);
    }
    let profile = decoder
        .icc_profile()
        .map_err(webp_error)?
        .as_deref()
        .map(color::parse_profile)
        .transpose()?;
    let exif = decoder.exif_metadata().map_err(webp_error)?;
    let mut samples = zeroed(decoder.output_buffer_size().ok_or(Failure::Unavailable)?)?;
    decoder.read_image(&mut samples).map_err(webp_error)?;
    Ok(Raster {
        width,
        height,
        samples: Samples::Eight(samples),
        layout: if decoder.has_alpha() {
            Layout::Rgba
        } else {
            Layout::Rgb
        },
        profile,
        cmyk: false,
        orientation: orientation(exif.as_deref()),
        format: ImageFormat::WebP,
    })
}

/// Only the container walk interprets chunk boundaries; the maintained codec supplies geometry.
struct Authority {
    geometry: (u32, u32),
    alpha: bool,
}

/// Validate exact extent, padding, placement, duplicate known chunks and animation contradictions.
fn admit(bytes: &[u8]) -> Result<Authority> {
    if bytes.len() < 12
        || &bytes[..4] != b"RIFF"
        || &bytes[8..12] != b"WEBP"
        || read32(&bytes[4..8]) as usize + 8 != bytes.len()
    {
        return Err(Failure::Source);
    }
    let mut offset = 12usize;
    let mut flags = 0;
    let mut canvas = None;
    let mut payload = None;
    let mut metadata = 0;
    let mut alpha = false;
    let mut previous = [0; 4];
    while offset < bytes.len() {
        let header = bytes.get(offset..offset + 8).ok_or(Failure::Source)?;
        let length = read32(&header[4..8]) as usize;
        let end = offset
            .checked_add(8)
            .and_then(|n| n.checked_add(length))
            .ok_or(Failure::Source)?;
        let next = end.checked_add(length & 1).ok_or(Failure::Source)?;
        let data = bytes.get(offset + 8..end).ok_or(Failure::Source)?;
        if next > bytes.len() || (length & 1 != 0 && bytes[end] != 0) {
            return Err(Failure::Source);
        }
        match &header[..4] {
            b"VP8X" => {
                if offset != 12 || length != 10 || data[0] & 0xc3 != 0 || data[1..4] != [0, 0, 0] {
                    return Err(Failure::Source);
                }
                flags = data[0];
                let geometry = (read24(&data[4..7]) + 1, read24(&data[7..10]) + 1);
                dimensions(geometry.0, geometry.1)?;
                canvas = Some(geometry);
            }
            b"ANIM" | b"ANMF" => return Err(Failure::Source),
            b"VP8 " | b"VP8L" => {
                if payload.is_some() || (alpha && (&header[..4] != b"VP8 " || &previous != b"ALPH"))
                {
                    return Err(Failure::Source);
                }
                payload = Some(offset..next);
            }
            b"ALPH" => {
                if alpha || payload.is_some() || canvas.is_none() || length == 0 {
                    return Err(Failure::Source);
                }
                alpha = true;
            }
            b"ICCP" => {
                if payload.is_some() {
                    return Err(Failure::Source);
                }
                metadata_chunk(&mut metadata, 0x20)?;
            }
            b"EXIF" => metadata_chunk(&mut metadata, 0x08)?,
            b"XMP " => metadata_chunk(&mut metadata, 0x04)?,
            _ => {}
        }
        previous.copy_from_slice(&header[..4]);
        offset = next;
    }
    let payload = payload.ok_or(Failure::Source)?;
    let (geometry, payload_alpha) = payload_info(bytes, payload)?;
    alpha |= payload_alpha;
    if canvas.is_some_and(|expected| expected != geometry)
        || flags & 0x2c != metadata
        || (canvas.is_some() && (flags & 0x10 != 0) != alpha)
        || (canvas.is_none() && metadata != 0)
    {
        return Err(Failure::Source);
    }
    Ok(Authority { geometry, alpha })
}

/// A known metadata declaration may occur only once.
fn metadata_chunk(seen: &mut u8, flag: u8) -> Result<()> {
    if *seen & flag != 0 {
        return Err(Failure::Source);
    }
    *seen |= flag;
    Ok(())
}

/// Ask the same public codec to read a simple bounded payload, avoiding a custom VP8 parser.
/// This temporary encoded copy is dropped before the original container is pixel-decoded.
fn payload_info(bytes: &[u8], range: Range<usize>) -> Result<((u32, u32), bool)> {
    let mut simple = zeroed(12 + range.len())?;
    simple[..4].copy_from_slice(b"RIFF");
    let length = simple.len() as u32 - 8;
    simple[4..8].copy_from_slice(&length.to_le_bytes());
    simple[8..12].copy_from_slice(b"WEBP");
    simple[12..].copy_from_slice(&bytes[range]);
    let decoder = image_webp::WebPDecoder::new(Cursor::new(simple)).map_err(webp_error)?;
    let geometry = decoder.dimensions();
    dimensions(geometry.0, geometry.1)?;
    Ok((geometry, decoder.has_alpha()))
}

/// Fixed-width container integers are bounded by their validated slice.
fn read32(bytes: &[u8]) -> u32 {
    u32::from_le_bytes(bytes.try_into().expect("bounded RIFF integer"))
}

/// VP8X stores canvas axes minus one as 24-bit little-endian integers.
fn read24(bytes: &[u8]) -> u32 {
    bytes[0] as u32 | (bytes[1] as u32) << 8 | (bytes[2] as u32) << 16
}

/// Best-effort codec allocation errors must remain infrastructure unavailability.
fn webp_error(error: image_webp::DecodingError) -> Failure {
    match error {
        image_webp::DecodingError::MemoryLimitExceeded => Failure::Unavailable,
        _ => Failure::Source,
    }
}
