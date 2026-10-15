"""Run the install wrapper against simulated npm failures without registry access."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("npm-ci.sh")


class NpmInstallTests(unittest.TestCase):
    def run_install(self, code, failures=1, status=152):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            binary = root / "bin"
            binary.mkdir()
            package = root / "package with spaces"
            package.mkdir()
            npm = binary / "npm"
            npm.write_text("""#!/usr/bin/env bash
set -eu
test "$PWD" = "$PACKAGE_DIR"
test "$*" = 'ci --no-fund'
count=0
if [[ -f "$STATE" ]]; then read -r count < "$STATE"; fi
count=$((count + 1))
echo "$count" > "$STATE"
if ((count <= FAILURES)); then
    echo "npm error code $ERROR_CODE" >&2
    exit "$ERROR_STATUS"
fi
echo 'installed successfully'
""")
            npm.chmod(0o755)
            sleep = binary / "sleep"
            sleep.write_text('#!/bin/sh\nprintf "%s\\n" "$1" >> "$DELAYS"\n')
            sleep.chmod(0o755)
            state = root / "state"
            delays = root / "delays"
            result = subprocess.run(
                ["bash", str(SCRIPT), str(package)], text=True, capture_output=True,
                timeout=10, env={**os.environ, "PATH": f"{binary}:{os.environ['PATH']}",
                                 "STATE": str(state), "DELAYS": str(delays),
                                 "PACKAGE_DIR": str(package), "ERROR_CODE": code,
                                 "FAILURES": str(failures), "ERROR_STATUS": str(status)},
            )
            return result, int(state.read_text()), delays.read_text().splitlines() if delays.exists() else []

    def test_success_needs_no_retry(self):
        result, attempts, delays = self.run_install("ECONNRESET", failures=0)
        self.assertEqual((result.returncode, attempts, delays), (0, 1, []))

    def test_transient_network_failure_recovers(self):
        for code in ["ECONNRESET", "ETIMEDOUT", "EAI_AGAIN", "ENETUNREACH",
                     "EHOSTUNREACH", "ECONNREFUSED", "ERR_SOCKET_TIMEOUT", "EPIPE"]:
            with self.subTest(code=code):
                result, attempts, delays = self.run_install(code, failures=2)
                self.assertEqual((result.returncode, attempts, delays), (0, 3, ["10", "20"]))
                self.assertIn(f"npm error code {code}", result.stdout)

    def test_persistent_network_failure_preserves_exit_status(self):
        result, attempts, delays = self.run_install("ECONNRESET", failures=10)
        self.assertEqual((result.returncode, attempts, delays), (152, 3, ["10", "20"]))

    def test_non_network_errors_fail_immediately(self):
        for code in ["EUSAGE", "EINTEGRITY", "ERESOLVE", "E401", "1"]:
            with self.subTest(code=code):
                result, attempts, delays = self.run_install(code, failures=10, status=1)
                self.assertEqual((result.returncode, attempts, delays), (1, 1, []))


if __name__ == "__main__":
    unittest.main()
