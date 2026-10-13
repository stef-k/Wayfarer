//! Isolated image policy and transform. HTTP, caches and process admission stay in C#.
mod color;
mod gif_input;
mod png_input;
mod raster;
mod transform;
mod webp_input;

#[cfg(feature = "codec-bench")]
pub use transform::benchmark_pixels;
pub use transform::{OutputFormat, Recipe, transform};

/// Fixed public diagnostics never contain source metadata or decoder error strings.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Failure {
    Arguments,
    TooLarge,
    Source,
    Unavailable,
}

impl Failure {
    /// The supervisor maps only positive source-limit evidence to exit 3.
    pub fn exit_code(self) -> i32 {
        match self {
            Self::Arguments => 2,
            Self::TooLarge => 3,
            Self::Source => 4,
            Self::Unavailable => 70,
        }
    }

    /// One fixed, bounded diagnostic is suitable for stderr.
    pub fn diagnostic(self) -> &'static str {
        match self {
            Self::Arguments => "invalid transform arguments",
            Self::TooLarge => "source exceeds image limits",
            Self::Source => "unsupported or malformed source image",
            Self::Unavailable => "image processing unavailable",
        }
    }
}

pub(crate) type Result<T> = std::result::Result<T, Failure>;
pub const MAX_ENCODED: usize = 50 * 1024 * 1024;

/// Local qualification probes contain fixed stage names and durations, never source metadata.
pub fn diagnostic_timing(stage: &str, start: std::time::Instant) {
    if cfg!(feature = "diagnostic-timing") {
        eprintln!(
            "WF_DIAG {stage} {:.6}",
            start.elapsed().as_secs_f64() * 1000.0
        );
    }
}

/// Check original geometry before any raster-sized allocation.
pub(crate) fn dimensions(width: u32, height: u32) -> Result<usize> {
    if width == 0 || height == 0 {
        return Err(Failure::Source);
    }
    let pixels = (width as usize)
        .checked_mul(height as usize)
        .ok_or(Failure::TooLarge)?;
    let estimate = pixels.checked_mul(4).ok_or(Failure::TooLarge)?;
    if width > 8192 || height > 8192 || pixels > 12_000_000 || estimate > 64 * 1024 * 1024 {
        return Err(Failure::TooLarge);
    }
    Ok(pixels)
}

/// Fallible allocations that we own are infrastructure failures, never size evidence.
pub(crate) fn zeroed<T: Default + Clone>(length: usize) -> Result<Vec<T>> {
    let mut buffer = Vec::new();
    buffer
        .try_reserve_exact(length)
        .map_err(|_| Failure::Unavailable)?;
    buffer.resize(length, T::default());
    Ok(buffer)
}
