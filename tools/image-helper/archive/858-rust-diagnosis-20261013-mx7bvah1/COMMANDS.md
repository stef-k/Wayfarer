# Reproducing the bounded #858 diagnosis

Run from `/home/stef/wayfarer-research/858-rust-diagnosis-20261013-mx7bvah1`.
The copied toolchain/package caches and original executable are scratch-owned.
No command requires touching or building in the preserved workspace.

## Direct transforms and controls

```bash
source ./env.sh
python3 diagnose.py --fixtures photo.jpg photo.webp opaque.png photo.gif --modes original instrumented --count 12 --warmups 3 --report evidence/direct.json
python3 diagnose.py --fixtures photo.jpg photo.webp opaque.png --modes io --count 12 --warmups 3 --report evidence/io-control.json
python3 diagnose.py --fixtures photo-no-icc.jpg alpha-large.png --modes instrumented --count 8 --warmups 3 --report evidence/icc-alpha-controls.json
```

Every transform uses `/usr/bin/prlimit` with AS 1 GiB, CPU 20/25 seconds,
nofile 128 and core zero, and the original mandatory recipe and parent-death
checks. The I/O control echoes encoded source bytes without image validation.
`controls/photo-no-icc.jpg` was made by removing only exact ICC APP2 segments
identified by Pillow; source raster equality and hashes are recorded in
`evidence/icc-control-provenance.json`. It is a diagnostic fixture only.

## Existing C# benchmark seam

```bash
source ./env.sh
export WAYFARER_IMAGE_HELPER="$PWD/source/tools/image-helper/target/release/wayfarer-image"
export WAYFARER_IMAGE_FIXTURES="$PWD/retained-evidence/fixtures"
export WAYFARER_IMAGE_PERF_INPUT=photo.jpg
export WAYFARER_IMAGE_PERF_REPORT="$PWD/evidence/csharp-jpeg-warm.json"
export WAYFARER_IMAGE_PERF_WARMUPS=20
export WAYFARER_IMAGE_PERF_REPETITIONS=20
cd source
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~ImageHelperPerformanceTests --logger 'console;verbosity=detailed'
```

Repeat with `photo.webp` and a distinct report path. Capture console output to
the corresponding `.log` to retain probes. The final scratch source has two
opt-in supplemental reference flags, `WAYFARER_DIAGNOSTIC_COMPAND=1` and
`WAYFARER_DIAGNOSTIC_JPEG444=1`; set both with eight repetitions for the fidelity
control. Leave both unset for the frozen baseline recipe. The original stage
source and assemblies used for the primary batches are retained separately in
`evidence/instrumentation-stages.patch` and `bin/*stage-instrumented.dll`.

## Existing profiler and offline analysis

```bash
perf record -e cycles:u -F 499 --call-graph dwarf,4096 -o evidence/jpeg-perf.data -- python3 diagnose.py --fixtures photo.jpg --modes original --count 20 --warmups 0 --report evidence/jpeg-profiled.json
perf report -i evidence/jpeg-perf.data --stdio --no-children --sort comm,dso,symbol --percent-limit 0
python3 analyze.py
```

The installed perf cannot resolve ELF symbols or unwind DWARF. Its printed
original-executable file offsets map to ELF VMA by adding 0x1000, as established
by `readelf -l`; `nm -n -C` and `addr2line -f -C` supply existing function names.
The libm IFUNC disassembly identifies its selected `powf` implementation.
The original profiler capture is auxiliary evidence; profiled wall times are
excluded from the ordinary benchmark statistics. No profiler was installed.

Build logs record `cargo build --offline --locked --release -vv`, `cargo fmt`
and `dotnet build -c Release`, using `env.sh`. The first C# restore used the
copied packages and disabled advisory polling for this frozen comparison.
No dependency version, guard threshold or accepted performance gate changed.
