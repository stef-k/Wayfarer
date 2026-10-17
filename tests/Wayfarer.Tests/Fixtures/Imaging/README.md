# Independent imaging regression inputs and output checks

Issue #878 retires the last active ImageSharp dependency. These 24 tiny golden
images preserve the generated input recipes from main at
`c69c3219c45d96788d0078acc8b2b9c7f47105cf`. They were captured once using the
historical ImageSharp 3.1.12 encoder, with its original defaults. No encoder or
ImageSharp tooling is needed to build or run the tests. The generated pixels are
Wayfarer test patterns, with no external photography or personal metadata.

[provenance.json](provenance.json) records each filename, SHA-256, byte length,
encoded dimensions, frame count and source recipe. It describes encoded source
pixels; JPEG is lossy, GIF quantizes alpha and JPEG drops alpha. Tests read a fresh
copy for each mutation. Do not regenerate fixtures to accommodate a product failure.
Existing photographic, color-profile, orientation and malformed regression images
remain unchanged in [the helper corpus](../../../../tools/image-helper/fixtures).

## Assertion contracts

All seven former direct-use files keep their product cases. Only fixture creation
and the independent output decoder change; malformed structure construction remains
in the existing tests. No assertions, test cases or negative vectors are removed.

| File | Preserved contract and replacement |
| --- | --- |
| `Infrastructure/ImageProxyTestFactory.cs` | Reads the same nine valid/rejected 2x2 encodings. Scope ownership and fresh-array behavior remain intact. |
| `Util/ImageProxyHelperOptimizationTests.cs` | Golden still, animated, resize, orientation and quality inputs; independent decoded PNG/JPEG format, single frame, dimensions, alpha, EXIF/XMP removal and quality-dependent bytes. |
| `Util/WebpFrameAuthorityTests.cs` | Original static/two-frame WebP seeds, one-frame derivation, RIFF sizing, padding, unknown chunks, overflow and malformed-container outcomes. |
| `Util/ImageProxyDecodedPreflightContractTests.cs` | Original 1x1 PNG seed, CRC-aware mutations, declared dimensions, exact IEND and APNG frame/control authority. |
| `Services/ImageProxyRustIntegrationTests.cs` | Real helper/proxy/cache behavior; original 1x1 padding seed for 50/100/200 MiB boundaries, byte-identical originals, decoded optimized output, bounded mixed-format loads and the exact malicious WebP. |
| `Util/ImageHelperSupervisorTests.cs` | Original stripe pixels and retained research corpus; numeric sRGB/alpha ranges, all eight JPEG/PNG orientations, independent PNG pixel comparisons, ICC/EXIF/XMP, CMYK/gamma/cICP, failures, limits, crashes, deadlines, admission and reap. |
| `Util/ImageHelperPngCompletionTests.cs` | Platform zlib/CRC construction remains; independent output dimensions and EXIF checks preserve Deflate, Adler, sample-layout and post-IDAT orientation cases. |

The shared factory also serves `ProxyImageSafetyTests`, `PublicTripImagesTests`,
`TripViewerControllerTests`, `ImageCacheProxyQualificationTests`,
`ImageProxyServiceTests` (including its decoded-resource partial),
`ImageProxyDiagnosticPrivacyTests`, `ImageProxyOriginOwnershipTests`,
`ImageProxyRasterPolicyTests`, `ImageProxyServiceBackgroundRefreshTests`,
`ProxiedImageCacheAdmissionTests` and `ProxiedImageCacheCancellationTests`.
Their valid/rejected bytes, MIME routing, raw fidelity, stale/cancellation/privacy,
admission, ETag/304 and public-cover contracts retain the same source inputs.

## Independent output oracle

`Magick.NET-Q8-AnyCPU` and its `Magick.NET.Core` dependency are pinned to 15.0.0,
with `PrivateAssets="all"` on the test reference. ImageMagick decodes canonical
PNG/JPEG output, exposes unconverted channel samples and independently reads ICC,
EXIF, XMP and orientation. Collections check the complete frame count rather than
implicitly selecting the first frame. Existing numeric expectations and pixel
comparisons remain; semi-transparent PNG alpha is now checked exactly at 128.
Orientation/metadata cases also assert that their source profiles are actually
visible to the new inspector before asserting their removal from output.

The reference PNG decoder is libpng, distinct from the helper's Rust `png` crate.
Its C libjpeg-turbo decoder is distinct from the helper's pure-Rust
`libjpeg-turbo-rs` decoder. C libjpeg-turbo is also the helper's JPEG **encoder**;
lossy JPEG checks therefore retain independent numeric color bounds and trusted
input expectations. The reference never invokes the helper or its production
decoding implementation to compute expected results. Rust still owns malicious
source decoding and rejection; those hostile vectors are not passed to the
reference decoder. Reference reads are bounded golden inputs or successful
canonical helper output.

## Maintenance and portability

The package identifies upstream commit
`79bb8700d7fe3a564f62926ab299fc2520cb2269`, ImageMagick 7.1.2-33 and a .NET 10
assembly. Its [15.0.0 release](https://github.com/dlemstra/Magick.NET/releases/tag/15.0.0)
contains upstream security fixes, including XMP parser fixes. The initial NuGet
direct/transitive vulnerability audit reported no advisories for this reference
dependency. Repeat that audit when updating the pin; it is not a security guarantee.

The [upstream platform table](https://github.com/dlemstra/Magick.NET/blob/15.0.0/Readme.md)
and [native-loading guidance](https://github.com/dlemstra/Magick.NET/blob/15.0.0/docs/CrossPlatform.md)
describe bundled Linux x64/ARM64, Windows and macOS assets. No system ImageMagick,
shell conversion command, font rendering or new image build step is used. Q8
matches the existing 8-bit output assertions; retained 16-bit input/gamma cases
still run through the real helper. Linux x64 execution is locally verified;
bundled assets alone do not establish ARM64, Windows or macOS runtime evidence.
The helper's existing Linux production/Windows development boundary is unchanged.

The managed wrapper is Apache-2.0. The package's `Notice.txt` inventories ImageMagick
and bundled native component licenses, including LGPL components; the package is
not uniformly Apache-2.0. Restore it from NuGet for development/CI use. These native
test binaries are absent from application dependencies/publish content and are
not included in release artifacts. Redistributing a test runner/native bundle
requires a separate native-license/source/relink assessment; this migration does
not approve such redistribution or adopt ImageMagick as a production decoder.

## Validation

Run the existing focused image selections, then shared factory consumers. Compare
the original and migrated test identities and outcomes, not only their totals.
Restore every checked-in `.csproj` (including the browser and Compose probes),
inspect `project.assets.json` and fresh `.deps.json` files, and run the NuGet
direct/transitive audit. Search active source/project files for retired package
references and imports; historical provenance and changelog explanations may remain.
Exact-head CI, fresh independent technical review and maintainer acceptance remain
required before merge. No release, publication or deployment is part of #878.
