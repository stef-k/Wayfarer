# Isolated fallback experiment: reuse installed tools/caches and keep all outputs task-local.
source /home/stef/wayfarer-research/858-readiness-20261015-KKforFH8/env.sh
export WAYFARER_IMAGE_RESEARCH=/home/stef/wayfarer-research/858-webp-fallback-20261015
export CARGO_TARGET_DIR="$WAYFARER_IMAGE_RESEARCH/target-default-only"
export DOTNET_CLI_HOME="$WAYFARER_IMAGE_RESEARCH/.dotnet-cli"
export WAYFARER_IMAGE_HELPER="$CARGO_TARGET_DIR/release/wayfarer-image"
export WAYFARER_IMAGE_FIXTURES="$WAYFARER_IMAGE_RESEARCH/evidence/fixtures"
