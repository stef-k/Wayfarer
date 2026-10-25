#!/usr/bin/env bash
# Bootstrap before the venv's first dependency fetch. The Python security notice and
# PyPI's independent wheel metadata bind these bytes; TLS verification stays on.
set -euo pipefail
"$1" -m pip --isolated --disable-pip-version-check --no-cache-dir install \
  --no-index --no-deps --only-binary=:all: \
  'https://files.pythonhosted.org/packages/3f/06/3e4420302363fbb71be3c7f35ce042f54bfca330e3e66b3a2e3c4cca579f/pip-26.2.2-py3-none-any.whl#sha256=fd7f4dfa8190e014013b9f08f3e973f4e4c26ffd179a204895533a7254813a70'
"$1" -m pip --version
"$1" - <<'PY'
"""Inspect the installed vendored bundle, including a positive public-root control."""
import hashlib
import ssl
import pip
from pip._vendor import certifi

assert pip.__version__ == '26.2.2'
store = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
store.load_verify_locations(certifi.where())
roots = {hashlib.sha256(c).hexdigest() for c in store.get_ca_certs(binary_form=True)}
assert '86a1ecba089c4a8d3bbe2734c612ba341d813e043cf9e8a862cd5c57a36bbe6b' not in roots, 'emSign G3 still trusted'
assert '96bcec06264976f37460779acf28c5a7cfe8a3c0aae11a8ffcee05c0bddf08c6' in roots, 'ISRG Root X1 missing'
print('PASS pip vendored roots:', certifi.where(), len(roots))
PY
