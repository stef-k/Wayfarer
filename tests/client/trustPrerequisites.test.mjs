/** Exercise the native/CI guard with real Node trust selection and disposable stores. */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { X509Certificate } from 'node:crypto';
import { rootCertificates } from 'node:tls';
import { spawnSync } from 'node:child_process';

// Assessed public G3 certificate; no private key or external download is needed.
const g3 = `-----BEGIN CERTIFICATE-----
MIICTjCCAdOgAwIBAgIKPPYHqWhwDtqLhDAKBggqhkjOPQQDAzBrMQswCQYDVQQG
EwJJTjETMBEGA1UECxMKZW1TaWduIFBLSTElMCMGA1UEChMcZU11ZGhyYSBUZWNo
bm9sb2dpZXMgTGltaXRlZDEgMB4GA1UEAxMXZW1TaWduIEVDQyBSb290IENBIC0g
RzMwHhcNMTgwMjE4MTgzMDAwWhcNNDMwMjE4MTgzMDAwWjBrMQswCQYDVQQGEwJJ
TjETMBEGA1UECxMKZW1TaWduIFBLSTElMCMGA1UEChMcZU11ZGhyYSBUZWNobm9s
b2dpZXMgTGltaXRlZDEgMB4GA1UEAxMXZW1TaWduIEVDQyBSb290IENBIC0gRzMw
djAQBgcqhkjOPQIBBgUrgQQAIgNiAAQjpQy4LRL1KPOxst3iAhKAnjlfSU2fySU0
WXTsuwYc58Byr+iuL+FBVIcUqEqy6HyC5ltqtdyzdc6LBtCGI79G1Y4PPwT01xyS
fvalY8L1X44uT6EYGQIrMgqCZH0Wk9GjQjBAMB0GA1UdDgQWBBR8XQKEE9TMipuB
zhccLikenEhjQjAOBgNVHQ8BAf8EBAMCAQYwDwYDVR0TAQH/BAUwAwEB/zAKBggq
hkjOPQQDAwNpADBmAjEAvvNhzwIQHWSVB7gYboiFBS+DCBeQyh+KTOgNG3qxrdWB
CUfvO6wIBHxcmbHtRwfSAjEAnbpV/KlK6O3t5nYBQnvI+GDZjVGLVTv7jHvrZQnD
+JbNR6iC8hZVdyR+EhCVBCyj
-----END CERTIFICATE-----
`;
const x1 = rootCertificates.find(pem => new X509Certificate(pem).fingerprint256 ===
  '96:BC:EC:06:26:49:76:F3:74:60:77:9A:CF:28:C5:A7:CF:E8:A3:C0:AA:E1:1A:8F:FC:EE:05:C0:BD:DF:08:C6');
const guard = fileURLToPath(new URL('../../deployment/check-trust.sh', import.meta.url));

test('trust guard checks OpenSSL and bundled Node defaults including extra roots', t => {
  assert.ok(x1, 'the supported Node retains the ISRG X1 positive control');
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'wayfarer-trust-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const bundle = path.join(directory, 'ca.pem');
  const extra = path.join(directory, 'extra.pem');
  const caDirectory = path.join(directory, 'certs');
  fs.mkdirSync(caDirectory);
  for (const selection of ['--use-openssl-ca', '--use-bundled-ca', '--use-system-ca']) {
    for (const addition of ['', x1, g3]) {
      fs.writeFileSync(bundle, x1);
      fs.writeFileSync(extra, addition);
      const result = spawnSync('bash', [guard, '--node'], {
        encoding: 'utf8', timeout: 15000,
        env: { ...process.env, PATH: `${path.dirname(process.execPath)}:${process.env.PATH}`,
          SSL_CERT_FILE: bundle, SSL_CERT_DIR: caDirectory,
          NODE_OPTIONS: selection, NODE_USE_SYSTEM_CA: '0', NODE_EXTRA_CA_CERTS: extra }
      });
      const diagnostic = `${selection}: ${result.stdout}${result.stderr}`;
      if (addition === g3) {
        assert.equal(result.status, 1, diagnostic);
        assert.match(result.stderr, /Node trust prerequisites failed/, diagnostic);
      } else {
        assert.equal(result.status, 0, diagnostic);
        assert.match(result.stdout, /PASS Node prerequisites/, diagnostic);
      }
    }
  }
  // Empty/unusable stores cannot pass on the strength of Node's bundled roots.
  for (const contents of ['', 'unusable certificate data', x1 + g3]) {
    fs.writeFileSync(bundle, contents);
    const result = spawnSync('bash', [guard, '--node'], {
      encoding: 'utf8', timeout: 15000,
      env: { ...process.env, SSL_CERT_FILE: bundle, SSL_CERT_DIR: caDirectory,
        NODE_OPTIONS: '--use-openssl-ca', NODE_EXTRA_CA_CERTS: '' }
    });
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /Host trust is unqualified|G3 is still trusted/);
  }
});
