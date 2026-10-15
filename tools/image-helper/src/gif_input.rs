//! GIF container events and a configured full LZW decoder establish independent authority.
use crate::{
    Failure, Result, color, dimensions,
    raster::{Raster, Samples},
    zeroed,
};
use gif::streaming_decoder::{Block, Decoded, OutputBuffer, StreamingDecoder};
use image::{ImageFormat, metadata::Orientation};
use moxcms::Layout;
use std::{io::Cursor, num::NonZeroU64};

/// Strict public frame decoding also supplies deinterlaced palette indices for rendering.
pub(crate) fn decode(bytes: &[u8]) -> Result<Raster> {
    let profile = admit(bytes)?
        .as_deref()
        .map(color::parse_profile)
        .transpose()?;
    let mut options = gif::DecodeOptions::new();
    options.check_frame_consistency(true);
    options.check_lzw_end_code(true);
    options.skip_frame_decoding(false);
    options.allow_unknown_blocks(false);
    options.set_color_output(gif::ColorOutput::Indexed);
    options.set_memory_limit(gif::MemoryLimit::Bytes(
        NonZeroU64::new(64 * 1024 * 1024).unwrap(),
    ));
    let mut reader = options.read_info(Cursor::new(bytes)).map_err(gif_error)?;
    let (width, height) = (u32::from(reader.width()), u32::from(reader.height()));
    dimensions(width, height)?;
    let global = reader.global_palette().map(<[u8]>::to_vec);
    let frame = reader
        .read_next_frame()
        .map_err(gif_error)?
        .ok_or(Failure::Source)?;
    let palette = frame
        .palette
        .as_deref()
        .or(global.as_deref())
        .ok_or(Failure::Source)?;
    let mut samples = zeroed(width as usize * height as usize * 4)?;
    for (index, &value) in frame.buffer.iter().enumerate() {
        let x = frame.left as usize + index % frame.width as usize;
        let y = frame.top as usize + index / frame.width as usize;
        let pixel = &mut samples[(y * width as usize + x) * 4..][..4];
        let color = palette
            .get(value as usize * 3..)
            .and_then(|color| color.get(..3))
            .ok_or(Failure::Source)?;
        pixel[..3].copy_from_slice(color);
        pixel[3] = if frame.transparent == Some(value) {
            0
        } else {
            255
        };
    }
    // read_next_frame stops when the raster is full. Keep supplying a real output sink
    // until DataEnd, so the enabled LZW end-code check cannot be bypassed by a full buffer.
    let mut sink = [0u8; 8192];
    if reader.fill_buffer(&mut sink).map_err(gif_error)? {
        return Err(Failure::Source);
    }
    Ok(Raster {
        width,
        height,
        samples: Samples::Eight(samples),
        layout: Layout::Rgba,
        profile,
        cmyk: false,
        orientation: Orientation::NoTransforms,
        format: ImageFormat::Gif,
    })
}

/// Default streaming events expose all declarations; strict LZW termination is checked
/// by the configured public Decoder above. The streaming constructor has no public options.
fn admit(bytes: &[u8]) -> Result<Option<Vec<u8>>> {
    let mut decoder = StreamingDecoder::new();
    let mut offset = 0;
    let mut frames = 0;
    let mut count = 0usize;
    let mut expected = 0usize;
    let mut ended = false;
    let mut palette = None;
    let mut frame_palette = 0;
    let mut sink = [0u8; 8192];
    let mut extension_start = false;
    let mut collecting_icc = false;
    let mut icc: Option<Vec<u8>> = None;
    while offset < bytes.len() {
        let (consumed, event) = decoder
            .update(&bytes[offset..], &mut OutputBuffer::Slice(&mut sink))
            .map_err(gif_error)?;
        offset += consumed;
        let transition = !matches!(event, Decoded::Nothing);
        match event {
            Decoded::GlobalPalette(colors) => palette = Some(colors),
            Decoded::HeaderEnd => {
                dimensions(decoder.width().into(), decoder.height().into())?;
            }
            Decoded::BlockStart(Block::Extension) => {
                extension_start = true;
                collecting_icc = false;
            }
            Decoded::SubBlock { ext, is_last }
                if ext.into_known() == Some(gif::Extension::Application) =>
            {
                let data = decoder.last_ext_sub_block();
                if extension_start {
                    if matches!(data, b"NETSCAPE2.0" | b"ANIMEXTS1.0") {
                        return Err(Failure::Source);
                    }
                    collecting_icc = data == b"ICCRGBG1012";
                    if collecting_icc {
                        if icc.is_some() {
                            return Err(Failure::Source);
                        }
                        icc = Some(Vec::new());
                    }
                    extension_start = false;
                } else if collecting_icc {
                    let profile = icc.as_mut().ok_or(Failure::Source)?;
                    if profile.len() + data.len() >= 4 * 1024 * 1024 {
                        return Err(Failure::Source);
                    }
                    profile
                        .try_reserve(data.len())
                        .map_err(|_| Failure::Unavailable)?;
                    profile.extend_from_slice(data);
                }
                if is_last {
                    collecting_icc = false;
                }
            }
            Decoded::FrameMetadata(_) => {
                frames += 1;
                if frames > 1 {
                    return Err(Failure::TooLarge);
                }
                let frame = decoder.current_frame();
                expected = dimensions(frame.width.into(), frame.height.into())?;
                if u32::from(frame.left) + u32::from(frame.width) > u32::from(decoder.width())
                    || u32::from(frame.top) + u32::from(frame.height) > u32::from(decoder.height())
                {
                    return Err(Failure::Source);
                }
                frame_palette = frame
                    .palette
                    .as_deref()
                    .or(palette.as_deref())
                    .ok_or(Failure::Source)?
                    .len()
                    / 3;
            }
            Decoded::BytesDecoded(length) => {
                count = count.checked_add(length.get()).ok_or(Failure::Source)?;
                if count > expected
                    || sink[..length.get()]
                        .iter()
                        .any(|&value| value as usize >= frame_palette)
                {
                    return Err(Failure::Source);
                }
            }
            Decoded::DataEnd => {
                if count != expected {
                    return Err(Failure::Source);
                }
                ended = true;
            }
            Decoded::BlockStart(Block::Trailer) => {
                if frames != 1 || !ended || offset != bytes.len() {
                    return Err(Failure::Source);
                }
                return Ok(icc);
            }
            _ => {}
        }
        if consumed == 0 && !transition {
            return Err(Failure::Source);
        }
    }
    Err(Failure::Source)
}

/// Library allocation failures cannot positively establish a source size violation.
fn gif_error(error: gif::DecodingError) -> Failure {
    match error {
        gif::DecodingError::MemoryLimit | gif::DecodingError::OutOfMemory => Failure::Unavailable,
        _ => Failure::Source,
    }
}
