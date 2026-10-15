"""Exercise checksum rejection, source boundaries and real git-cliff version decisions."""

import hashlib
from datetime import date
import importlib.util
import io
import json
import secrets
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


def load_module(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ToolBoundaryTests(unittest.TestCase):
    def test_trivy_gate_blocks_fixable_vulnerabilities_and_failed_high_configuration(self):
        gate = load_module("gate-trivy")
        report = {"SchemaVersion": 2, "Results": [{"Target": "image", "Vulnerabilities": [
            {"Severity": "CRITICAL", "FixedVersion": "2.0", "VulnerabilityID": "fixed-critical"},
            {"Severity": "HIGH", "VulnerabilityID": "unfixed-high"},
            {"Severity": "MEDIUM", "FixedVersion": "2.0", "VulnerabilityID": "fixed-medium"},
        ], "Misconfigurations": [
            {"Severity": "HIGH", "Status": "FAIL", "ID": "bad-config"},
            {"Severity": "HIGH", "Status": "PASS", "ID": "good-config"},
        ]}]}
        self.assertEqual(gate.blocking_findings(report), [("image", "fixed-critical"), ("image", "unfixed-high"), ("image", "bad-config")])
        with self.assertRaises(ValueError):
            gate.blocking_findings({"SchemaVersion": 2, "Results": []})

    def test_unfixed_high_exception_is_scoped_expires_and_never_hides_a_fix_or_critical(self):
        gate = load_module("gate-trivy")
        exception = {"vulnerability": "CVE-test", "package": "braces", "version": "3.0.3",
                     "targets": ["frontend/package-lock.json"], "expires": "2026-11-14",
                     "owner": "Maintainers", "reason": "No patch; reviewed build-only path"}
        vulnerability = {"Severity": "HIGH", "VulnerabilityID": "CVE-test",
                         "PkgName": "braces", "InstalledVersion": "3.0.3"}
        result = {"Target": "frontend/package-lock.json", "Vulnerabilities": [vulnerability]}
        report = {"SchemaVersion": 2, "Results": [result]}
        self.assertEqual(gate.blocking_findings(report, [exception], date(2026, 10, 15)), [])
        self.assertTrue(gate.blocking_findings(report, [exception], date(2026, 11, 15)))
        for change in [{"FixedVersion": "3.0.4"}, {"Severity": "CRITICAL"}, {"InstalledVersion": "3.0.2"}]:
            modified = {"SchemaVersion": 2, "Results": [{**result, "Vulnerabilities": [{**vulnerability, **change}]}]}
            self.assertTrue(gate.blocking_findings(modified, [exception], date(2026, 10, 15)))
        result["Target"] = "runtime-image"
        self.assertTrue(gate.blocking_findings(report, [exception], date(2026, 10, 15)))

    def test_corrupt_tool_archive_is_rejected_before_extraction(self):
        with self.assertRaisesRegex(ValueError, "checksum"):
            load_module("install-tool").extract_binary(b"untrusted archive", "scanner", "0" * 64)

    def test_only_verified_regular_binary_is_extracted(self):
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w:gz") as bundle:
            binary = b"verified binary"
            entry = tarfile.TarInfo("nested/scanner")
            entry.size = len(binary)
            bundle.addfile(entry, io.BytesIO(binary))
        data = archive.getvalue()
        self.assertEqual(load_module("install-tool").extract_binary(
            data, "scanner", hashlib.sha256(data).hexdigest()), binary)

    def test_environment_and_generated_paths_are_excluded_without_opening_files(self):
        export = load_module("export-source")
        for path in [".env", ".env.example", "frontend/.env.local", "frontend/node_modules/a.js",
                     "services/core/obj/project.assets.json", ".devcontainer/Dockerfile"]:
            self.assertFalse(export.is_scan_input(path), path)
        for path in ["frontend/package-lock.json", "services/core/Dockerfile", "services/cli/go.sum"]:
            self.assertTrue(export.is_scan_input(path), path)


@unittest.skipUnless(shutil.which("gitleaks"), "Install the pinned gitleaks to run history integration tests")
class SecretScanTests(unittest.TestCase):
    def test_missing_git_history_is_a_failure_not_a_clean_scan(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            result = subprocess.run(["bash", str(ROOT / "scripts/ci/scan-secrets.sh"), str(root / "report.json")],
                                    cwd=root, text=True, capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse((root / "report.json").exists())

    def test_new_secret_in_documentation_is_blocked_and_fully_redacted(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in [".gitleaks.toml", ".gitleaksignore"]:
                shutil.copyfile(ROOT / name, root / name)
            def git(*arguments):
                subprocess.check_call(["git", *arguments], cwd=root, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            git("init")
            git("config", "user.name", "Security test")
            git("config", "user.email", "security-test@example.invalid")
            fake_key = "ghp_" + secrets.token_hex(18)
            (root / "README.md").write_text(f'github_api_key = "{fake_key}"\n')
            git("add", "README.md")
            git("commit", "-m", "test: synthetic secret in documentation")
            report = root / "report.json"
            result = subprocess.run(["bash", str(ROOT / "scripts/ci/scan-secrets.sh"), str(report)],
                                    cwd=root, text=True, capture_output=True)
            self.assertEqual(result.returncode, 1, result.stderr)
            serialized = report.read_text()
            self.assertNotIn(fake_key, serialized + result.stdout + result.stderr)
            self.assertEqual(json.loads(serialized)[0]["File"], "README.md")


@unittest.skipUnless(shutil.which("git-cliff"), "Install the pinned git-cliff to run release integration tests")
class ReleaseVersionTests(unittest.TestCase):
    def prepare(self, message, tagged=True, bump="auto"):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        shutil.copyfile(ROOT / "cliff.toml", root / "cliff.toml")
        def git(*arguments):
            return subprocess.check_output(["git", *arguments], cwd=root, stderr=subprocess.DEVNULL)
        git("init")
        git("config", "user.name", "Release test")
        git("config", "user.email", "release-test@example.invalid")
        git("add", "cliff.toml")
        git("commit", "-m", "chore: initial configuration")
        if tagged:
            git("tag", "v1.2.3")
        if message:
            name = "README.md" if message.startswith("docs:") else "application.cs"
            (root / name).write_text(message)
            git("add", name)
            git("commit", "-m", message)
        result = subprocess.run(["bash", str(ROOT / "scripts/ci/prepare-release.sh"), bump, str(root / "metadata")],
                                cwd=root, text=True, capture_output=True)
        return root, git, result

    def test_conventional_and_legacy_changes_produce_expected_versions(self):
        for message, expected in [("fix: repair helper", "v1.2.4"), ("feat: add provider", "v1.3.0"),
                                  ("feat!: remove old API", "v2.0.0"), ("Repair the helper", "v1.2.4")]:
            with self.subTest(message=message):
                root, git, result = self.prepare(message)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual((root / "metadata/TAG").read_text().strip(), expected)
                self.assertIn(message.split(": ")[-1], (root / "metadata/RELEASE_NOTES.md").read_text())
                self.assertEqual(git("tag", "--list").decode().strip(), "v1.2.3")

    def test_first_release_is_initial_version(self):
        root, _, result = self.prepare("feat: first release", tagged=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((root / "metadata/TAG").read_text().strip(), "v0.1.0")

    def test_existing_release_is_rejected_without_creating_a_tag(self):
        _, git, result = self.prepare(None)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(git("tag", "--list").decode().strip(), "v1.2.3")

    def test_docs_only_manual_release_is_rejected_before_versioning(self):
        for bump in ("auto", "patch", "minor", "major"):
            with self.subTest(bump=bump):
                root, git, result = self.prepare("docs: explain installation", bump=bump)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("No product changes", result.stderr)
                self.assertFalse((root / "metadata").exists())
                self.assertEqual(git("tag", "--list").decode().strip(), "v1.2.3")
