#!/usr/bin/env python3
"""Refresh the native service template while retaining installed key-ring authority."""

import pathlib
import shlex
import sys


def refresh(template: pathlib.Path, installed: pathlib.Path) -> None:
    """Preserve exact Environment lines containing the installed compatibility override.

    Parsing is completed before writing, so malformed quoted assignments fail closed.
    Keeping the whole assignment line also preserves systemd quoting and ordering.
    Drop-ins remain managed by systemd and are never rewritten here.
    """
    overrides = []
    section = ""
    if installed.exists():
        for line in installed.read_text().splitlines(keepends=True):
            stripped = line.strip()
            if stripped.startswith("["):
                section = stripped
            if section != "[Service]" or not stripped.startswith("Environment="):
                continue
            assignments = shlex.split(stripped.removeprefix("Environment="), comments=False)
            if any(value.startswith("DataProtection__KeyRingPath=") for value in assignments):
                overrides.append(line if line.endswith("\n") else line + "\n")
    content = template.read_text()
    if content.count("[Service]\n") != 1:
        raise ValueError("Service template must contain one Service section")
    installed.write_text(content.replace("[Service]\n", "[Service]\n" + "".join(overrides), 1))


if __name__ == "__main__":
    refresh(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]))
