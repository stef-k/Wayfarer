//! Finite codec-only observations on retained admitted fixtures, never a production path.
use image::ImageDecoder;
use std::{error::Error, hint::black_box, io::Cursor, path::Path, time::Instant};
use wayfarer_image::{MAX_ENCODED, OutputFormat, Recipe};

type Result<T> = std::result::Result<T, Box<dyn Error>>;

/// Read only explicit local paths; emit timing JSON and save a single completed sample.
fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        Some("prepare") if args.len() == 6 => prepare(&args),
        Some("decode") if args.len() == 5 => measure_decode(&args),
        Some("encode") if args.len() == 9 => measure_encode(&args),
        _ => Err("usage: prepare INPUT OUTPUT W H Q | decode CODEC INPUT OUTPUT COUNT | encode CODEC RAW OUTPUT W H Q COUNT CHANNELS".into()),
    }
}

/// Capture pixels after the existing pipeline's admission, color, rotation and resize.
fn prepare(args: &[String]) -> Result<()> {
    let recipe = Recipe {
        width: args[3].parse()?,
        height: args[4].parse()?,
        quality: args[5].parse()?,
        output: OutputFormat::Auto,
        max_input: MAX_ENCODED,
        max_output: MAX_ENCODED,
    };
    let pixels = wayfarer_image::benchmark_pixels(&std::fs::read(&args[1])?, &recipe)
        .map_err(|failure| failure.diagnostic())?;
    let rgb = pixels.into_rgb8();
    std::fs::write(&args[2], rgb.as_raw())?;
    println!(
        "{{\"width\":{},\"height\":{},\"channels\":3}}",
        rgb.width(),
        rgb.height()
    );
    Ok(())
}

/// Compare owned packed RGB/RGBA buffers; source read and sample writes are untimed.
fn measure_decode(args: &[String]) -> Result<()> {
    let input = std::fs::read(&args[2])?;
    let codec = &args[1];
    observe(codec, &args[3], args[4].parse()?, || decode(codec, &input))
}

/// Decode without normalization or resizing, using each exact pinned public API.
fn decode(codec: &str, bytes: &[u8]) -> Result<Vec<u8>> {
    match codec {
        "jpeg-decoder" => Ok(jpeg_decoder::Decoder::new(Cursor::new(bytes)).decode()?),
        "zune-jpeg" => {
            let decoder = image::codecs::jpeg::JpegDecoder::new(Cursor::new(bytes))?;
            let mut pixels = vec![0; decoder.total_bytes() as usize];
            decoder.read_image(&mut pixels)?;
            Ok(pixels)
        }
        "libjpeg-turbo-rs" => {
            let decoder = libjpeg_turbo_rs::Decoder::new(bytes)?
                .with_output_format(libjpeg_turbo_rs::PixelFormat::Rgb)
                .with_max_pixels(12_000_000)
                .with_max_memory(128 * 1024 * 1024);
            Ok(decoder.decode_image()?.data)
        }
        "image-webp" => {
            let mut decoder = image_webp::WebPDecoder::new(Cursor::new(bytes))?;
            decoder.set_memory_limit(128 * 1024 * 1024);
            let mut pixels = vec![0; decoder.output_buffer_size().ok_or("buffer size")?];
            decoder.read_image(&mut pixels)?;
            Ok(pixels)
        }
        "libwebp" => Ok(webp::Decoder::new(bytes)
            .decode()
            .ok_or("decode failed")?
            .to_vec()),
        _ => Err("unknown decode codec".into()),
    }
}

/// Encode identical prepared pixels with explicit dimensions, quality and canonical ICC.
fn measure_encode(args: &[String]) -> Result<()> {
    let input = std::fs::read(&args[2])?;
    let width: u32 = args[4].parse()?;
    let height: u32 = args[5].parse()?;
    let quality: u8 = args[6].parse()?;
    let channels: usize = args[8].parse()?;
    if width == 0 || height == 0 || input.len() != width as usize * height as usize * channels {
        return Err("invalid admitted pixel buffer".into());
    }
    let profile = moxcms::ColorProfile::new_srgb().encode()?;
    observe(&args[1], &args[3], args[7].parse()?, || {
        encode(&args[1], &input, width, height, quality, channels, &profile)
    })
}

/// ICC embedding is included for JPEG; raw WebP codecs cannot embed ICC and are marked separately.
fn encode(
    codec: &str,
    pixels: &[u8],
    width: u32,
    height: u32,
    quality: u8,
    channels: usize,
    profile: &[u8],
) -> Result<Vec<u8>> {
    match codec {
        "jpeg-encoder" if channels == 3 => {
            let mut output = Vec::new();
            let mut encoder = jpeg_encoder::Encoder::new(&mut output, quality);
            encoder.set_sampling_factor(jpeg_encoder::SamplingFactor::F_2_2);
            encoder.set_chroma_subsampling_method(jpeg_encoder::ChromaSubsamplingMethod::Average);
            encoder.add_icc_profile(profile)?;
            encoder.encode(
                pixels,
                width as u16,
                height as u16,
                jpeg_encoder::ColorType::Rgb,
            )?;
            Ok(output)
        }
        "libjpeg-turbo-rs" if channels == 3 => Ok(libjpeg_turbo_rs::Encoder::new(
            pixels,
            width as usize,
            height as usize,
            libjpeg_turbo_rs::PixelFormat::Rgb,
        )
        .quality(quality)
        .subsampling(libjpeg_turbo_rs::Subsampling::S420)
        .fancy_downsampling(false)
        .optimize_huffman(false)
        .icc_profile(profile)
        .encode()?),
        "image-webp-lossless" => {
            let mut output = Vec::new();
            let color = if channels == 4 {
                image::ExtendedColorType::Rgba8
            } else {
                image::ExtendedColorType::Rgb8
            };
            image::codecs::webp::WebPEncoder::new_lossless(&mut output)
                .encode(pixels, width, height, color)?;
            Ok(output)
        }
        "libwebp-lossy" | "libwebp-lossless" => {
            let encoder = if channels == 4 {
                webp::Encoder::from_rgba(pixels, width, height)
            } else {
                webp::Encoder::from_rgb(pixels, width, height)
            };
            Ok(encoder
                .encode_simple(codec == "libwebp-lossless", quality as f32)
                .map_err(|_| "WebP encoding failed")?
                .to_vec())
        }
        _ => Err("unknown encode codec or channel layout".into()),
    }
}

/// Three warmups and finite measured samples exclude timer-adjacent resource inspection.
fn observe(
    codec: &str,
    destination: &str,
    count: usize,
    mut operation: impl FnMut() -> Result<Vec<u8>>,
) -> Result<()> {
    if !(1..=40).contains(&count) {
        return Err("finite observation count required".into());
    }
    for iteration in -3..count as i32 {
        let before = usage();
        let timer = Instant::now();
        let output = black_box(operation()?);
        let elapsed = timer.elapsed().as_secs_f64() * 1000.0;
        let after = usage();
        println!(
            "{{\"codec\":\"{codec}\",\"iteration\":{iteration},\"milliseconds\":{elapsed},\"cpu_ms\":{},\"process_peak_rss_kib\":{},\"bytes\":{}}}",
            after.0 - before.0,
            after.1,
            output.len()
        );
        if iteration == 0 {
            std::fs::write(Path::new(destination), &output)?;
        }
    }
    Ok(())
}

/// Process CPU and current-executable VmHWM are observations, not hard allocation bounds.
/// ru_maxrss retains pre-exec launcher high water, so it cannot attribute codec RSS.
fn usage() -> (f64, i64) {
    // SAFETY: getrusage fills one initialized output; no pointers survive this call.
    unsafe {
        let mut usage: libc::rusage = std::mem::zeroed();
        assert_eq!(libc::getrusage(libc::RUSAGE_SELF, &mut usage), 0);
        let seconds = usage.ru_utime.tv_sec + usage.ru_stime.tv_sec;
        let micros = usage.ru_utime.tv_usec + usage.ru_stime.tv_usec;
        let cpu = seconds as f64 * 1000.0 + micros as f64 / 1000.0;
        let status = std::fs::read_to_string("/proc/self/status").expect("Linux observation");
        let highwater = status
            .lines()
            .find(|line| line.starts_with("VmHWM:"))
            .and_then(|line| line.split_whitespace().nth(1))
            .and_then(|value| value.parse().ok())
            .expect("current executable high water");
        (cpu, highwater)
    }
}
