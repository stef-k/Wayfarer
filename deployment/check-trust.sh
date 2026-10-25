#!/usr/bin/env bash
# Read-only prerequisite check before native/CI package acquisition. Pass --node
# for the supported build/provisioning Node; runtime-only hosts need only OS roots.
set -euo pipefail
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
