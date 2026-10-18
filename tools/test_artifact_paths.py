"""Ordinary filesystem checks and deletion authority for freshly created browser roots.

There is deliberately no delete-by-path CLI. A retained owner can remove only the
UUID child it created, after the supervisor has proved its writers have stopped.
"""

import json
import os
from pathlib import Path
import shutil
import stat
import tempfile
import uuid


def ordinary(path: Path, *, tree=False):
    """Inspect lexical ancestors and optionally descendants without resolving links."""
    if '..' in Path(path).parts:
        raise ValueError('Parent traversal in an artifact path is refused')
    path = Path(os.path.abspath(path))
    for item in (*reversed(path.parents), path):
        if not os.path.lexists(item):
            continue
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError(f'Linked/reparse artifact refused: {item}')
        if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
            raise ValueError(f'Special artifact refused: {item}')
        if item != path and not stat.S_ISDIR(info.st_mode):
            raise ValueError(f'Artifact ancestor is not a directory: {item}')
    if tree and path.is_dir():
        for item in path.iterdir():
            ordinary(item, tree=True)
    return path


class BrowserRoot:
    """Retains creation proof for exactly one private OS-temp UUID directory."""

    def __init__(self, profile, supervisor):
        self.run_id = str(uuid.uuid4())
        self._parent = ordinary(Path(tempfile.gettempdir()))
        self.path = self._parent / f'wayfarer-browser-e2e-{self.run_id}'
        self.path.mkdir(mode=0o700)
        self._identity = self.path.stat().st_dev, self.path.stat().st_ino
        self._marker = {'run': self.run_id, 'profile': profile, 'supervisor': supervisor}
        self.marker = self.path / 'owner.json'
        with self.marker.open('x', encoding='utf-8') as stream:
            json.dump(self._marker, stream)
        self._removed = False
        self._diagnostic = None

    def verify(self):
        """Refuse foreign roots, replacement directories and changed creation markers."""
        if self.path != self._parent / f'wayfarer-browser-e2e-{self.run_id}':
            raise ValueError('Foreign browser root refused')
        ordinary(self.path, tree=True)
        if not self.path.is_dir() or (self.path.stat().st_dev, self.path.stat().st_ino) != self._identity:
            raise ValueError('Browser root creation identity changed')
        if json.loads(self.marker.read_text(encoding='utf-8')) != self._marker:
            raise ValueError('Browser ownership marker changed')

    def append_diagnostic(self, diagnostic):
        """Write only through pinned owned descriptors; refuse unsafe platform fallbacks."""
        self.verify()
        if self._diagnostic is None:
            if os.open not in os.supports_dir_fd or not hasattr(os, 'O_NOFOLLOW'):
                raise NotImplementedError('Pinned diagnostic storage is unavailable')
            directory = os.open(self.path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                info = os.fstat(directory)
                if (info.st_dev, info.st_ino) != self._identity:
                    raise ValueError('Browser root creation identity changed')
                flags = os.O_WRONLY | os.O_APPEND | os.O_NOFOLLOW | os.O_CREAT | os.O_EXCL
                descriptor = os.open('supervisor.log', flags, 0o600, dir_fd=directory)
                # Retain the file itself so unlink/recreation cannot recycle its identity.
                self._diagnostic = os.fdopen(descriptor, 'a', encoding='utf-8')
            finally:
                os.close(directory)
        else:
            target = (self.path / 'supervisor.log').lstat()
            if not os.path.samestat(target, os.fstat(self._diagnostic.fileno())):
                raise ValueError('Diagnostic target creation identity changed')
        info = os.fstat(self._diagnostic.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
            raise ValueError('Diagnostic target is not a private ordinary file')
        self._diagnostic.write(diagnostic)
        self._diagnostic.flush()

    def remove(self):
        """Delete the verified created root once; never adopt a recreated stale path."""
        if self._removed:
            if os.path.lexists(self.path):
                raise ValueError('Finalized browser root was recreated')
            return
        self.verify()
        if self._diagnostic is not None:
            self._diagnostic.close()
        shutil.rmtree(self.path)
        self._removed = True
