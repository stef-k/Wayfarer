"""Deterministic distribution bytes and external checksum boundary."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tarfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import bundle


def test_archive_is_reproducible_and_checksum_external(tmp_path):
    """Location and mtime do not affect archive identity; inventory has no self-checksum."""
    source = tmp_path / 'source'
    source.mkdir()
    for name in bundle.PAYLOADS:
        path = source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(name)
    (source / 'release.json').write_text(json.dumps({'Version': '1.9.19', 'SourceRevision': 'a' * 40, 'Files': [{'Path': name} for name in bundle.PAYLOADS]}))
    first, second = tmp_path / 'first', tmp_path / 'second'
    first.mkdir()
    second.mkdir()
    a = bundle.archive(source, first)
    b = bundle.archive(source, second)
    assert a.read_bytes() == b.read_bytes()
    assert (first / 'SHA256SUMS').read_text() == f'{bundle.digest(a)}  {a.name}\n'
    with tarfile.open(a) as archive:
        assert archive.getnames() == sorted((*bundle.PAYLOADS, 'release.json'))
        assert all(item.isfile() and item.mode == bundle.mode(item.name) for item in archive)
    assert a.name.startswith('wayfarer-candidate-')
