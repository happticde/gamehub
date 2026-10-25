"""Exercise installed hooks with real Git indexes and synthetic credentials only."""

import importlib.util
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("scan_staged", ROOT / "scripts/scan-staged-secrets.py")
SCAN = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SCAN)


class EnvironmentBoundaryTests(unittest.TestCase):
    def test_environment_names_are_rejected_at_any_depth(self):
        for name in [".env", ".env.local", "frontend/.env.example", "nested/space dir/.env.production"]:
            self.assertTrue(SCAN.is_environment_file(name), name)
        for name in ["environment.cs", "docs/env.md", "env.json", "frontend/.envrc"]:
            self.assertFalse(SCAN.is_environment_file(name), name)

    def test_environment_paths_block_before_blob_reads_or_scanner_execution(self):
        def git(*arguments):
            if arguments[0] == "rev-parse":
                return os.fsencode(ROOT) + b"\n"
            if arguments[0] == "ls-files":
                return b""
            if arguments[0] == "diff":
                return b"nested/.env.local\0"
            self.fail("Environment file content must never be read")
        with patch.object(SCAN, "git", side_effect=git), patch.object(SCAN, "scanner") as scanner, \
                patch.object(SCAN.sys, "argv", ["scan-staged-secrets.py"]):
            self.assertEqual(SCAN.main(), 1)
            scanner.assert_not_called()


@unittest.skipUnless(shutil.which("gitleaks"), "Install pinned Gitleaks for hook integration tests")
class GitHookTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        for name in [".githooks/pre-commit", "scripts/install-git-hooks.sh", "scripts/scan-staged-secrets.py",
                     ".gitleaks.toml", ".ci/tools.json"]:
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, target)
        self.git("init", "-q")
        self.git("config", "user.name", "Hook test")
        self.git("config", "user.email", "hook-test@example.invalid")
        self.git("add", ".")
        self.git("-c", "core.hooksPath=/dev/null", "commit", "-qm", "Initial fixture")
        setup = self.run_command("sh", "scripts/install-git-hooks.sh")
        self.assertEqual(setup.returncode, 0, setup.stderr)
        # Fresh generated values are not usable credentials and never enter this repository.
        self.token = "ghp_" + secrets.token_hex(18)

    def run_command(self, *arguments, env=None):
        return subprocess.run(arguments, cwd=self.root, capture_output=True, text=True, env=env)

    def git(self, *arguments):
        result = self.run_command("git", *arguments)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout

    def stage(self, text, name="README.md"):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        self.git("add", "--", name)

    def commit(self):
        index = self.git("ls-files", "--stage", "-z")
        result = self.run_command("git", "commit", "-qm", "Exercise installed hook")
        self.assertEqual(self.git("ls-files", "--stage", "-z"), index)
        self.assertNotIn(self.token, result.stdout + result.stderr)
        return result

    def test_clean_commit_and_repeated_setup_succeed(self):
        self.stage("Ordinary documentation\n", "space dir/new\nfile.md")
        self.assertEqual(self.commit().returncode, 0)
        self.assertEqual(self.run_command("sh", "scripts/install-git-hooks.sh").returncode, 0)

    def test_first_commit_is_scanned(self):
        self.git("checkout", "--orphan", "new-history")
        self.stage(f'github_api_key = "{self.token}"\n')
        self.assertNotEqual(self.commit().returncode, 0)

    def test_staged_secret_blocks_even_after_working_copy_is_cleaned(self):
        self.stage(f'github_api_key = "{self.token}"\n')
        (self.root / "README.md").write_text("Clean working copy\n")
        self.assertNotEqual(self.commit().returncode, 0)
        self.assertEqual((self.root / "README.md").read_text(), "Clean working copy\n")

    def test_unstaged_secret_does_not_block_clean_index(self):
        self.stage("Clean staged content\n")
        (self.root / "README.md").write_text(f'github_api_key = "{self.token}"\n')
        self.assertEqual(self.commit().returncode, 0)

    def test_unchanged_lines_in_changed_file_are_scanned(self):
        self.stage(f'github_api_key = "{self.token}"\n')
        self.git("-c", "core.hooksPath=/dev/null", "commit", "-qm", "Synthetic historical fixture")
        self.stage(f'github_api_key = "{self.token}"\nAdditional documentation\n')
        self.assertNotEqual(self.commit().returncode, 0)

    def test_pure_rename_of_secret_is_blocked(self):
        self.stage(f'github_api_key = "{self.token}"\n')
        self.git("-c", "core.hooksPath=/dev/null", "commit", "-qm", "Synthetic historical fixture")
        self.git("mv", "README.md", "renamed.md")
        self.assertNotEqual(self.commit().returncode, 0)

    def test_secret_deletion_is_allowed(self):
        self.stage(f'github_api_key = "{self.token}"\n')
        self.git("-c", "core.hooksPath=/dev/null", "commit", "-qm", "Synthetic historical fixture")
        self.git("rm", "README.md")
        self.assertEqual(self.commit().returncode, 0)

    def test_commit_all_checks_the_temporary_index(self):
        self.stage("Original clean content\n")
        self.assertEqual(self.commit().returncode, 0)
        (self.root / "README.md").write_text(f'github_api_key = "{self.token}"\n')
        result = self.run_command("git", "commit", "-am", "Must fail")
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn(self.token, result.stdout + result.stderr)

    def test_existing_default_hook_is_preserved(self):
        self.git("config", "--unset", "core.hooksPath")
        existing = self.root / ".git/hooks/pre-commit"
        existing.write_text("#!/bin/sh\nexit 0\n")
        existing.chmod(0o755)
        self.assertNotEqual(self.run_command("sh", "scripts/install-git-hooks.sh").returncode, 0)
        self.assertEqual(existing.read_text(), "#!/bin/sh\nexit 0\n")

    def test_symlink_target_is_not_read(self):
        (self.root / "untracked-credential.txt").write_text(f'github_api_key = "{self.token}"\n')
        (self.root / "link.txt").symlink_to("untracked-credential.txt")
        self.git("add", "link.txt")
        self.assertEqual(self.commit().returncode, 0)

    def test_inline_allow_comment_does_not_bypass_detection(self):
        self.stage(f'github_api_key = "{self.token}" # gitleaks:allow\n')
        self.assertNotEqual(self.commit().returncode, 0)

    def test_existing_hooks_path_is_preserved(self):
        self.git("config", "core.hooksPath", "custom-hooks")
        result = self.run_command("sh", "scripts/install-git-hooks.sh")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git("config", "--get", "core.hooksPath").strip(), "custom-hooks")

    def test_scanner_errors_and_version_mismatch_block_commits(self):
        self.stage("Clean staged content\n")
        version = SCAN.json.loads((ROOT / ".ci/tools.json").read_text())["gitleaks"]["version"]
        tool_directory = self.root / "fake-tools"
        tool_directory.mkdir()
        fake = tool_directory / "gitleaks"
        env = {**os.environ, "PATH": str(tool_directory) + os.pathsep + os.environ["PATH"]}
        for body in ["exit 2", "echo wrong-version", f'if [ "$1" = version ]; then echo {version}; '
                     'else echo "scanner error" >&2; fi']:
            fake.write_text("#!/bin/sh\n" + body + "\n")
            fake.chmod(0o755)
            result = self.run_command("git", "commit", "-qm", "Must fail", env=env)
            self.assertNotEqual(result.returncode, 0)

    def test_missing_scanner_blocks_commit(self):
        self.stage("Clean staged content\n")
        tool_directory = self.root / "limited-tools"
        tool_directory.mkdir()
        for name in ["git", "python3"]:
            (tool_directory / name).symlink_to(shutil.which(name))
        env = {**os.environ, "PATH": str(tool_directory)}
        result = self.run_command("git", "commit", "-qm", "Must fail", env=env)
        self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
