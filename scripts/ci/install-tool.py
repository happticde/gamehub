"""Install reviewed Linux scanner/release binaries after checking a committed SHA-256."""

import hashlib
import io
import json
import os
from pathlib import Path
import platform
import sys
import tarfile
import urllib.request


def extract_binary(archive, tool, expected_digest):
    if hashlib.sha256(archive).hexdigest() != expected_digest:
        raise ValueError("Tool archive checksum mismatch")
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as bundle:
        candidates = [entry for entry in bundle.getmembers()
                      if entry.isfile() and Path(entry.name).name == tool]
        if len(candidates) != 1:
            raise ValueError("Expected exactly one regular tool binary")
        return bundle.extractfile(candidates[0]).read()


def main():
    tool, destination = sys.argv[1:]
    lock = json.loads((Path(__file__).resolve().parents[2] / ".ci/tools.json").read_text())
    spec = lock[tool]
    architecture = {"x86_64": "amd64", "aarch64": "arm64"}[platform.machine()]
    asset, digest = spec[architecture]
    url = f"https://github.com/{spec['repository']}/releases/download/v{spec['version']}/{asset}"
    with urllib.request.urlopen(url, timeout=120) as response:
        binary = extract_binary(response.read(), tool, digest)
    target = Path(destination) / tool
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(binary)
    target.chmod(0o755)
    if os.environ.get("GITHUB_PATH"):
        with open(os.environ["GITHUB_PATH"], "a", encoding="utf-8") as output:
            output.write(str(target.parent.resolve()) + "\n")
    print(f"Installed verified {tool} {spec['version']}")


if __name__ == "__main__":
    main()
