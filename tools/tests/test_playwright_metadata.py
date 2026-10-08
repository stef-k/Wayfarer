"""Fail-closed Chromium cache metadata validation against disposable generated payloads."""

import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from playwright_metadata import chromium_revision


class MetadataTests(unittest.TestCase):
    """Prove exact file/entry ownership and bounded cache-key output."""

    def test_valid_nested_metadata_and_invalid_shapes(self):
        """Do not infer a revision from missing, duplicate, malformed or nonnumeric evidence."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            metadata = root / 'nested' / 'browsers.json'
            metadata.parent.mkdir()
            with self.assertRaises(ValueError):
                chromium_revision(root)
            valid = {'browsers': [{'name': 'chromium', 'revision': '1243'}, {'name': 'firefox'}]}
            metadata.write_text(json.dumps(valid))
            self.assertEqual(chromium_revision(root), '1243')
            duplicate = root / 'browsers.json'
            duplicate.write_text(json.dumps(valid))
            with self.assertRaises(ValueError):
                chromium_revision(root)
            duplicate.unlink()
            for content in ('{', '[]', '{}', '{"browsers":{}}', '{"browsers":[null]}',
                            '{"browsers":[]}', json.dumps({'browsers': valid['browsers'] * 2})):
                metadata.write_text(content)
                with self.subTest(content=content), self.assertRaises(ValueError):
                    chromium_revision(root)
            for revision in (None, 1243, '', ' 1243', '1243\nother=true', '1.2', '\u0661'):
                metadata.write_text(json.dumps({'browsers': [{'name': 'chromium', 'revision': revision}]}))
                with self.subTest(revision=revision), self.assertRaises(ValueError):
                    chromium_revision(root)


if __name__ == '__main__':
    unittest.main()
