# #858 bounded WebP correction — 2026-10-15

**BLOCKED.** The selected published candidate does not close the required native
security or wrapper-notice gates. No corrected executable was produced and no
push was made. All existing source, optimizations, lockfile, notices, fixtures and
reports remain byte-identical to `a67267660fc958bb019557c400ca5fa06d9d1852`.

Authority: [the maintainer's bounded correction](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6194604503).
The official research branch was verified at that exact commit before creating
the isolated branch `fix/858-webp-security-20261015` in
`/home/stef/wayfarer-research/858-webp-correction-20261015/source`.

## Exact candidate and source verification

Inspected only the authorized preferred candidate, `libwebp-sys` **0.14.4**.
The downloaded published crate matches its registry SHA-256:
`6b3a87b44e34d17161e4f17d92a463d596cb13825dcd1758ed18fd3a721e189c`.
Its `.cargo_vcs_info.json` identifies wrapper commit
[`3a40281fd97537b693174acb7375d64d3b471a56`](https://github.com/NoXF/libwebp-sys/tree/3a40281fd97537b693174acb7375d64d3b471a56).
That tree's vendor submodule identifies libwebp **1.6.0**, commit
[`4fa21912338357f89e4fd51cf2368325b59e9bd9`](https://github.com/webmproject/libwebp/tree/4fa21912338357f89e4fd51cf2368325b59e9bd9),
dated 2025-07-08. All six packaged wrapper files and 200 packaged native files
match their declared upstream Git blobs. Generated package metadata was excluded
from that source comparison; `Cargo.toml.orig` matches upstream `Cargo.toml`.

## Blocking findings

1. **Required allocation hardening is absent.** The candidate contains the
   [`0b56dedc` sorted-offset check](https://github.com/webmproject/libwebp/commit/0b56dedc98cb0367a2c375651ff7820a16bee9cc)
   in `src/utils/huffman_utils.c:127`. It does **not** contain the required
   [`5755839a` initial Huffman-allocation limit](https://chromium.googlesource.com/webm/libwebp/+/5755839a4772de6123628bb0c61996d7c42a0454),
   dated 2026-03-24. Its `src/dec/vp8l_dec.c:483` still initially allocates
   `num_htree_groups * table_size`; `MAX_HUFF_IMAGE_SIZE` and
   `total_huffman_table_size` are absent from the decoder/header. A newer Rust
   package version therefore does not establish the required native hardening.
   This is a source-level gate failure, not a claim of a new CVE or exploit.
2. **Authoritative Rust-wrapper notice remains unavailable.** The published
   archive contains only native `vendor/COPYING` and `vendor/PATENTS` notices.
   Its exact upstream wrapper tree contains no LICENSE/COPYING/NOTICE/copyright
   file. The wrapper sources, build script, header and README supply no copyright
   or permission notice; the only license match is `license = "MIT"` in the
   manifest. The bundled C license does not resolve the wrapper attribution.
   Under the explicit authorization, that metadata alone cannot close the gate.

No native patch, fork, alternate codec search, system-library substitution or
invented attribution was attempted. Existing containment and process limits do
not waive either requirement. The retained TurboJPEG/IJG/zlib and historical
WebP notices remain unchanged; this report makes no new redistribution approval.

## Validation and stopping point

The crate checksum, complete package inventory, exact wrapper/vendor Git-blob
comparisons and the two named upstream patches were verified. The concise
[source/security/license receipt](results/webp-correction-20261015/source-security-license.json)
and compressed [complete source audit](results/webp-correction-20261015/source-audit.json.gz)
retain the identities, hashes, notice-path inventory and hardening comparison.
The downloaded archive and full raw API/source evidence remain in the sibling
workspace's `evidence` directory.

Implementation and runtime qualification stopped at these prerequisite failures.
No new Release build, linked-symbol claim, Rust tests/fmt/Clippy, 74-case C# run,
malformed/lossless/output-cap regression run, paired performance comparison or
memory qualification is claimed. Earlier passing evidence remains historical.
ARM64 execution and native Windows remain unqualified.

Code Guard passes this documentation/evidence delta. The complete research branch
against `origin/main` remains REVIEW: all 15 findings match the previous measured
values and source hashes. Their retained parser, finite-observation and archived
analysis rationales are recorded in the [guard disposition](results/webp-correction-20261015/code-guard-disposition.json).
No guard configuration or baseline changed.

Publishing was conditional on a passing correction. The remote research branch
remains at the authorized starting commit. The report and evidence are preserved
locally; production integration, PR, main, merge, release and issue closure remain
outside this task. A supported source containing the required hardening and an
authoritative permissive wrapper notice are still required before readiness can
be established.
