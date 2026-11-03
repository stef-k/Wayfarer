//! Fixed profile-photo recipe; no inherited metadata reaches the pixel-only encoder.
use crate::{Failure, Result, avatar_color, avatar_input, zeroed};
use fast_image_resize::{PixelType, ResizeOptions, Resizer, images};

/// Optional square in oriented source pixels, validated against server-decoded dimensions.
#[derive(Clone, Copy, Debug)]
pub struct AvatarCrop {
    pub x: u32,
    pub y: u32,
    pub size: u32,
}

/// Strict decode, bounded color conversion, orientation, crop, alpha-aware resize and verification.
pub fn avatar(bytes: &[u8], crop: Option<AvatarCrop>) -> Result<Vec<u8>> {
    if bytes.len() > 15 * 1024 * 1024 {
        return Err(Failure::TooLarge);
    }
    let raster = avatar_input::decode(bytes)?;
    let mut pixels = image::DynamicImage::ImageRgba8(avatar_color::normalize(&raster)?);
    let orientation = raster.orientation;
    drop(raster);
    pixels.apply_orientation(orientation);
    let size = pixels.width().min(pixels.height());
    let crop = crop.unwrap_or(AvatarCrop {
        x: (pixels.width() - size) / 2,
        y: (pixels.height() - size) / 2,
        size,
    });
    if crop.size == 0
        || crop
            .x
            .checked_add(crop.size)
            .is_none_or(|x| x > pixels.width())
        || crop
            .y
            .checked_add(crop.size)
            .is_none_or(|y| y > pixels.height())
    {
        return Err(Failure::Arguments);
    }
    let side = crop.size.min(256);
    let source = images::ImageRef::new(
        pixels.width(),
        pixels.height(),
        pixels.as_bytes(),
        PixelType::U8x4,
    )
    .map_err(|_| Failure::Unavailable)?;
    let mut destination = images::Image::from_vec_u8(
        side,
        side,
        zeroed(side as usize * side as usize * 4)?,
        PixelType::U8x4,
    )
    .map_err(|_| Failure::Unavailable)?;
    // The integer resizer premultiplies alpha and divides it after convolution by default.
    Resizer::new()
        .resize(
            &source,
            &mut destination,
            &ResizeOptions::new().crop(
                crop.x.into(),
                crop.y.into(),
                crop.size.into(),
                crop.size.into(),
            ),
        )
        .map_err(|_| Failure::Unavailable)?;
    drop(pixels);
    let output = encode(destination.buffer(), side)?;
    verify(&output, side)?;
    Ok(output)
}

/// Copy the bounded libwebp allocation once and always release through its matching allocator.
fn encode(pixels: &[u8], side: u32) -> Result<Vec<u8>> {
    let mut pointer = std::ptr::null_mut();
    // SAFETY: tightly packed initialized RGBA8 and positive <=256 axes/stride; output
    // is owned by libwebp until WebPFree, including the oversized/error branches.
    let length = unsafe {
        libwebp_sys::WebPEncodeRGBA(
            pixels.as_ptr(),
            side as i32,
            side as i32,
            (side * 4) as i32,
            85.0,
            &mut pointer,
        )
    };
    if length == 0 || pointer.is_null() {
        return Err(Failure::Unavailable);
    }
    let result = (|| {
        if length > 256 * 1024 {
            Err(Failure::Unavailable)
        } else {
            let mut output = zeroed(length)?;
            // SAFETY: libwebp returned exactly length initialized bytes, still live here.
            output.copy_from_slice(unsafe { std::slice::from_raw_parts(pointer, length) });
            Ok(output)
        }
    })();
    // SAFETY: matching allocator; pointer is neither borrowed nor used afterwards.
    unsafe { libwebp_sys::WebPFree(pointer.cast()) };
    result
}

/// Decode the complete static result and reject every inherited metadata/container payload.
fn verify(bytes: &[u8], side: u32) -> Result<()> {
    let raster = crate::webp_input::decode(bytes).map_err(|_| Failure::Unavailable)?;
    if (raster.width, raster.height) != (side, side) || raster.profile.is_some() {
        return Err(Failure::Unavailable);
    }
    let mut offset = 12;
    while offset < bytes.len() {
        let tag = bytes.get(offset..offset + 4).ok_or(Failure::Unavailable)?;
        if !matches!(tag, b"VP8X" | b"VP8 " | b"VP8L" | b"ALPH") {
            return Err(Failure::Unavailable);
        }
        let length = u32::from_le_bytes(
            bytes
                .get(offset + 4..offset + 8)
                .ok_or(Failure::Unavailable)?
                .try_into()
                .map_err(|_| Failure::Unavailable)?,
        ) as usize;
        offset += 8 + length + (length & 1);
    }
    if offset != bytes.len() {
        return Err(Failure::Unavailable);
    }
    Ok(())
}
