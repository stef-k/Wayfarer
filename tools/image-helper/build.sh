#!/usr/bin/env bash
# Build the locked helper natively; reject accidental cross-RID packaging.
set -euo pipefail
case "$(uname -m)" in x86_64) native_rid=linux-x64 ;; aarch64) native_rid=linux-arm64 ;; *) exit 1 ;; esac
if [ -n "${1:-}" ] && [ "$1" != "$native_rid" ]; then
  echo 'Build the image helper on the target Linux architecture.' >&2
  exit 1
fi
cd "$(dirname "$0")"
cargo build --release --locked
