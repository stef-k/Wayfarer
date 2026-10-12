//! Explicit decoder routes preserve color channels and metadata until normalization.
use crate::{Failure, Result, color, dimensions, png_input, zeroed};
use image::{ImageFormat, metadata::Orientation};
use moxcms::{ColorProfile, Layout};
use std::io::Cursor;

/// Preserve 16-bit source samples through color conversion.
pub(crate) enum Samples {
    Eight(Vec<u8>),
    Sixteen(Vec<u16>),
}

/// Exactly one admitted raster, with straight color channels and optional alpha.
pub(crate) struct Raster {
    pub width: u32,
    pub height: u32,
    pub samples: Samples,
    pub layout: Layout,
    pub profile: Option<ColorProfile>,
    pub cmyk: bool,
    pub orientation: Orientation,
    pub format: ImageFormat,
}

/// Byte-derived format selection prevents extension/MIME authority.
pub(crate) fn decode(bytes: &[u8]) -> Result<Raster> {
    match image::guess_format(bytes).map_err(|_| Failure::Source)? {
        ImageFormat::Jpeg => jpeg(bytes),
        ImageFormat::Png => png(bytes),
        ImageFormat::Gif => crate::gif_input::decode(bytes),
        ImageFormat::WebP => crate::webp_input::decode(bytes),
        _ => Err(Failure::Source),
    }
}

/// The JPEG-only recovery policy accepts a successful bounded decode, never raw bytes.
fn jpeg(bytes: &[u8]) -> Result<Raster> {
    let mut decoder = jpeg_decoder::Decoder::new(Cursor::new(bytes));
    decoder.read_info().map_err(|_| Failure::Source)?;
    let info = decoder.info().ok_or(Failure::Source)?;
    dimensions(info.width.into(), info.height.into())?;
    let decoded = decoder.decode().map_err(|_| Failure::Source)?;
    let (layout, cmyk) = match info.pixel_format {
        jpeg_decoder::PixelFormat::RGB24 => (Layout::Rgb, false),
        jpeg_decoder::PixelFormat::L8 => (Layout::Gray, false),
        jpeg_decoder::PixelFormat::CMYK32 => (Layout::Rgba, true),
        // This API does not expose lossless JPEG sample precision for normalization.
        jpeg_decoder::PixelFormat::L16 => return Err(Failure::Source),
    };
    // Absent and malformed APP2 assemblies both become untagged by the agreed policy.
    let profile = decoder
        .icc_profile()
        .as_deref()
        .map(color::parse_profile)
        .transpose()?;
    Ok(Raster {
        width: info.width.into(),
        height: info.height.into(),
        samples: Samples::Eight(decoded),
        layout,
        cmyk,
        profile,
        orientation: orientation(decoder.exif_data()),
        format: ImageFormat::Jpeg,
    })
}

/// Admission scratch is dropped before allocating the actual raster.
fn png(bytes: &[u8]) -> Result<Raster> {
    png_input::admit(bytes)?;
    let mut decoder = png::Decoder::new_with_options(Cursor::new(bytes), png_input::options());
    decoder.set_limits(png::Limits {
        bytes: 128 * 1024 * 1024,
    });
    decoder.set_transformations(png::Transformations::EXPAND);
    let mut reader = decoder.read_info().map_err(png_input::png_error)?;
    let mut decoded = zeroed(reader.output_buffer_size().ok_or(Failure::Unavailable)?)?;
    let output = reader
        .next_frame(&mut decoded)
        .map_err(png_input::png_error)?;
    decoded.truncate(output.buffer_size());
    reader.finish().map_err(png_input::png_error)?;
    let info = reader.info();
    let layout = match output.color_type {
        png::ColorType::Grayscale => Layout::Gray,
        png::ColorType::GrayscaleAlpha => Layout::GrayAlpha,
        png::ColorType::Rgb => Layout::Rgb,
        png::ColorType::Rgba => Layout::Rgba,
        png::ColorType::Indexed => return Err(Failure::Unavailable),
    };
    let samples = if output.bit_depth == png::BitDepth::Sixteen {
        let mut values = zeroed(decoded.len() / 2)?;
        for (value, pair) in values.iter_mut().zip(decoded.chunks_exact(2)) {
            *value = u16::from_be_bytes([pair[0], pair[1]]);
        }
        Samples::Sixteen(values)
    } else {
        Samples::Eight(decoded)
    };
    Ok(Raster {
        width: output.width,
        height: output.height,
        samples,
        layout,
        cmyk: false,
        profile: color::png_profile(info, layout)?,
        orientation: orientation(info.exif_metadata.as_deref()),
        format: ImageFormat::Png,
    })
}

/// The maintained EXIF reader accepts a raw TIFF header; WebP may carry an Exif prefix.
pub(crate) fn orientation(exif: Option<&[u8]>) -> Orientation {
    exif.and_then(|bytes| {
        Orientation::from_exif_chunk(bytes.strip_prefix(b"Exif\0\0").unwrap_or(bytes))
    })
    .unwrap_or(Orientation::NoTransforms)
}
