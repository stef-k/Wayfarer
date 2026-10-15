//! Focused compatibility contracts for the opt-in scratch decoder substitutions.
#![cfg(any(feature = "jpeg-candidate-decode", feature = "webp-candidate-decode"))]
use wayfarer_image::{Failure, MAX_ENCODED, OutputFormat, Recipe, transform};

/// Reuse only the retained local corpus; no fixture generation or host mutation.
fn fixture(name: &str) -> Vec<u8> {
    let root = std::env::var("WAYFARER_IMAGE_FIXTURES").expect("retained fixture directory");
    std::fs::read(std::path::Path::new(&root).join(name)).unwrap()
}

/// A complete recipe exercises the actual normalization and derivative verification seam.
fn recipe() -> Recipe {
    Recipe {
        width: 600,
        height: 8192,
        quality: 85,
        output: OutputFormat::Auto,
        max_input: MAX_ENCODED,
        max_output: MAX_ENCODED,
    }
}

/// Preserve prior truncated-JPEG rejection and the previously successful bounded recovery control.
#[cfg(feature = "jpeg-candidate-decode")]
#[test]
fn jpeg_recovery_preserves_rejection_and_completed_output() {
    for name in [
        "jpeg-without-eoi.jpg",
        "jpeg-truncated-entropy.jpg",
        "truncated.jpg",
    ] {
        assert_eq!(
            transform(&fixture(name), &recipe()),
            Err(Failure::Source),
            "{name}"
        );
    }
    let recovered = transform(&fixture("jpeg-truncated-entropy-with-eoi.jpg"), &recipe()).unwrap();
    image::load_from_memory(&recovered).unwrap();
}

/// Both WebP routes preserve alpha and reject malformed payloads and container controls.
#[test]
fn webp_preserves_alpha_and_strict_admission() {
    let source = fixture("static.webp");
    let output = transform(&source, &recipe()).unwrap();
    assert_eq!(
        image::guess_format(&output).unwrap(),
        image::ImageFormat::Png
    );
    let pixels = image::load_from_memory(&output).unwrap().into_rgba8();
    assert!(pixels.pixels().any(|pixel| pixel[3] < 255));
    let mut explicit = recipe();
    explicit.output = OutputFormat::Jpeg;
    assert_eq!(transform(&source, &explicit), Err(Failure::Source));
    for name in [
        "animated.webp",
        "truncated.webp",
        "metadata.webp",
        "upstream-huffman-overflow.webp",
        "huffman-exact-extent.webp",
    ] {
        assert_eq!(
            transform(&fixture(name), &recipe()),
            Err(Failure::Source),
            "{name}"
        );
    }
}
