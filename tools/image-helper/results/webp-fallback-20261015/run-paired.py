#!/usr/bin/env python3
"""Run one bounded fallback matrix through the unchanged retained paired collector."""
import importlib.util
import json
import os
from pathlib import Path

ROOT = Path(os.environ['WAYFARER_IMAGE_RESEARCH'])
SPEC = importlib.util.spec_from_file_location('retained', ROOT / 'source/tools/image-helper/results/readiness-20261015/qualify-paired.py')
COLLECTOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COLLECTOR)
COLLECTOR.CELLS = [(profile, fixture) for profile in ('web', 'pdf')
                   for fixture in ('photo.webp', 'large-vilaplana.webp')]
COLLECTOR.main()
for profile in ('web', 'pdf'):
    COLLECTOR.collect(profile, 'photo.jpg', 'warm', 1)
    print(f'JPEG smoke: {profile}', flush=True)
group = Path('/proc/self/cgroup').read_text().strip().split(':')[-1]
path = Path('/sys/fs/cgroup' + group)
(ROOT / 'evidence/paired-cgroup.json').write_text(json.dumps({
    name: (path / name).read_text() for name in
    ('memory.peak', 'memory.max', 'memory.events', 'cpu.stat', 'pids.max', 'cpu.max')
}, indent=2) + '\n')
