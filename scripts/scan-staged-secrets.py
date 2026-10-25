"""Scan exact staged blobs without reading working files or environment-file contents."""

import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys


def git(*arguments):
    return subprocess.check_output(["git", *arguments], stderr=subprocess.PIPE)


def is_environment_file(path):
    name = PurePosixPath(path).name
    return name == ".env" or name.startswith(".env.")


def scanner(root):
    version = json.loads((root / ".ci/tools.json").read_text())["gitleaks"]["version"]
    if shutil.which("gitleaks"):
        command = ["gitleaks"]
        config = str(root / ".gitleaks.toml")
    else:
        # Docker supplies the same checksum-verified tool used by CI on host checkouts.
        container = git("config", "--get", "gamehub.gitleaksContainer").decode().strip()
        command = ["docker", "exec", "-i", "-w", "/workspaces/gamehub", container,
                   "/tmp/gamehub-ci-tools/gitleaks"]
        config = ".gitleaks.toml"
    result = subprocess.run([*command, "version"], capture_output=True, text=True)
    if result.returncode or result.stdout.strip() != version or result.stderr:
        raise ValueError(f"Gitleaks {version} is required. See CONTRIBUTING.md for setup.")
    return [*command, "stdin", "--config", config, "--redact=100", "--no-banner",
            "--log-level", "error", "--ignore-gitleaks-allow"]


def main():
    root = Path(git("rev-parse", "--show-toplevel").decode().strip())
    if sys.argv[1:] == ["--check-tool"]:
        scanner(root)
        return 0
    if sys.argv[1:]:
        raise ValueError("Usage: scan-staged-secrets.py [--check-tool]")
    if git("ls-files", "--unmerged", "-z"):
        raise ValueError("Resolve unmerged files before committing.")
    # Disable renames so moving an unchanged secret into a new path is scanned too.
    paths = git("diff", "--cached", "--name-only", "--no-renames", "-z",
                "--diff-filter=ACMRTUXB", "--").split(b"\0")
    paths = [path for path in paths if path]
    blocked = [path for path in paths if is_environment_file(path.decode(errors="surrogateescape"))]
    if blocked:
        for path in blocked:
            print(f"Commit blocked: environment file {path!r} must not be staged.", file=sys.stderr)
        return 1
    if not paths:
        return 0
    command = scanner(root)
    for path in paths:
        # Read the index, even if the working file was changed or removed after staging.
        # A staged symlink is scanned as link text; its target is never opened.
        content = git("cat-file", "blob", b":" + path)
        result = subprocess.run(command, input=content, capture_output=True)
        if result.returncode or result.stderr:
            # Never forward scanner output or Git errors: they could contain credentials.
            print(f"Commit blocked: secret scan failed for staged file {path!r}. "
                  "Remove staged credentials or repair the scanner, then retry.", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, subprocess.SubprocessError):
        print("Commit blocked: the secret scan could not run. See CONTRIBUTING.md for setup.",
              file=sys.stderr)
        sys.exit(1)
