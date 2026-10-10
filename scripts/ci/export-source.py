"""Copy only tracked scan inputs, without opening user-owned environment files."""

from pathlib import Path
import shutil
import subprocess
import sys


def is_scan_input(path):
    parts = Path(path).parts
    return not any(part == ".env" or part.startswith(".env.") or part in {
        ".git", "node_modules", "bin", "obj", ".next", ".devcontainer"
    } for part in parts)


def main():
    destination = Path(sys.argv[1]).resolve()
    root = Path(__file__).resolve().parents[2]
    if destination == root or root in destination.parents:
        raise ValueError("Scan snapshot must be outside the checkout")
    files = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
    for name in files:
        if not name or not is_scan_input(name):
            continue
        source = root / name
        if source.is_symlink():
            raise ValueError("Symlinks are not supported in scan snapshots")
        if not source.is_file():
            continue
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)


if __name__ == "__main__":
    main()
