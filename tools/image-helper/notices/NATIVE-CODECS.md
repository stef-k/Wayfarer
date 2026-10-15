# Native codec security and redistribution qualification

2026-10-15 follow-up to the [approved finite tasks](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6189016280).
**BLOCKED for redistribution/security closure.** This is a source and artifact review
of the selected codecs, not approval to distribute the research executable.
The previous [qualification](../QUALIFICATION.md) and its evidence remain unchanged.

## Exact selected source and linkage

The default-only build retains `turbojpeg` 1.5.1 / `turbojpeg-sys` 1.2.0 and
`webp` 0.3.1 / `libwebp-sys` 0.9.6. All four downloaded crate archive SHA-256
values match the unchanged Cargo.lock. No system codec override or host CPU flags
were set. Native build output selects static `turbojpeg`, `webpsys` and `sharpyuv`.
Their compiled archive hashes, source hashes and selected executable symbols are
recorded in [the follow-up evidence](../results/linux-qualification-20261015/).

Vendored libjpeg-turbo is 3.1.0, upstream
[`20ade4dea9589515a69793e447a6c6220b464535`](https://github.com/libjpeg-turbo/libjpeg-turbo/tree/20ade4dea9589515a69793e447a6c6220b464535).
It supplies only JPEG encoding of validated, bounded RGB8 pixels through
`tj3Compress8`, a new compressor per operation, baseline output and a nonempty
borrowed output buffer with `TJPARAM_NOREALLOC`. Source JPEG decoding is Rust.
The prototype does not call TurboJPEG image loaders, JPEG decoding, transforms,
YUV APIs or Java/JNI. Only symbols retained in the final executable count as
shipped code; building an archive or an upstream command-line tool does not make
every function in that archive part of the executable.

Vendored libwebp is
[`95ea5226c870449522240ccff26f0b006037c520`](https://github.com/webmproject/libwebp/tree/95ea5226c870449522240ccff26f0b006037c520),
1.3.1 plus backports. The wrapper calls `WebPDecodeRGB` / `WebPDecodeRGBA` on
admitted static inputs. Native VP8/VP8L decoding is shipped; the retained
`BuildHuffmanTable` symbol confirms that the Huffman source matters to this artifact.
The prototype does not request native WebP encoding, muxing or incremental decoding.

## Security disposition

The upstream review was frozen at libjpeg-turbo main
`2fa570c514a4b0e70342ef8b32ce1cda21b85710` and libwebp main
`4b9e293a86d3ab96eb8ad2798514eddd10af27f2`.

- **CVE-2023-4863: backport present.** The vendor descends from upstream fix
  `902bc9190331343b2017211debcec8d2ab87e17a`; its Huffman .c/.h hashes still match
  the retained 1.3.2 comparison. A scanner using only the 1.3.1 version string
  would miss this provenance. This does not establish all later hardening.
- **CVE-2023-1999: fixed before this vendor.** Upstream 1.3.1 NEWS records the
  lossless encoder fix. This helper also uses only native WebP decoding.
- **CVE-2026-75466: not applicable to this JPEG pin/path.** The upstream tracker
  identifies introduction with 3.1.90 PNG loading; the selected version is 3.1.0
  and the helper does not call `tj3LoadImage*()`.
- The reviewed JPEG changelog after 3.1.0 contains decompression, transform,
  loader, JNI and API-misuse fixes. The pinned RGB8 encoder uses none of those
  routes, zero-sized destination reuse, or reallocating output buffers. No known
  relevant JPEG pin correction was established by this finite review.
- **WebP hardening remains unresolved.** This vendor lacks upstream
  [`0b56dedc`](https://github.com/webmproject/libwebp/commit/0b56dedc98cb0367a2c375651ff7820a16bee9cc),
  which checks the sorted Huffman-table offset before writing, and
  [`5755839a`](https://github.com/webmproject/libwebp/commit/5755839a4772de6123628bb0c61996d7c42a0454),
  which limits initial Huffman-tree allocation for inputs from other encoders.
  These concern the shipped lossless decoder. No new CVE or exploitable
  counterexample is asserted. Source dimension caps and process containment do
  not prove that those internal hardening changes are unnecessary.

The published `webp` 0.3.1 wrapper requires `libwebp-sys ^0.9.3`; 0.9.6 is the
newest non-yanked compatible release. The current native binding is 0.14.4,
outside that range; `webp` 0.3.1 and `turbojpeg-sys` 1.2.0 are already their
latest published releases. Adding 0.14.4 directly would leave the wrapper's
0.9.6 dependency in place and would not replace its old C source. A wrapper
fork, a new codec route or native-source patch series exceeds the approved
small supported pin update. No dependency change was made.

This finite review stops at those unresolved changes. It does not claim a
complete vulnerability audit or classify every later upstream commit.
Primary references: [WebP NEWS](https://chromium.googlesource.com/webm/libwebp/+/refs/tags/v1.6.0/NEWS),
[JPEG changelog at reviewed main](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/2fa570c514a4b0e70342ef8b32ce1cda21b85710/ChangeLog.md),
[WebP security tracker](https://security-tracker.debian.org/tracker/source-package/libwebp),
[JPEG PNG-loader CVE](https://security-tracker.debian.org/tracker/CVE-2026-75466).

## Notices and redistribution

Carry the following documentation with either AMD64 or ARM64 binaries:

- **IJG acknowledgment:** This software is based in part on the work of the
  Independent JPEG Group. Retain the exact [IJG README](libjpeg-turbo-3.1.0-README.ijg.txt)
  and [TurboJPEG BSD terms](libjpeg-turbo-3.1.0-LICENSE.md.txt), including its
  explanation of the SIMD zlib terms. Do not use contributor names for endorsement.
- **WebP C source:** retain the BSD copyright/conditions/disclaimer, AUTHORS
  and WebM PATENTS text in [the existing notice](libwebp-sys-0.9.6.txt).
- **Rust wrappers:** select the permissive MIT alternative where offered.
  The exact turbojpeg-sys commit's root MIT license is byte-identical to the
  [already retained turbojpeg license](turbojpeg-1.5.1-LICENSE.txt).
  The [webp wrapper notice](webp-0.3.1.txt) retains its MIT/Apache alternatives.
- **Unresolved wrapper notice:** libwebp-sys 0.9.6 declares MIT in Cargo.toml,
  but its crate archive and exact upstream tree
  [`4007a323`](https://github.com/NoXF/libwebp-sys/tree/4007a323c1dcc4ad11d70ddadffc51ecfa1dbb5e)
  provide no wrapper LICENSE/COPYING/NOTICE text or copyright grant. The existing
  libwebp C notice does not fill that gap. Do not invent an upstream copyright
  statement. Obtain the authoritative wrapper notice before declaring this closed.

The identified native licenses do not require a copyleft source offer or a
relinkable application object distribution; that does not resolve the missing
wrapper notice or cover every other dependency in a future application package.
These native notice requirements are the same on AMD64 and ARM64. Actual ARM64
build/link/execution and its final binary inventory remain **UNPROVEN**.

The measured AMD64 ELF statically contains the native codecs and dynamically
needs only the GNU loader, libc, libm and libgcc_s. Its highest required GLIBC
symbol version is 2.34. Native deployment must provide compatible system
libraries and `/usr/bin/prlimit`; do not label it a fully static executable.
The follow-up report records the separate non-root/read-only container smoke.
No release bundle, Dockerfile or application integration was changed.
