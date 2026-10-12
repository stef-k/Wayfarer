# Isolated Rust image prototype (#858)

This Linux x64 prototype follows the [accepted design](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6131203483).
It does not connect to the production image proxy, HTTP or cache.

Build with Rust 1.90.0 using `cargo build --locked --release`.
Invoke the resulting `target/release/wayfarer-image` executable through
`/usr/bin/prlimit` with address space 1 GiB, CPU 20/25 seconds, descriptors 128
and core dumps zero. Set `WAYFARER_IMAGE_PARENT_PID` to the launching process PID.
The launch thread must survive until the child has been reaped.

All six recipe options are mandatory:

```text
transform --width 600 --height 8192 --quality 85 --output-format auto
          --max-input-bytes 52428800 --max-output-bytes 52428800
```

Stdin supplies one bounded image through EOF. Stdout contains only a completed,
decoded and verified JPEG/PNG derivative. Diagnostics are fixed strings.
Exit 2 means invalid trusted arguments, 3 positive source limits, 4 malformed
or unsupported source/color, and 70 processing/control/I/O unavailability.
Every nonzero exit invalidates stdout. The supervisor must also reject crashes,
timeouts and output overflow as unavailable.

The first checkpoint supports JPEG and PNG. PNG admission verifies CRCs, exact
container EOF, exact filtered-byte count and complete checksum-verified zlib
termination using the accepted immutable E+1 buffer. It rejects APNG declarations.
ICC conversion preserves JPEG CMYK channels and PNG 16-bit precision. Untagged
CMYK uses multiplicative conversion. EXIF orientation includes post-IDAT PNG eXIf.
Resizing uses scene-linear premultiplied alpha and Lanczos3, without enlargement.
Fresh encoders retain only generated canonical sRGB ICC metadata.

JPEG ICC assemblies that the decoder cannot assemble are treated as untagged,
as accepted in the design. High-precision lossless JPEG (L16) is rejected: the
pinned API does not expose its sample precision for correct normalization.
WebP, GIF and the standalone supervisor follow in subsequent local checkpoints.
