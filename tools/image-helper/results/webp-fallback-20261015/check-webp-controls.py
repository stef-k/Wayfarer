#!/usr/bin/env python3
"""Replay retained WebP controls and two upstream malformed inputs through the actual CLI."""
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess

from PIL import Image, ImageOps

ROOT = Path(os.environ['WAYFARER_IMAGE_RESEARCH'])
FIXTURES = ROOT / 'evidence/fixtures'
CONTROLS = ROOT / 'evidence/controls'
OUTPUTS = ROOT / 'outputs/controls'
OUTPUTS.mkdir(parents=True, exist_ok=True)
LIMITS = ['/usr/bin/prlimit', '--as=1073741824:1073741824', '--cpu=20:25',
          '--nofile=128:128', '--core=0:0', '--', os.environ['WAYFARER_IMAGE_HELPER']]
RECIPE = ['transform', '--width', '600', '--height', '8192', '--quality', '85',
          '--output-format', 'auto', '--max-input-bytes', '52428800',
          '--max-output-bytes', '52428800']


def invoke(name, data, expected, overrides=None):
    """Require exact failure/output behavior; independently load every successful derivative."""
    recipe = RECIPE.copy()
    for option, value in (overrides or {}).items():
        recipe[recipe.index(option) + 1] = str(value)
    command = LIMITS + recipe
    result = subprocess.run(command, input=data, capture_output=True, timeout=30,
                            env={**os.environ, 'WAYFARER_IMAGE_PARENT_PID': str(os.getpid())})
    row = {'control': name, 'source_sha256': hashlib.sha256(data).hexdigest(),
           'source_bytes': len(data), 'exit': result.returncode, 'command': command,
           'output_bytes': len(result.stdout), 'stderr': result.stderr.decode()}
    if expected is not None:
        assert result.returncode == expected, row
    if result.returncode:
        assert not result.stdout, row
    else:
        with Image.open(io.BytesIO(result.stdout)) as image:
            image.load()
            assert getattr(image, 'n_frames', 1) == 1 and not image.getexif() and not image.info.get('xmp')
            assert image.info.get('icc_profile')
            row.update(geometry=image.size, format=image.format,
                       alpha_range=image.convert('RGBA').getchannel('A').getextrema())
        destination = OUTPUTS / (name + '.bin')
        destination.write_bytes(result.stdout)
        row['output_sha256'] = hashlib.sha256(result.stdout).hexdigest()
    return row


rows = []
for name in ('static.webp', 'rgb-icc.webp'):
    rows.append(invoke(name, (FIXTURES / name).read_bytes(), 0))
assert rows[0]['format'] == 'PNG' and rows[0]['alpha_range'][0] == 0
for name in ('animated.webp', 'truncated.webp', 'metadata.webp'):
    rows.append(invoke(name, (FIXTURES / name).read_bytes(), 4))
source = (FIXTURES / 'static.webp').read_bytes()
rows.append(invoke('alpha-explicit-jpeg', source, 4, {'--output-format': 'jpeg'}))
rows.append(invoke('input-ceiling', source, 3, {'--max-input-bytes': len(source) - 1}))
rows.append(invoke('output-ceiling', source, 70, {'--max-output-bytes': 100}))
rows.append(invoke('missing-tail', source[:-1], 4))
over_width = bytearray(source)
over_width[24:27] = (8193 - 1).to_bytes(3, 'little')
rows.append(invoke('axis-ceiling', over_width, 3))
over_pixels = bytearray(source)
over_pixels[24:27] = (4000 - 1).to_bytes(3, 'little')
over_pixels[27:30] = (3001 - 1).to_bytes(3, 'little')
rows.append(invoke('pixel-ceiling', over_pixels, 3))
lossless_animation = bytearray((FIXTURES / 'rgb-icc.webp').read_bytes())
lossless_animation[20] |= 0x02
rows.append(invoke('vp8l-animation-declaration', lossless_animation, 4))
# Preserve existing lossless/alpha/ICC payload bytes; append explicit EXIF and XMP controls.
metadata = bytearray((FIXTURES / 'rgb-icc.webp').read_bytes())
assert metadata[12:16] == b'VP8X'
metadata[20] |= 0x0c
with Image.open(FIXTURES / 'orientation-6.png') as oriented:
    exif = oriented.getexif().tobytes()
for tag, payload in ((b'EXIF', exif), (b'XMP ', b'private GPS control')):
    metadata += tag + len(payload).to_bytes(4, 'little') + payload + b'\0' * (len(payload) % 2)
metadata[4:8] = (len(metadata) - 8).to_bytes(4, 'little')
(CONTROLS / 'icc-exif-xmp.webp').write_bytes(metadata)
rows.append(invoke('icc-exif-xmp', metadata, 0))
assert rows[-1]['geometry'] == (32, 64)
for name in ('upstream-huffman-overflow.webp', 'upstream-partition-claim.webp'):
    rows.append(invoke(name, (FIXTURES / name).read_bytes(), 4))
# Only the RIFF/VP8L lengths and mandatory pad differ from the published 235-byte Huffman control.
rows.append(invoke('huffman-exact-extent', (CONTROLS / 'huffman-exact-extent.webp').read_bytes(), None))
(ROOT / 'evidence/webp-controls.json').write_text(json.dumps(rows, indent=2) + '\n')
print(json.dumps([{k: v for k, v in row.items() if k in ('control', 'exit', 'output_bytes', 'geometry')}
                  for row in rows], indent=2))
