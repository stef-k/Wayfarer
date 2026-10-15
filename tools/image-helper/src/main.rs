//! Linux AMD64/AArch64 helper. No source metadata or logging is written to stdout.
use std::io::{Read, Write};
use wayfarer_image::{Failure, OutputFormat, Recipe};

/// Emit only completed verified output; every error invalidates the whole operation.
fn main() {
    if let Err(failure) = run() {
        let _ = writeln!(std::io::stderr().lock(), "{}", failure.diagnostic());
        std::process::exit(failure.exit_code());
    }
}

/// Validate the trusted recipe and containment before accepting any image bytes.
fn run() -> Result<(), Failure> {
    let (recipe, validate) = arguments()?;
    controls()?;
    let mut bytes = Vec::new();
    let mut chunk = [0u8; 8192];
    let mut input = std::io::stdin().lock();
    loop {
        let length = input.read(&mut chunk).map_err(|_| Failure::Unavailable)?;
        if length == 0 {
            break;
        }
        if length > recipe.max_input.saturating_sub(bytes.len()) {
            return Err(Failure::TooLarge);
        }
        bytes
            .try_reserve(length)
            .map_err(|_| Failure::Unavailable)?;
        bytes.extend_from_slice(&chunk[..length]);
    }
    let output = if validate {
        wayfarer_image::validate(&bytes)?.as_bytes().to_vec()
    } else {
        wayfarer_image::transform(&bytes, &recipe)?
    };
    let mut stdout = std::io::stdout().lock();
    stdout
        .write_all(&output)
        .map_err(|_| Failure::Unavailable)?;
    stdout.flush().map_err(|_| Failure::Unavailable)?;
    Ok(())
}

/// Six distinct explicit options avoid defaults and ambiguous duplicate arguments.
fn arguments() -> Result<(Recipe, bool), Failure> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() != 13 || !matches!(args[0].as_str(), "transform" | "validate") {
        return Err(Failure::Arguments);
    }
    let mut recipe = Recipe {
        width: 0,
        height: 0,
        quality: 0,
        output: OutputFormat::Auto,
        max_input: 0,
        max_output: 0,
    };
    let mut seen = 0u8;
    for pair in args[1..].chunks_exact(2) {
        let flag = match pair[0].as_str() {
            "--width" => {
                recipe.width = number(&pair[1])?;
                1
            }
            "--height" => {
                recipe.height = number(&pair[1])?;
                2
            }
            "--quality" => {
                recipe.quality = number(&pair[1])?;
                4
            }
            "--max-input-bytes" => {
                recipe.max_input = number(&pair[1])?;
                8
            }
            "--max-output-bytes" => {
                recipe.max_output = number(&pair[1])?;
                16
            }
            "--output-format" => {
                recipe.output = match pair[1].as_str() {
                    "auto" => OutputFormat::Auto,
                    "jpeg" => OutputFormat::Jpeg,
                    "png" => OutputFormat::Png,
                    _ => return Err(Failure::Arguments),
                };
                32
            }
            _ => return Err(Failure::Arguments),
        };
        if seen & flag != 0 {
            return Err(Failure::Arguments);
        }
        seen |= flag;
    }
    recipe.validate()?;
    Ok((recipe, args[0] == "validate"))
}

/// Numeric arguments are strict unsigned decimal values.
fn number<T: std::str::FromStr>(value: &str) -> Result<T, Failure> {
    if value.is_empty() || !value.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(Failure::Arguments);
    }
    value.parse().map_err(|_| Failure::Arguments)
}

/// prlimit must execute us with finite inherited limits, followed by race-checked parent death.
#[cfg(all(
    target_os = "linux",
    any(target_arch = "x86_64", target_arch = "aarch64")
))]
fn controls() -> Result<(), Failure> {
    let expected: libc::pid_t = std::env::var("WAYFARER_IMAGE_PARENT_PID")
        .ok()
        .and_then(|value| value.parse().ok())
        .filter(|pid| *pid > 1)
        .ok_or(Failure::Unavailable)?;
    // SAFETY: these libc calls use scalar arguments and an initialized rlimit output.
    unsafe {
        if libc::geteuid() == 0 || libc::getppid() != expected {
            return Err(Failure::Unavailable);
        }
        for (resource, soft, hard) in [
            (libc::RLIMIT_AS, 1024 * 1024 * 1024, 1024 * 1024 * 1024),
            (libc::RLIMIT_CPU, 20, 25),
            (libc::RLIMIT_NOFILE, 128, 128),
            (libc::RLIMIT_CORE, 0, 0),
        ] {
            let mut limit = libc::rlimit {
                rlim_cur: 0,
                rlim_max: 0,
            };
            if libc::getrlimit(resource, &mut limit) != 0
                || limit.rlim_cur > soft
                || limit.rlim_max > hard
                || (resource != libc::RLIMIT_CORE && limit.rlim_cur == 0)
            {
                return Err(Failure::Unavailable);
            }
        }
        if libc::prctl(libc::PR_SET_PDEATHSIG, libc::SIGKILL, 0, 0, 0) != 0
            || libc::getppid() != expected
        {
            return Err(Failure::Unavailable);
        }
    }
    Ok(())
}

/// Other platform launch controls require a later, separately qualified implementation.
#[cfg(not(all(
    target_os = "linux",
    any(target_arch = "x86_64", target_arch = "aarch64")
)))]
fn controls() -> Result<(), Failure> {
    Err(Failure::Unavailable)
}
