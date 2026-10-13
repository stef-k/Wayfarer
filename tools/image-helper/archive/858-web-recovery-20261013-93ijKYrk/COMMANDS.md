# Reproducing the #858 recovery evidence

Use the scratch-owned caches and source at `/home/stef/wayfarer-research/858-web-recovery-20261013-93ijKYrk`.
The preserved prototype/diagnosis/primary checkouts are read-only references.
No GitHub write or production integration is involved.

## Build and focused checks

```bash
cd /home/stef/wayfarer-research/858-web-recovery-20261013-93ijKYrk
source ./env.sh
cd source
cargo build --manifest-path tools/image-helper/Cargo.toml --offline --locked --release
cargo fmt --manifest-path tools/image-helper/Cargo.toml --check
cargo clippy --manifest-path tools/image-helper/Cargo.toml --offline --locked --release -- -D warnings
cargo build --manifest-path tools/image-helper/Cargo.toml --offline --locked --release --features diagnostic-timing --target-dir ../diagnostic-target
export WAYFARER_IMAGE_HELPER="$PWD/tools/image-helper/target/release/wayfarer-image"
export WAYFARER_IMAGE_FIXTURES="$PWD/../evidence/fixtures"
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ImageHelperPrototype|FullyQualifiedName~ImageHelperPngCompletion' --logger 'trx;LogFileName=recovery-functional-replay.trx' --results-directory ../evidence/test-results
code-guard . --base-ref c998da079838894da32cb331f4f338cd9597047f --ci
```

Fixtures were copied from the original evidence. PNG completion tests intentionally
write only to this scratch fixture directory. Original/recovered normal binaries
and the copied diagnosis binary are hashed in `evidence/environment.json`.
The measured processing/collector head is
`220b1be574b0cc0898f04ee4aacc846872470e71`; later changes are documentation only.

## Paired warm observations

From the scratch `source/` directory after building:

```bash
set -e
export WAYFARER_IMAGE_PERF_ORIGINAL="$PWD/../bin/wayfarer-image-original"
export WAYFARER_IMAGE_PERF_JPEG444=1
export WAYFARER_IMAGE_PERF_WARMUPS=20
export WAYFARER_IMAGE_PERF_REPETITIONS=40
export WAYFARER_IMAGE_PERF_PHASE=warm
export WAYFARER_IMAGE_PERF_ORDER=0
for session in 1 2; do
  for profile in pdf web; do
    if test "$profile" = pdf; then
      export WAYFARER_IMAGE_PERF_WIDTH=600 WAYFARER_IMAGE_PERF_HEIGHT=8192 WAYFARER_IMAGE_PERF_QUALITY=85
    else
      export WAYFARER_IMAGE_PERF_WIDTH=2048 WAYFARER_IMAGE_PERF_HEIGHT=2048 WAYFARER_IMAGE_PERF_QUALITY=90
    fi
    for format in jpeg webp; do
      if test "$format" = jpeg; then
        export WAYFARER_IMAGE_PERF_INPUT=photo.jpg
      else
        export WAYFARER_IMAGE_PERF_INPUT=photo.webp
      fi
      export WAYFARER_IMAGE_PERF_REPORT="$PWD/../evidence/performance/$profile-$format-warm-$session.json"
      dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~ImageHelperPerformanceTests --logger 'console;verbosity=normal'
    done
  done
done
```

To reproduce cold observations, use the same two profile/format loops with
`WAYFARER_IMAGE_PERF_PHASE=cold` and `WAYFARER_IMAGE_PERF_REPETITIONS=1`.
For each cell, launch eight separate test commands with sample numbers 1–8,
`WAYFARER_IMAGE_PERF_ORDER` set to that number, and report filename
`$profile-$format-cold-$sample.json`. Each command creates a fresh managed host.
Do not flush OS caches or run another benchmark/build concurrently.
Both phases always launch one new Rust child per transform.

The unchanged production ImageSharp seam remains the gate. The optional 4:4:4
reference is implemented only in the existing performance test source.

## Stage, CPU, RSS and quality observations

From the scratch workspace root:

```bash
source ./env.sh
python3 diagnose.py --fixtures photo.jpg photo.webp --modes original original-instrumented recovered instrumented --count 8 --warmups 3 --width 600 --height 8192 --quality 85 --report evidence/direct-pdf.json
python3 diagnose.py --fixtures photo.jpg photo.webp --modes original original-instrumented recovered instrumented --count 8 --warmups 3 --width 2048 --height 2048 --quality 90 --report evidence/direct-web.json
python3 analyze.py
```

`diagnose.py` is the retained collector adapted for explicit profiles and
per-child sampled VmHWM. Every child inherits the original prlimit/PDEATHSIG
controls. Its sampler adds auxiliary overhead; primary performance gates use
the uninstrumented C# path. `analyze.py` reuses the retained median/nearest-rank
statistics and independent LittleCMS output inspection. The aggregate formula
cross-check is retained in `evidence/summary-crosscheck.json`.

Original and recovered probe builds must each match their respective normal
decoded-pixel hashes. Full paired outputs are retained, including profile,
sampling and quantization-table observations. Uniform large-source geometry
controls and their verified results are in `evidence/geometry/` and
`evidence/geometry-results.json`.

`ARTIFACTS.json`, `evidence/preservation-after.json`, `evidence/recovery.patch`
and `SHA256SUMS` identify the final source/evidence and protected references.
Do not interpret cold wins, PSNR, sampled RSS or this NO-GO as production,
aggregate-memory or broader-corpus qualification.
