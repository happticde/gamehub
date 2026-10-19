"""Package immutable, tracked release source without opening environment files."""
# SPDX-FileCopyrightText: 2026 jfk1987 and GameHub contributors
# SPDX-License-Identifier: AGPL-3.0-only
import gzip
import io
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tarfile


def include_source(name):
    parts = PurePosixPath(name).parts
    return bool(parts) and not any(part.startswith('.env') or part in {
        '.git', 'node_modules', 'bin', 'obj', '.next', '__pycache__'
    } for part in parts)


def package_source(root, destination):
    root, destination = Path(root).resolve(), Path(destination).resolve()
    if destination == root or root in destination.parents:
        raise ValueError('Write release archives outside the checkout')
    revision = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    entries = subprocess.check_output(['git', 'ls-tree', '-rz', '--full-tree', revision], cwd=root)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open('wb') as output, gzip.GzipFile(filename='', mode='wb', fileobj=output, mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode='w|') as archive:
            for entry in entries.split(b'\0'):
                if not entry:
                    continue
                metadata, name = entry.decode().split('\t', 1)
                if not include_source(name):
                    continue
                mode, kind, oid = metadata.split()
                if kind != 'blob' or mode not in {'100644', '100755'}:
                    raise ValueError(f'Unsupported source entry: {name}')
                content = subprocess.check_output(['git', 'cat-file', 'blob', oid], cwd=root)
                info = tarfile.TarInfo(f'gamehub-{revision}/{name}')
                info.size, info.mode = len(content), int(mode, 8) & 0o777
                archive.addfile(info, io.BytesIO(content))
            content = (f'Source commit: {revision}\n'
                       'Environment files are deliberately omitted. Supply your own secret configuration; '
                       'see docs/guides/configuration.md.\n').encode()
            info = tarfile.TarInfo(f'gamehub-{revision}/SOURCE-README.txt')
            info.size = len(content)
            archive.addfile(info, io.BytesIO(content))
    return revision


if __name__ == '__main__':
    print(package_source(Path(__file__).resolve().parents[2], sys.argv[1]))
