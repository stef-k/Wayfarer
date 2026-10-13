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
        recipe.max_output = 100;
        assert_eq!(
            transform(source.get_ref(), &recipe),
            Err(Failure::Unavailable)
        );
    }
}
