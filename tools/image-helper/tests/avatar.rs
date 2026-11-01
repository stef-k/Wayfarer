//! Profile photos use strict decode, oriented square crops and metadata-free static WebP.
use wayfarer_image::{AvatarCrop, Failure, avatar};

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!("{}/fixtures/{name}", env!("CARGO_MANIFEST_DIR"))).unwrap()
}

/// Every supported source completes the actual pipeline; output is independently decoded.
#[test]
fn static_photos_are_small_square_and_never_enlarged() {
    for name in [
        "opaque.png",
        "alpha.png",
        "photo.jpg",
        "static.webp",
        "cmyk-tagged.jpg",
        "cmyk-untagged.jpg",
        "gray-icc.png",
        "avatar-gray-icc.jpg",
        "gamma16-linear.png",
    ] {
        let output = avatar(&fixture(name), None).unwrap_or_else(|e| panic!("{name}: {e:?}"));
        let mut decoder = image_webp::WebPDecoder::new(std::io::Cursor::new(&output)).unwrap();
        let (width, height) = decoder.dimensions();
        assert_eq!(width, height, "{name}");
        assert!(width <= 256 && output.len() <= 256 * 1024);
        assert!(!decoder.is_animated());
        assert!(decoder.icc_profile().unwrap().is_none());
        assert!(decoder.exif_metadata().unwrap().is_none());
        assert!(decoder.xmp_metadata().unwrap().is_none());
        let mut pixels = vec![0; decoder.output_buffer_size().unwrap()];
        decoder.read_image(&mut pixels).unwrap();
    }
    let output = avatar(&fixture("orientation-1.png"), None).unwrap();
    let decoder = image_webp::WebPDecoder::new(std::io::Cursor::new(&output)).unwrap();
    assert_eq!(decoder.dimensions(), (32, 32));
}

/// The avatar path must never inherit the proxy's JPEG recovery or animation policy.
#[test]
fn corrupt_and_animated_sources_are_rejected() {
    for name in [
        "jpeg-without-eoi.jpg",
        "jpeg-truncated-entropy.jpg",
        "jpeg-truncated-entropy-with-eoi.jpg",
        "truncated.jpg",
        "truncated.png",
        "png-truncated-zlib.png",
        "png-invalid-filter.png",
        "animated.png",
        "apng-lies-one-frame.png",
        "animated.webp",
        "vp8l-animation-declaration.webp",
        "truncated.webp",
    ] {
        assert_eq!(avatar(&fixture(name), None), Err(Failure::Source), "{name}");
    }
    assert_eq!(
        avatar(&fixture("photo.gif"), None),
        Err(Failure::Unsupported)
    );
}

/// Crop coordinates belong to the oriented source; positive bounds are checked before resizing.
#[test]
fn oriented_crops_validate_and_preserve_mirrors() {
    let source = fixture("orientation-7.png");
    assert_eq!(
        avatar(
            &source,
            Some(AvatarCrop {
                x: 0,
                y: 0,
                size: 999
            })
        ),
        Err(Failure::Arguments)
    );
    let output = avatar(
        &source,
        Some(AvatarCrop {
            x: 0,
            y: 0,
            size: 10,
        }),
    )
    .unwrap();
    let mut decoder = image_webp::WebPDecoder::new(std::io::Cursor::new(output)).unwrap();
    assert_eq!(decoder.dimensions(), (10, 10));
    let mut actual = vec![0; decoder.output_buffer_size().unwrap()];
    decoder.read_image(&mut actual).unwrap();
    let mut expected = image::load_from_memory(&source).unwrap();
    expected.apply_orientation(image::metadata::Orientation::Rotate270FlipH);
    let expected = expected.crop_imm(0, 0, 10, 10).into_rgb8();
    assert!(
        actual
            .chunks_exact(3)
            .zip(expected.pixels())
            .all(|(a, b)| a.iter().zip(b.0).all(|(a, b)| a.abs_diff(b) < 35))
    );
}

/// Encoded bytes are rejected independently of signature, geometry and decoder allocation.
#[test]
fn avatar_encoded_byte_limit_precedes_decode() {
    assert_eq!(
        avatar(&vec![0; 15 * 1024 * 1024 + 1], None),
        Err(Failure::TooLarge)
    );
}
