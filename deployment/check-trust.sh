#!/usr/bin/env bash
# Read-only prerequisite check before native/CI package acquisition. Pass --node
# for the supported build/provisioning Node; runtime-only hosts need only OS roots. --rustup validates the hosted downloader.
set -euo pipefail
# Retain the platform image identity and the effective corrected package in job logs.
printf 'Trust substrate: %s %s %s %s\n' "${ImageOS:-native}" "${ImageVersion:-unknown}" "${RUNNER_OS:-unknown}" "${RUNNER_ARCH:-unknown}"
if command -v dpkg-query >/dev/null; then
  dpkg-query -W -f='${Package} ${Version}\n' ca-certificates
fi
python3 - <<'PY'
"""Inspect effective OpenSSL defaults, including additions supplied by the host."""
import hashlib
from pathlib import Path
import ssl

context = ssl.create_default_context()
# OpenSSL loads hashed directories lazily. Inspect their certificates explicitly
# so a stale custom anchor cannot hide behind an already-correct bundle file.
capath = ssl.get_default_verify_paths().capath
for entry in Path(capath).iterdir() if capath else ():
    if entry.is_file():
        try:
            context.load_verify_locations(cafile=str(entry))
        except ssl.SSLError:
            continue
roots = {hashlib.sha256(c).hexdigest() for c in context.get_ca_certs(binary_form=True)}
if '86a1ecba089c4a8d3bbe2734c612ba341d813e043cf9e8a862cd5c57a36bbe6b' in roots:
    raise SystemExit('Patch the host CA package: emSign ECC Root CA G3 is still trusted.')
if '96bcec06264976f37460779acf28c5a7cfe8a3c0aae11a8ffcee05c0bddf08c6' not in roots:
    raise SystemExit('Host trust is unqualified: ISRG Root X1 positive control is missing.')
print('PASS host CA prerequisites:', ssl.get_default_verify_paths(), len(roots))
PY
if [[ "${1:-}" == --node ]]; then
  node --input-type=commonjs - <<'JS'
// Node 24 has separate bundled roots; inspect its actual effective defaults too.
const tls = require('node:tls');
const { X509Certificate } = require('node:crypto');
const [major, minor, patch] = process.versions.node.split('.').map(Number);
if (major !== 24 || minor < 22 || (minor === 22 && patch < 1)) {
  throw new Error('Build/provisioning requires Node 24.22.1+ within Node 24.');
}
// Defaults include enabled bundled/system roots and NODE_EXTRA_CA_CERTS. Add the
// required host store too: OpenSSL's lazy defaults are absent from the default view.
const certificates = [...tls.getCACertificates('default'), ...tls.getCACertificates('system')];
const roots = certificates.map(pem => new X509Certificate(pem).fingerprint256.replaceAll(':', '').toLowerCase());
if (roots.includes('86a1ecba089c4a8d3bbe2734c612ba341d813e043cf9e8a862cd5c57a36bbe6b') ||
    !roots.includes('96bcec06264976f37460779acf28c5a7cfe8a3c0aae11a8ffcee05c0bddf08c6')) {
  throw new Error('Node trust prerequisites failed; remove stale custom CA additions and use patched roots.');
}
console.log('PASS Node prerequisites:', process.version, roots.length);
JS
fi
# The assessed rustup 1.29.1/1.29.2 downloaders use platform roots plus GlobalSign
# R3/R46 respectively and Amazon Root CA 1. Reject alternate trust/transport owners.
if [[ "${1:-}" == --rustup ]]; then
  rustup_version="$(rustup --version)"
  printf 'Rustup manager: %s\n' "$rustup_version"
  # Retain failing input names, never potentially credential-bearing override URLs.
  rejected_inputs=()
  case "$rustup_version" in
    'rustup 1.29.1 ('*|'rustup 1.29.2 ('*) ;;
    *) rejected_inputs+=(manager-version) ;;
  esac
  [[ "${RUSTUP_USE_CURL-0}" == 0 ]] || rejected_inputs+=(RUSTUP_USE_CURL)
  [[ "${RUSTUP_USE_RUSTLS-1}" == 1 ]] || rejected_inputs+=(RUSTUP_USE_RUSTLS)
  [[ "${RUSTUP_DIST_SERVER-https://static.rust-lang.org}" == https://static.rust-lang.org ]] || rejected_inputs+=(RUSTUP_DIST_SERVER)
  [[ -z "${RUSTUP_DIST_ROOT:-}" ]] || rejected_inputs+=(RUSTUP_DIST_ROOT)
  [[ -z "${RUSTUP_UPDATE_ROOT:-}" ]] || rejected_inputs+=(RUSTUP_UPDATE_ROOT)
  if (( ${#rejected_inputs[@]} )); then
    printf 'Unassessed rustup input: %s; refusing toolchain acquisition.\n' "${rejected_inputs[@]}" >&2
    exit 1
  fi
  printf 'PASS rustup prerequisites: %s; reqwest/rustls platform verifier; https://static.rust-lang.org\n' "$rustup_version"
fi
