"""Validate the built .NET Playwright Chromium revision for its existing CI cache key."""

import argparse
import json
import os
from pathlib import Path
import re

from test_artifact_paths import ordinary


def chromium_revision(root):
    """Require one ordinary metadata file and one Chromium entry with an ASCII revision."""
    root = ordinary(root, tree=True)
    files = list(root.rglob('browsers.json'))
    if len(files) != 1 or not files[0].is_file():
        raise ValueError(f'Expected exactly one generated browsers.json under {root}; found {len(files)}')
    try:
        metadata = json.loads(files[0].read_text(encoding='utf-8'))
    except (OSError, ValueError) as error:
        raise ValueError('Generated .NET Playwright metadata is not readable JSON') from error
    browsers = metadata.get('browsers') if isinstance(metadata, dict) else None
    if not isinstance(browsers, list) or not all(isinstance(browser, dict) for browser in browsers):
        raise ValueError('Generated .NET Playwright browsers must be a list of objects')
    chromium = [browser for browser in browsers if browser.get('name') == 'chromium']
    if len(chromium) != 1:
        raise ValueError(f'Expected exactly one Chromium entry; found {len(chromium)}')
    revision = chromium[0].get('revision')
    if not isinstance(revision, str) or not re.fullmatch(r'[0-9]+', revision):
        raise ValueError('Generated .NET Playwright Chromium revision is empty or malformed')
    return revision


def main():
    """Write only a validated numeric revision to stdout and the Actions output file."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('metadata_root', type=Path)
    args = parser.parse_args()
    try:
        output = f'chromium_revision={chromium_revision(args.metadata_root)}'
    except (OSError, ValueError) as error:
        parser.exit(1, f'{error}\n')
    if os.environ.get('GITHUB_OUTPUT'):
        with Path(os.environ['GITHUB_OUTPUT']).open('a', encoding='utf-8') as stream:
            stream.write(output + '\n')
    print(output)


if __name__ == '__main__':
    main()
