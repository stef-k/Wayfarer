//! One complete normalized derivative, verified before exposure to stdout.
use crate::{Failure, MAX_ENCODED, Result, color, diagnostic_timing, raster};
use image::{DynamicImage, GenericImageView, ImageEncoder, ImageFormat, imageops::FilterType};
use std::io::{self, Write};
use std::time::Instant;

/// The CLI supports only auto, JPEG and PNG.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum OutputFormat {
    Auto,
    Jpeg,
    Png,
}

/// Every caller supplies the complete output recipe and both encoded-byte ceilings.
pub struct Recipe {
    pub width: u32,
    pub height: u32,
    pub quality: u8,
    pub output: OutputFormat,
    pub max_input: usize,
    pub max_output: usize,
}

impl Recipe {
    /// Invalid trusted arguments are infrastructure failures to the supervisor.
    pub fn validate(&self) -> Result<()> {
        if self.width == 0
            || self.height == 0
            || self.width > 8192
            || self.height > 8192
            || self.quality == 0
            || self.quality > 100
            || self.max_input == 0
            || self.max_input > MAX_ENCODED
            || self.max_output == 0
            || self.max_output > MAX_ENCODED
        {
            return Err(Failure::Arguments);
        }
        Ok(())
    }
}

/// Decode, normalize color/orientation, resize without enlargement, encode and verify.
pub fn transform(bytes: &[u8], recipe: &Recipe) -> Result<Vec<u8>> {
    recipe.validate()?;
    if bytes.len() > recipe.max_input {
        return Err(Failure::TooLarge);
    }
    let stage = Instant::now();
    let mut raster = raster::decode(bytes)?;
    diagnostic_timing("source_decode", stage);
    let source_format = raster.format;
    let stage = Instant::now();
    let mut oriented = match color::opaque_rgb8(&mut raster)? {
        Some(rgb) => DynamicImage::ImageRgb8(rgb),
        None => DynamicImage::ImageRgba32F(color::to_srgb(&raster)?),
    };
    diagnostic_timing("color_prepare", stage);
    let stage = Instant::now();
    oriented.apply_orientation(raster.orientation);
    drop(raster);
    diagnostic_timing("orientation_drop", stage);
    let transparent = oriented
        .as_rgba32f()
        .is_some_and(|image| image.pixels().any(|pixel| pixel[3] < 1.0));
    let output_format = match recipe.output {
        OutputFormat::Auto
            if matches!(source_format, ImageFormat::Png | ImageFormat::Gif) || transparent =>
        {
            ImageFormat::Png
        }
        OutputFormat::Auto | OutputFormat::Jpeg => ImageFormat::Jpeg,
        OutputFormat::Png => ImageFormat::Png,
    };
    if output_format == ImageFormat::Jpeg && transparent {
        return Err(Failure::Source);
    }
    let scale = (recipe.width as f64 / oriented.width() as f64)
        .min(recipe.height as f64 / oriented.height() as f64)
        .min(1.0);
    // Truncate fractional axes, matching the existing proxy's bounding-box geometry.
    let width = ((oriented.width() as f64 * scale) as u32).max(1);
    let height = ((oriented.height() as f64 * scale) as u32).max(1);
    let normalized = resize(oriented, width, height)?;
    let stage = Instant::now();
    let encoded = encode(normalized, output_format, recipe)?;
    diagnostic_timing("encode", stage);
    let stage = Instant::now();
    verify(&encoded, output_format, width, height)?;
    diagnostic_timing("output_verify", stage);
    Ok(encoded)
}

/// Conventional Lanczos3 sRGB filtering for opaque bytes; keep precise premultiplied filtering otherwise.
fn resize(pixels: DynamicImage, width: u32, height: u32) -> Result<DynamicImage> {
    match pixels {
        DynamicImage::ImageRgb8(rgb) => {
            let stage = Instant::now();
            let resized = if (width, height) == rgb.dimensions() {
                rgb
            } else {
                image::imageops::resize(&rgb, width, height, FilterType::Lanczos3)
            };
            diagnostic_timing("resize_srgb8", stage);
            Ok(DynamicImage::ImageRgb8(resized))
        }
        DynamicImage::ImageRgba32F(mut linear) => {
            let stage = Instant::now();
            color::premultiply(&mut linear);
            diagnostic_timing("linear_premultiply", stage);
            let stage = Instant::now();
            if (width, height) != linear.dimensions() {
                linear = image::imageops::resize(&linear, width, height, FilterType::Lanczos3);
            }
            diagnostic_timing("resize_linear", stage);
            let stage = Instant::now();
            let rgba = color::quantize(linear)?;
            diagnostic_timing("quantize", stage);
            Ok(DynamicImage::ImageRgba8(rgba))
        }
        _ => Err(Failure::Unavailable),
    }
}

/// Fresh encoders receive only a generated canonical sRGB profile.
fn encode(pixels: DynamicImage, format: ImageFormat, recipe: &Recipe) -> Result<Vec<u8>> {
    let profile = moxcms::ColorProfile::new_srgb()
        .encode()
        .map_err(|_| Failure::Unavailable)?;
    let mut output = BoundedOutput {
        bytes: Vec::new(),
        limit: recipe.max_output,
    };
    let (width, height) = pixels.dimensions();
    if format == ImageFormat::Jpeg {
        let rgb = pixels.into_rgb8();
        let mut encoder =
            image::codecs::jpeg::JpegEncoder::new_with_quality(&mut output, recipe.quality);
        encoder
            .set_icc_profile(profile)
            .map_err(|_| Failure::Unavailable)?;
        encoder
            .write_image(rgb.as_raw(), width, height, image::ExtendedColorType::Rgb8)
            .map_err(|_| Failure::Unavailable)?;
    } else {
        let mut encoder = image::codecs::png::PngEncoder::new_with_quality(
            &mut output,
            image::codecs::png::CompressionType::Fast,
            image::codecs::png::FilterType::Adaptive,
        );
        encoder
            .set_icc_profile(profile)
            .map_err(|_| Failure::Unavailable)?;
        encoder
            .write_image(pixels.as_bytes(), width, height, pixels.color().into())
            .map_err(|_| Failure::Unavailable)?;
    }
    Ok(output.bytes)
}

/// Verify full derivative decoding, exact geometry and a single static canonical format.
fn verify(bytes: &[u8], format: ImageFormat, width: u32, height: u32) -> Result<()> {
    if image::guess_format(bytes).ok() != Some(format) {
        return Err(Failure::Unavailable);
    }
    let verified = raster::decode(bytes).map_err(|_| Failure::Unavailable)?;
    if (verified.width, verified.height) != (width, height)
        || verified.orientation != image::metadata::Orientation::NoTransforms
    {
        return Err(Failure::Unavailable);
    }
    Ok(())
}

/// Encoding cannot grow the output past its ceiling; no partial bytes reach stdout.
struct BoundedOutput {
    bytes: Vec<u8>,
    limit: usize,
}

impl Write for BoundedOutput {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if bytes.len() > self.limit.saturating_sub(self.bytes.len()) {
            return Err(io::Error::other("output limit"));
        }
        self.bytes
            .try_reserve(bytes.len())
            .map_err(|_| io::Error::other("allocation"))?;
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
