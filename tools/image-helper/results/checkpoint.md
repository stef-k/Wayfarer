## Retained #858 optimization checkpoint — preserve before any new JPEG experiments (2026-10-13)

**Purpose:** make measured improvements, limitations, and deferred decisions findable in the issue. This is a maintainer-level evidence checkpoint based on Codex reports relayed to ChatGPT; the underlying local reports have **not been independently reopened or replayed** here. No production approval or new test execution is implied.

### Preserved performance work

| Stage | Exact preserved reference | What was learned |
| --- | --- | --- |
| Working Rust CLI + C# supervisor | Original prototype and retained #858 history | JPEG/PNG/WebP/static GIF functional path, validation, crash isolation and output verification; not production-integrated |
| Product-focused pipeline recovery | `017e7495c42336d7205b2d6263828ec96e0d980c`, `/home/stef/wayfarer-research/858-web-recovery-20261013-93ijKYrk/REPORT.md` | Avoid unconditional expensive linear-light and float processing for ordinary opaque sRGB photos; substantial improvement, parity still failed |
| SIMD resize and JPEG encoder experiment | `8a857230c0751d87b71e227431365d0fb16a7ca4`, `/home/stef/wayfarer-research/858-simd-experiment-20261013-mfkfwgez/REPORT.md` | `fast_image_resize 6.1.0` dramatically improved small PDF resizes (roughly 32–35 ms to 2.6–2.9 ms); pinned `jpeg-encoder 0.7.1` with SIMD/4:2:0 roughly halved JPEG encoding. Four warm profiles still missed the #821 parity gate |
| Narrow final profiling | Same unmodified `8a857230c0751d87b71e227431365d0fb16a7ca4`, `/home/stef/wayfarer-research/858-final-bottlenecks-20261013-3K3yZ023/REPORT.md` | No sufficient *small fix within existing codec family*; diagnosis was **not** a rejection of other codec libraries |
| Targeted codec comparisons (newest) | **`8d6e488f13d7de0b36c93ce10c2b3b5dcea9ccb4`**, `/home/stef/wayfarer-research/858-codec-comparison-20261013-zufsdz12/REPORT.md` | `libjpeg-turbo-rs` isolated JPEG decode about **41% faster than actual CLI decode path** / **19% faster than zune-jpeg**; JPEG encoding about **74% faster in isolation**, but the tested encoder API did **not demonstrate the mandatory incremental output-byte ceiling**; full JPEG parity still **NO-GO**. Alternative native `libwebp` decoding improved **complete WebP→JPEG** processing **27% web / 40% PDF**, reportedly **passing both measured profile gates**. Experimental WebP→WebP output reduced sizes **11%/34%**, but encoding took **~9–10×** the JPEG encoder; it is **not** accepted as an output-policy change |

The new codec comparison head is a **clean local experimental head**, not a qualified, fully integrated or remotely published Wayfarer replacement. Preserve its code, source fixture hashes, benchmark commands and evidence, not just this summary. Full reports and actual bytes reside in the user's local Linux/WSL filesystem; **a GitHub comment is not a backup of those files**.

### Current disposition and exact next hypothesis

- **Production:** ImageSharp 3.1.12 remains unchanged. #821's normalized JPEG/PNG output contract, web default 2048/90, explicit PDF 600/85, high-fidelity `optimize=false` intent, resource safety and full verification remain binding.
- **WebP input path:** measured profile performance PASS, subject to final correctness, native licensing/provenance, Linux x64/ARM64 and Windows support, memory/process containment and full qualification. Do **not** discard the successful WebP decoder experiment.
- **JPEG input/output path:** principal measured performance blocker. Faster encoder is promising but not yet shown to satisfy a *safe incremental output-byte limit*. Before any more extensive work, test whether `libjpeg-turbo-rs`'s claimed bounded-buffer `compress_into()` can meet **all** caps/ICC/quality requirements; a narrowly scoped native `libjpeg-turbo`/Rust `turbojpeg` comparison is the fallback **only if justified**. Both are unproven hypotheses, not accepted fixes. Preserve and compare against the previous actual CLI, SIMD and ImageSharp baselines rather than replacing them.
- **Decision principle:** keep future experiments short, candidate-specific and performance-measured; no speculative library survey, broad redesign, silent parity-gate relaxation, unbounded output or omitted validation. Use fresh isolated scratch clones and preserve every prior branch/commit/report/manifest. Do not modify existing reports or scratch workspaces; no push/PR, production integration, release or issue closure without new authorization.

### Recorded but deliberately deferred (do not mix into next JPEG test)

- Real 10/20-image trip loads, first-image user experience, cache-hit validation and trust/provenance optimization.
- Whether original safely validated WebP can ever be served unchanged, or whether normalized lossy WebP is a worthwhile output policy; JPEG/PNG remains the accepted contract. Include WebP transparency and ICC/privacy behavior in any later policy design.
- Product rollout of 2048/90, caller override semantics, cache recipe/versioning, compatibility with browsers/PDF/exports/mobile/external clients and the distinct `optimize=false` behavior.
- Overall native dependency notices, RID/ARM64 packaging, output integrity and hostile-resource qualification, and independent exact-head review before any production merge.

**Status: #858 open; Rust replacement overall NO-GO for production today, with valuable preserved measured improvements and a narrowly actionable JPEG experiment hypothesis.**
