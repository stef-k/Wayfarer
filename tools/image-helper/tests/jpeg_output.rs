//! The normalized JPEG product seam retains canonical color and bounded, complete odd-sized output.
use image::{DynamicImage, ImageFormat, Rgb, RgbImage};
use std::io::Cursor;
use wayfarer_image::{Failure, MAX_ENCODED, OutputFormat, Recipe, transform};

/// Exercise both photographic profiles and a higher-quality override through the complete transform.
#[test]
fn jpeg_profiles_preserve_icc_geometry_privacy_and_output_limit() {
    let pixels = RgbImage::from_fn(17, 19, |x, y| {
        Rgb([(x * 13) as u8, (y * 11) as u8, ((x + y) * 7) as u8])
    });
    let mut source = Cursor::new(Vec::new());
    DynamicImage::ImageRgb8(pixels)
        .write_to(&mut source, ImageFormat::Png)
        .unwrap();
    let canonical = moxcms::ColorProfile::new_srgb().encode().unwrap();
    for quality in [85, 90, 95] {
        let mut recipe = Recipe {
            width: 2048,
            height: 2048,
            quality,
            output: OutputFormat::Jpeg,
            max_input: MAX_ENCODED,
            max_output: MAX_ENCODED,
        };
        let encoded = transform(source.get_ref(), &recipe).unwrap();
        #[cfg(feature = "native-jpeg-encode")]
        assert_eq!(
            turbojpeg::read_header(&encoded).unwrap().subsamp,
            if quality <= 90 {
                turbojpeg::Subsamp::Sub2x2
            } else {
                turbojpeg::Subsamp::None
            }
        );
        recipe.max_output = encoded.len();
        // The native entropy writer can conservatively refuse a full destination before EOI.
        #[cfg(not(feature = "native-jpeg-encode"))]
        assert!(transform(source.get_ref(), &recipe).is_ok());
        #[cfg(feature = "native-jpeg-encode")]
        assert_eq!(
            transform(source.get_ref(), &recipe),
            Err(Failure::Unavailable)
        );
        recipe.max_output -= 1;
        assert_eq!(
            transform(source.get_ref(), &recipe),
            Err(Failure::Unavailable)
        );
        let mut decoder = jpeg_decoder::Decoder::new(Cursor::new(encoded));
        decoder.decode().unwrap();
        let info = decoder.info().unwrap();
        assert_eq!((info.width, info.height), (17, 19));
        let profile = decoder.icc_profile().unwrap();
        assert_eq!(profile.len(), canonical.len());
        // The retained CMS writer timestamps ICC headers; compare every other canonical byte.
        assert_eq!(&profile[..24], &canonical[..24]);
        assert_eq!(&profile[36..], &canonical[36..]);
        assert!(decoder.exif_data().is_none());
        assert!(decoder.xmp_data().is_none());
        recipe.max_output = 100;
        assert_eq!(
            transform(source.get_ref(), &recipe),
            Err(Failure::Unavailable)
        );
    }
}

/// The requested pure Rust route reports full encoded size only after buffering it internally.
#[cfg(feature = "codec-bench")]
#[test]
fn pure_rust_compress_into_is_a_late_size_check_without_icc() {
    use libjpeg_turbo_rs::{JpegError, PixelFormat, Subsampling, compress_into};
    let pixels = vec![127; 17 * 19 * 3];
    let mut canary = [0xa5; 64];
    let error = compress_into(
        &mut canary[16..32],
        &pixels,
        17,
        19,
        PixelFormat::Rgb,
        90,
        Subsampling::S420,
    )
    .unwrap_err();
    let needed = match error {
        JpegError::BufferTooSmall { need, got: 16 } => need,
        _ => panic!("expected a late full-size refusal"),
    };
    assert!(needed > 16);
    assert_eq!(canary, [0xa5; 64]);
    let mut exact = vec![0; needed];
    assert_eq!(
        compress_into(
            &mut exact,
            &pixels,
            17,
            19,
            PixelFormat::Rgb,
            90,
            Subsampling::S420
        )
        .unwrap(),
        needed
    );
    let mut decoder = jpeg_decoder::Decoder::new(Cursor::new(exact));
    decoder.decode().unwrap();
    assert!(decoder.icc_profile().is_none());
}

/// Borrowed native buffers refuse overflow without touching canaries, then the same handle recovers.
#[cfg(feature = "native-jpeg-encode")]
#[test]
fn native_borrowed_slice_refuses_actual_overflow_and_recovers() {
    let pixels = vec![127; 17 * 19 * 3];
    let image = turbojpeg::Image {
        pixels: pixels.as_slice(),
        width: 17,
        pitch: 51,
        height: 19,
        format: turbojpeg::PixelFormat::RGB,
    };
    let mut compressor = turbojpeg::Compressor::new().unwrap();
    compressor.set_quality(90).unwrap();
    compressor.set_subsamp(turbojpeg::Subsamp::Sub2x2).unwrap();
    let mut canary = [0xa5; 64];
    assert!(
        compressor
            .compress_to_slice(image, &mut canary[16..32])
            .is_err()
    );
    assert_eq!(&canary[..16], &[0xa5; 16]);
    assert_eq!(&canary[32..], &[0xa5; 32]);
    let mut output = vec![0; compressor.buf_len(17, 19).unwrap()];
    let length = compressor.compress_to_slice(image, &mut output).unwrap();
    let mut exact = vec![0; length];
    assert!(compressor.compress_to_slice(image, &mut exact).is_err());
    // Refusal is conservative: a full entropy destination is an error even if final bytes fit.
    assert_eq!(
        compressor.compress_to_slice(image, &mut output).unwrap(),
        length
    );
    output.truncate(length);
    assert_eq!(
        turbojpeg::read_header(&output).unwrap().subsamp,
        turbojpeg::Subsamp::Sub2x2
    );
}
