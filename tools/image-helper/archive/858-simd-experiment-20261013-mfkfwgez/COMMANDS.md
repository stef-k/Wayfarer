# Reproducing the isolated #858 SIMD experiment

The frozen workspace is `/home/stef/wayfarer-research/858-simd-experiment-20261013-mfkfwgez`.
Use a fresh owned copy with private caches and new evidence/report paths for a
replay. The original evidence and all retained prototypes must stay read-only.
The commands below record the method used; they are not permission to overwrite
the frozen artifacts. Exact input/binary/source hashes accompany `SHA256SUMS`.

## Sources and builds

Recovered baseline: `017e7495c42336d7205b2d6263828ec96e0d980c`.
Resize-only: `29bfcfe91481466d565f7de48642e9e31e8f5522`.
Encoder-only measured source: `a13c90bc12c6e28a715e5637320fd25bd88ee531`.
Combined measured source: `0e83d22fdacfc7b1f952c8d9f2083577b7cfb7bc`.
Each is preserved in Git and as normal/diagnostic executables in `bin/`.
Subsequent Git changes affect tests/documentation only.

```bash
source ./env.sh
cd source
cargo build --manifest-path tools/image-helper/Cargo.toml --offline --locked --release
cargo fmt --manifest-path tools/image-helper/Cargo.toml --check
cargo test --manifest-path tools/image-helper/Cargo.toml --offline --locked --release -- --nocapture
cargo clippy --manifest-path tools/image-helper/Cargo.toml --offline --locked --release --all-targets -- -D warnings
cargo build --manifest-path tools/image-helper/Cargo.toml --offline --locked --release --features diagnostic-timing --target-dir ../diagnostic-target
```

The initial resolver builds added only pinned new dependencies; final qualification
used locked/offline Release builds. The two processing commits are independently
applicable; the JPEG commit cherry-picks cleanly onto the recovered base. The
independent encoder worktree's Cargo.lock contains no fast_image_resize dependency.

## Functional checks

```bash
export WAYFARER_IMAGE_HELPER="$PWD/../bin/wayfarer-image-combined"
export WAYFARER_IMAGE_FIXTURES="$PWD/../evidence/fixtures"
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~ImageHelperPrototype|FullyQualifiedName~ImageHelperPngCompletion' \
  --logger 'trx;LogFileName=combined-functional-replay.trx' --results-directory ../evidence/replay-tests
```

The five large/small geometry sources are retained under `evidence/geometry/`.
`geometry-results.json` records each exact recipe, input hash and independently
decoded result, including quality-95 4:4:4. The ordinary performance photos are
1920×863, so web recompression does not exercise large photographic downscaling.

## Paired measurements

The unchanged opt-in xUnit collector owns the timing boundary. The exact orchestration
is retained as `evidence/paired-collector-command.txt`; use it in a fresh copy
with new report paths. It alternates backend order and records two warm sessions
of 20 warmups + 40 measured samples, then eight fresh cold hosts per cell.
Every Rust sample uses a fresh child process and the unchanged supervisor.
No build or competing benchmark runs during those batches.

Required environment for each collector invocation:

```bash
export WAYFARER_IMAGE_HELPER="$PWD/../bin/wayfarer-image-combined"
export WAYFARER_IMAGE_FIXTURES="$PWD/../evidence/fixtures"
export WAYFARER_IMAGE_PERF_ORIGINAL="$PWD/../bin/wayfarer-image-baseline"
export WAYFARER_IMAGE_PERF_INPUT=photo.jpg
export WAYFARER_IMAGE_PERF_PHASE=warm
export WAYFARER_IMAGE_PERF_REPETITIONS=40
export WAYFARER_IMAGE_PERF_WARMUPS=20
export WAYFARER_IMAGE_PERF_ORDER=1
export WAYFARER_IMAGE_PERF_WIDTH=2048
export WAYFARER_IMAGE_PERF_HEIGHT=2048
export WAYFARER_IMAGE_PERF_QUALITY=90
export WAYFARER_IMAGE_PERF_REPORT="$PWD/../evidence/replay/web-jpeg-warm-1.json"
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release --no-build --no-restore \
  --filter FullyQualifiedName~ImageHelperPerformanceTests --logger 'console;verbosity=normal'
```

Set PDF to width 600, height 8192, quality 85; switch input to `photo.webp` for WebP.
Cold uses repetitions 1, phase cold, a fresh test invocation and distinct report
for each of eight samples. No OS cache flush is performed. Disable the optional
original control only for the recovered-baseline batch. Do not enable the optional
ImageSharp 4:4:4 reference as a substitute for the accepted ordinary 4:2:0 gate.

## Stage, resource and quality evidence

From a fresh workspace root:

```bash
source ./env.sh
python3 diagnose.py --fixtures photo.jpg photo.webp --modes original recovered instrumented \
  --candidate "$PWD/bin/wayfarer-image-combined" \
  --diagnostic "$PWD/bin/wayfarer-image-combined-instrumented" \
  --count 8 --warmups 3 --width 600 --height 8192 --quality 85 \
  --report evidence/replay-direct-combined-pdf.json
```

Repeat with 2048/2048/90 and the other variant binaries/report names. `original`
refers to the frozen recovered baseline copied to `bin/wayfarer-image-original`.
Baseline instrumentation uses `original-instrumented`; other variants use
`instrumented`. Per-variant output directories preserve profile-specific files.
Diagnostic-stage/RSS-sampler results are observational; the uninstrumented C#
measurements remain the primary gate.

The reused `analyze.py --variant baseline|resize|encoder|combined` reads the
per-variant performance directories and original direct-profile reports.
Its median, nearest-rank p95 and inverse-mean formulas were independently
recomputed. Independent LittleCMS inspection compares canonical displayed color,
quantization, sampling, sizes and metadata. Paired full images and unscaled crops
were inspected; no perceptual pass threshold is invented from PSNR.
The pre-existing ICC creation timestamp explains variable encoded hashes;
normal/diagnostic and saved warm/cold decoded pixels remain stable.

## Portability and Code Guard

Private Rustup installed library targets only:

```bash
rustup target add aarch64-unknown-linux-gnu x86_64-pc-windows-gnu
cargo check --manifest-path source/tools/image-helper/Cargo.toml --offline --locked --release --lib --target aarch64-unknown-linux-gnu --target-dir portability-target
cargo check --manifest-path source/tools/image-helper/Cargo.toml --offline --locked --release --lib --target x86_64-pc-windows-gnu --target-dir portability-target
cd source
code-guard . --changed-only --json --json-mode compact
code-guard . --base-ref 017e7495c42336d7205b2d6263828ec96e0d980c --ci --json --json-mode compact
code-guard . --base-ref main --ci --json --json-mode compact
```

The owned clone fetched live main and retained its exact SHA in
`evidence/live-main-reference.json`; neither preserved source nor protection was
changed. The experiment scope passes; the full-main comparison retains documented
inherited admission/color reviews. External collector complexity REVIEW is
accepted after independent formula verification. No guard policy was changed.
Cross-compilation is library build evidence, not other-platform execution or
process-isolation qualification. The complete report records remaining gaps.
