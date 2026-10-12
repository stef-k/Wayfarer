//! Decoder-driven PNG admission, including the accepted immutable E+1 completion witness.
use crate::{Failure, Result, dimensions, zeroed};
use png::{Decoded, Info, StreamingDecoder, UnfilterRegion};

/// Use checksum-enabled ordinary builds for both admission and raster decoding.
pub(crate) fn options() -> png::DecodeOptions {
    let mut options = png::DecodeOptions::default();
    options.set_ignore_checksums(false);
    options.set_skip_ancillary_crc_failures(false);
    options.set_ignore_text_chunk(true);
    options
}

/// Calculate filtered bytes from original samples, including only nonempty Adam7 passes.
fn filtered_length(info: &Info<'_>) -> Result<usize> {
    dimensions(info.width, info.height)?;
    let passes = [
        (0, 0, 8, 8),
        (4, 0, 8, 8),
        (0, 4, 4, 8),
        (2, 0, 4, 4),
        (0, 2, 2, 4),
        (1, 0, 2, 2),
        (0, 1, 1, 2),
    ];
    let mut total = 0usize;
    for (x, y, dx, dy) in if info.interlaced {
        &passes[..]
    } else {
        &[(0, 0, 1, 1)][..]
    } {
        let width = info.width.saturating_sub(*x).div_ceil(*dx);
        let height = info.height.saturating_sub(*y).div_ceil(*dy);
        if width != 0 && height != 0 {
            let pass = info
                .raw_row_length_from_width(width)
                .checked_mul(height as usize)
                .ok_or(Failure::TooLarge)?;
            total = total.checked_add(pass).ok_or(Failure::TooLarge)?;
        }
    }
    Ok(total)
}

/// Consume every chunk with CRC checks, fully inflate, and require IEND at exact EOF.
/// The scratch buffer and its region are never reset or compacted during admission.
pub(crate) fn admit(bytes: &[u8]) -> Result<()> {
    let mut decoder = StreamingDecoder::new_with_options(options());
    let mut scratch = Vec::new();
    let mut region = UnfilterRegion::default();
    let mut expected = None;
    let mut offset = 0;
    let mut flushed = false;
    let mut empty_idat_transition = false;
    let mut iccp = false;
    while offset < bytes.len() {
        let before = region.filled;
        let (consumed, event) = decoder
            .update(&bytes[offset..], Some(&mut region.as_buf(&mut scratch)))
            .map_err(png_error)?;
        offset = offset.checked_add(consumed).ok_or(Failure::Source)?;
        let mut permitted_transition = false;
        match event {
            Decoded::ChunkBegin(length, kind) => match &kind.0 {
                b"acTL" | b"fcTL" | b"fdAT" => return Err(Failure::Source),
                b"iCCP" => iccp = true,
                b"IDAT" => empty_idat_transition = length == 0,
                _ => {}
            },
            Decoded::ChunkComplete(kind) if &kind.0 == b"IHDR" => {
                let length = filtered_length(decoder.info().ok_or(Failure::Source)?)?;
                scratch = zeroed(length.checked_add(1).ok_or(Failure::TooLarge)?)?;
                expected = Some(length);
            }
            Decoded::ImageData if empty_idat_transition && consumed == 0 => {
                empty_idat_transition = false;
                permitted_transition = true;
            }
            Decoded::ImageDataFlushed => {
                if flushed || expected != Some(region.filled) {
                    return Err(Failure::Source);
                }
                flushed = true;
                permitted_transition = true;
            }
            Decoded::BadAncillaryChunk(_) => return Err(Failure::Source),
            Decoded::ChunkComplete(kind) if &kind.0 == b"IEND" => {
                let info = decoder.info().ok_or(Failure::Source)?;
                if !flushed || offset != bytes.len() || (iccp && info.icc_profile.is_none()) {
                    return Err(Failure::Source);
                }
                return Ok(());
            }
            _ => {}
        }
        if expected.is_some_and(|length| region.filled > length) {
            return Err(Failure::Source);
        }
        if consumed == 0 && region.filled == before && !permitted_transition {
            return Err(Failure::Source);
        }
    }
    Err(Failure::Source)
}

/// Decoder allocation and API failures are not positive source-limit evidence.
pub(crate) fn png_error(error: png::DecodingError) -> Failure {
    match error {
        png::DecodingError::LimitsExceeded | png::DecodingError::Parameter(_) => {
            Failure::Unavailable
        }
        _ => Failure::Source,
    }
}
