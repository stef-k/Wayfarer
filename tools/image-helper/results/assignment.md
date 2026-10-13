## #858 maintainer authorization — SINGLE final JPEG test + research archive + verified local cleanup (2026-10-14)

**One Codex execution, one remote research branch, no additional review/optimization loop.** The maintainer wants this completed as a single bounded task: (a) test the two already identified JPEG encoders, (b) retain the **best safe optimized Rust implementation** and its essential results in GitHub, (c) safely remove superseded local #858 experiment workspaces **only after verifying the remote archive**. This explicitly overrides older #858 `no push` restrictions **only for this research branch**. No production/main edits, PR, merge, release or issue closure.

### Starting point and immutable evidence

- Read `AGENTS.md`, the latest issue checkpoint [6155228367](https://github.com/stef-k/Wayfarer/issues/858#issuecomment-6155228367), and local `/home/stef/wayfarer-research/858-codec-comparison-20261013-zufsdz12/REPORT.md`.
- Start from clean **local comparison commit `8d6e488f13d7de0b36c93ce10c2b3b5dcea9ccb4`**. Verify `git status`, ancestry and actual files before reusing it; compare with prior `8a857230c0751d87b71e227431365d0fb16a7ca4`. **Do not lose working SIMD, JPEG decoding, or passing native WebP decoding gains.** Distinguish benchmark-only trials from fully integrated/safety-qualified changes.
- Previous experiments and their reports/hashes remain reference material. Keep the baseline commit recoverable; do not reset/force-push/rewrite history or touch other projects/worktrees.

### Exactly two JPEG encoder tests (no expanded surveys)

1. **Pure Rust:** `libjpeg-turbo-rs 0.8.0` bounded-output `compress_into()` route (or exact supported equivalent). Test whether a fixed, enforceable **incremental output-byte limit** works, including full-buffer/cancel/failure behavior, ICC, valid JPEG, output quality, 4:2:0, and x64/ARM64 SIMD/portability. Benchmark encode-only and end-to-end JPEG with the previous optimized pipeline.
2. **Native fallback:** `libjpeg-turbo` through Rust `turbojpeg` with an explicit **bounded caller-controlled output buffer**. Test actual bounded failure, allocation/resource behavior, metadata/ICC, quality and portability/licensing cost. Benchmark separately with the same fixtures. **No production native packaging project**; if pinned binding cannot enforce bounds or is clearly impractical, mark FAIL with the exact reason rather than building a custom wrapper.

Use existing corpus and measurement tooling, **not a new harness**. Preserve source-byte admission, full output verification, process supervision, timeout, pixel/RSS limits, metadata privacy, alpha/orientation, static-image policy and PDF 600/85 + web 2048/90 settings. Compare cold/warm p50/p95, throughput, CPU/RSS, output bytes, visual fidelity against ImageSharp and the preserved optimized Rust baseline. **Record any failure of the existing #821 parity gate**; a small remaining regression may be proposed for a separate maintainer decision, **not silently accepted**. Focus on the two JPEG routes; do not repeat general codec surveys or WebP policy experiments.

### Publish ONE durable research branch

- Produce/push **only `research/858-rust-imaging` in `stef-k/Wayfarer`** (if unexpectedly already present, stop to reconcile rather than overwrite). It must preserve the **best safe working Rust source**, C# adapter if applicable, focused tests, `Cargo.lock`, dependency/license notes, small necessary reproducible fixtures/instructions, and one consolidated `README/REPORT` with performance tables, prior experiment SHAs/reports, remaining blockers, and deferred suggestions.
- Retain the **passing WebP decoder route** and SIMD improvements where the current comparison proves them safe/integrated; do **not** accidentally commit slower or unbounded experimental encoder paths as selected production candidates. Label negative experiments clearly rather than silently dropping their results.
- Do not push large generated binaries, temporary harnesses, duplicate raw logs, secret data or the huge tree of evidence files. Keep a short report and hashes/manifest; if an essential fixture cannot be archived safely within reasonable size, retain its local folder and explicitly report this blocker.
- Verify remote branch **SHA, file list, and clean fresh-checkout Release build/focused tests** (where locally feasible). State precisely what is archived and what is not. Research branch is **not** a shipping approval and must not be merged without separate independent review/CI/maintainer acceptance.

### Then CLEAN UP local #858 workspaces, safely and in the same run

Only after **remote archive verification passes**, enumerate the old **`/home/stef/wayfarer-research/858-*`** research folders plus any specifically identified owned #858 research checkout. Classify retained versus superseded, verify none has unique uncommitted/unarchived source, reports, tests, hashes or other live worktree ownership. Remove **only individually verified superseded #858 local experimental directories**, not using a blanket glob, force deletion, `git reset --hard`, or cleanup of unrelated data. Preserve one useful local checkout of the research branch and anything not yet safely recoverable. Record exact deleted paths, reclaimed disk space and retained exceptions. A verification failure **blocks cleanup**, not the published evidence or the overall report; avoid another round of remediation.

### End condition

One concise response with: **chosen encoder and actual JPEG/WebP vs ImageSharp measurements**, output-cap/safety disposition, remote branch URL + HEAD, archived reports, test status, local cleanup performed/retained and **READY-FOR-MAINTAINER-DECISION / NO-GO**. **Stop. No new optimization, no additional Codex/review iteration, no PR/merge, no main changes.** Existing production ImageSharp 3.1.12 unchanged.
