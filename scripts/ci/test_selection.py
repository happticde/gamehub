"""Exercise path coverage, real Git comparisons, and the required-check failure boundary."""

import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("select-checks.py")
spec = importlib.util.spec_from_file_location("selection", SCRIPT)
selection = importlib.util.module_from_spec(spec)
spec.loader.exec_module(selection)


class SelectionTests(unittest.TestCase):
    def test_documentation_skips_builds_and_images(self):
        plan = selection.select_checks(["README.md", "AGENTS.md", "docs/ci-and-releases.md", ".agents/skills/example/SKILL.md"])
        self.assertTrue(plan["jobs"]["docs"])
        self.assertFalse(any(value for job, value in plan["jobs"].items() if job != "docs"))
        self.assertEqual(plan["components"], [])
        self.assertEqual(selection.image_matrix(plan), {"include": []})

    def test_docs_configuration_dependencies_and_mixed_changes(self):
        for path in ("Makefile", "docs/site/package.json", "docs/site/package-lock.json", "docs/site/scripts/prepare.mjs",
                     "docs/site/src/pages/index.jsx", "docs/site/content.json"):
            with self.subTest(path=path):
                plan = selection.select_checks([path])
                self.assertEqual([job for job, selected in plan["jobs"].items() if selected], ["docs"])
                self.assertEqual(plan["components"], [])
        plan = selection.select_checks(["docs/index.md", "services/core/Program.cs"])
        self.assertTrue(plan["jobs"]["docs"])
        self.assertTrue(plan["jobs"]["backend"])
        self.assertEqual(plan["components"], ["core"])
        plan = selection.select_checks([".github/workflows/docs.yml"])
        self.assertEqual([job for job, selected in plan["jobs"].items() if selected], ["workflow", "docs"])
        self.assertFalse(any(selection.select_checks(["docs/agents/history.md"])["jobs"].values()))

    def test_frontend_code_and_dependencies(self):
        plan = selection.select_checks(["frontend/app/page.tsx"])
        self.assertTrue(plan["jobs"]["frontend"])
        self.assertTrue(plan["jobs"]["semgrep"])
        self.assertFalse(plan["jobs"]["dependencies"])
        self.assertFalse(plan["jobs"]["backend"])
        self.assertFalse(plan["jobs"]["cli"])
        self.assertEqual(plan["components"], ["frontend"])
        plan = selection.select_checks(["frontend/package-lock.json"])
        self.assertTrue(plan["jobs"]["dependencies"])
        self.assertTrue(plan["jobs"]["frontend"])

    def test_core_ingest_and_shared_dependencies(self):
        for service, images in (("core", ["core"]), ("ingest", ["ingest"]), ("shared", ["core", "ingest"])):
            with self.subTest(service=service):
                plan = selection.select_checks([f"services/{service}/Example.cs"])
                self.assertTrue(plan["jobs"]["backend"])
                self.assertEqual(plan["components"], images)
                self.assertFalse(plan["jobs"]["frontend"])
                self.assertFalse(plan["jobs"]["cli"])
        for path in ("services/shared/GameHub.Shared.csproj", "services/core/Directory.Packages.props", "tests/GameHub.Tests/packages.lock.json"):
            with self.subTest(path=path):
                self.assertTrue(selection.select_checks([path])["jobs"]["dependencies"])

    def test_cli_and_packaged_helper_inputs_rebuild_frontend_image(self):
        for path in ("services/cli/cmd/helper.go", "services/cli/helper-distribution/install.command", "services/cli/helper-distribution/README.txt"):
            with self.subTest(path=path):
                plan = selection.select_checks([path])
                self.assertTrue(plan["jobs"]["cli"])
                self.assertFalse(plan["jobs"]["frontend"])
                self.assertEqual(plan["components"], ["frontend"])
        plan = selection.select_checks(["services/cli/scripts/package-macos-helper.sh"])
        self.assertTrue(plan["jobs"]["workflow"])
        self.assertEqual(plan["components"], ["frontend"])
        self.assertTrue(selection.select_checks(["services/cli/go.sum"])["jobs"]["dependencies"])

    def test_test_only_changes_do_not_rebuild_images(self):
        for path, job in (("tests/GameHub.Tests/ExampleTests.cs", "backend"),
                          ("frontend/tests/platform-connect.test.mjs", "frontend"),
                          ("services/cli/cmd/helper_test.go", "cli"),
                          ("services/cli/scripts/test-macos-installer.mjs", "cli")):
            with self.subTest(path=path):
                plan = selection.select_checks([path])
                self.assertTrue(plan["jobs"][job])
                self.assertFalse(plan["jobs"]["semgrep"])
                self.assertEqual(plan["components"], [])

    def test_shared_notice_collector_rebuilds_every_image(self):
        plan = selection.select_checks(["services/cli/tools/license-notices/main.go"])
        self.assertTrue(plan["jobs"]["cli"])
        self.assertTrue(plan["jobs"]["semgrep"])
        self.assertEqual(plan["components"], list(selection.COMPONENTS))

    def test_release_publishes_every_verified_component(self):
        release = (SCRIPT.parents[2] / ".github/workflows/release.yml").read_text()
        components = re.search(r"component: \[([^\]]+)\]", release)[1]
        self.assertEqual([item.strip() for item in components.split(",")], list(selection.COMPONENTS))
        self.assertIn("arch: [amd64, arm64]", release)
        loops = re.findall(r"for component in ([\w ]+); do", release)
        self.assertEqual(len(loops), 2)
        for components in loops:
            self.assertEqual(components.split(), list(selection.COMPONENTS))

    def test_mcp_and_backend_tool_changes_do_not_rebuild_unrelated_images(self):
        for path, images in (("services/mcp/Program.cs", ["mcp"]),
                             ("tools/product-catalog-benchmark/Program.cs", [])):
            with self.subTest(path=path):
                plan = selection.select_checks([path])
                self.assertTrue(plan["jobs"]["backend"])
                self.assertTrue(plan["jobs"]["semgrep"])
                self.assertEqual(plan["components"], images)

    def test_docker_changes_select_only_affected_images_and_configuration_scan(self):
        for path, images in (("frontend/Dockerfile", ["frontend"]),
                             ("services/core/Dockerfile", ["core"]),
                             ("services/ingest/Dockerfile", ["ingest"]),
                             ("services/mcp/Dockerfile", ["mcp"]),
                             (".dockerignore", ["core", "ingest", "mcp"]),
                             ("docker-compose.yml", [])):
            with self.subTest(path=path):
                plan = selection.select_checks([path])
                self.assertTrue(plan["jobs"]["dependencies"])
                self.assertEqual(plan["components"], images)
                self.assertFalse(plan["jobs"]["backend"])
                self.assertFalse(plan["jobs"]["frontend"])

    def test_ci_security_unknown_and_full_mode_keep_all_checks(self):
        for path in (".github/workflows/checks.yml", ".ci/tools.json", "scripts/ci/select-checks.py",
                     ".semgrep.yml", ".gitleaksignore", "cliff.toml", "new-service/main.rs",
                     "LICENSE", "licensing/standard/MIT.txt", "licensing/generate.mjs"):
            with self.subTest(path=path):
                plan = selection.select_checks([path])
                self.assertTrue(all(plan["jobs"].values()))
                self.assertEqual(plan["components"], ["core", "ingest", "frontend", "mcp"])
        plan = selection.select_checks(["README.md"], full=True)
        self.assertTrue(all(plan["jobs"].values()))
        self.assertEqual(len(selection.image_matrix(plan)["include"]), 8)

    def test_shared_build_inputs_rebuild_mcp_on_both_architectures(self):
        for path in ("global.json", "Directory.Packages.props", "NuGet.Config", ".dockerignore",
                     "services/mcp/GameHub.Mcp.csproj"):
            with self.subTest(path=path):
                plan = selection.select_checks([path])
                self.assertTrue(plan["jobs"]["dependencies"])
                entries = selection.image_matrix(plan)["include"]
                self.assertEqual({entry["arch"] for entry in entries if entry["component"] == "mcp"},
                                 {"amd64", "arm64"})

    def test_maintenance_configuration_does_not_rebuild_products(self):
        for path in (".github/dependabot.yml", ".github/actionlint.yaml"):
            with self.subTest(path=path):
                self.assertEqual(selection.select_checks([path]), selection.make_plan(["workflow"]))
        plan = selection.select_checks([".github/dependabot.yml", "services/core/Program.cs"])
        self.assertTrue(plan["jobs"]["workflow"])
        self.assertTrue(plan["jobs"]["backend"])
        self.assertEqual(plan["components"], ["core"])

    def test_combined_changes_preserve_both_image_architectures(self):
        plan = selection.select_checks(["frontend/app/page.tsx", "services/core/Program.cs", "README.md"])
        self.assertEqual(plan["components"], ["core", "frontend"])
        matrix = selection.image_matrix(plan)["include"]
        self.assertEqual({(entry["component"], entry["arch"]) for entry in matrix},
                         {("core", "amd64"), ("core", "arm64"), ("frontend", "amd64"), ("frontend", "arm64")})
        self.assertEqual({entry["runner"] for entry in matrix}, {"ubuntu-26.04", "ubuntu-26.04-arm"})


class ActionPinTests(unittest.TestCase):
    workflow = (SCRIPT.parents[2] / selection.CHECKS_WORKFLOW).read_text()

    @classmethod
    def update_action(cls, action, source=None):
        return re.sub(re.escape(action) + r"@[0-9a-f]{40}(?: #.*)?",
                      action + "@" + "a" * 40 + " # updated version",
                      cls.workflow if source is None else source)

    def test_actual_workflow_pins_select_every_consumer(self):
        for action, jobs, components in (
            ("actions/setup-node", ["workflow", "frontend", "cli", "docs"], []),
            ("actions/setup-dotnet", ["workflow", "dependencies", "backend"], []),
            ("actions/setup-go", ["workflow", "backend", "frontend", "cli", "docs"], []),
            ("actions/cache", ["workflow", "dependencies", "backend", "frontend"], []),
            ("docker/setup-buildx-action", ["workflow"], selection.COMPONENTS),
            ("docker/build-push-action", ["workflow"], selection.COMPONENTS),
        ):
            with self.subTest(action=action):
                plan = selection.action_pin_plan(self.workflow, self.update_action(action))
                self.assertEqual(plan, selection.make_plan(jobs, components))
                self.assertEqual(selection.select_checks([selection.CHECKS_WORKFLOW], action_plan=plan), plan)

    def test_shared_checkout_and_upload_changes_keep_broad_coverage(self):
        self.assertIsNone(selection.action_pin_plan(self.workflow, self.update_action("actions/checkout")))
        plan = selection.action_pin_plan(self.workflow, self.update_action("actions/upload-artifact"))
        self.assertTrue(all(plan["jobs"].values()))
        self.assertEqual(len(selection.image_matrix(plan)["include"]), 8)

    def test_non_pin_edits_and_unknown_context_fall_back_to_full_checks(self):
        updated = self.update_action("actions/setup-node")
        for changed in (
            updated.replace("contents: read", "contents: write", 1),
            updated.replace("npm run build", "npm run different-build", 1),
            updated.replace("needs: select", "needs: workflow", 1),
            updated.replace("node-version: '24'", "node-version: '26'", 1),
            updated.replace("actions/setup-node@", "other/setup-node@", 1),
            updated.replace("@" + "a" * 40, "@v9", 1),
            updated.replace("  frontend:\n", "  renamed-frontend:\n", 1),
            updated + "\n# new line\n",
            "",
        ):
            with self.subTest(changed=changed[-100:]):
                self.assertNotEqual(changed, updated)
                plan = selection.action_pin_plan(self.workflow, changed)
                self.assertIsNone(plan)
                self.assertTrue(all(selection.select_checks([selection.CHECKS_WORKFLOW], action_plan=plan)["jobs"].values()))
        unknown = self.workflow.replace("  frontend:\n", "  unknown:\n")
        self.assertIsNone(selection.action_pin_plan(unknown, self.update_action("actions/setup-node", unknown)))
        self.assertIsNone(selection.action_pin_plan(self.workflow, self.workflow))

    def test_pin_plans_combine_with_code_changes_and_never_override_full_mode(self):
        action_plan = selection.action_pin_plan(self.workflow, self.update_action("actions/setup-node"))
        plan = selection.select_checks([selection.CHECKS_WORKFLOW, "services/core/Program.cs"], action_plan=action_plan)
        self.assertTrue(plan["jobs"]["backend"])
        self.assertTrue(plan["jobs"]["frontend"])
        self.assertTrue(plan["jobs"]["semgrep"])
        self.assertEqual(plan["components"], ["core"])
        for paths, full in (([selection.CHECKS_WORKFLOW], True),
                            ([selection.CHECKS_WORKFLOW, ".github/workflows/release.yml"], False)):
            plan = selection.select_checks(paths, full=full, action_plan=action_plan)
            self.assertTrue(all(plan["jobs"].values()))
            self.assertEqual(len(selection.image_matrix(plan)["include"]), 8)


class RequiredCheckTests(unittest.TestCase):
    def results(self, plan):
        return {job: {"result": "success" if selected else "skipped"}
                for job, selected in {**plan["jobs"], "select": True, "gitleaks": True, "images": bool(plan["components"])}.items()}

    def test_docs_allow_only_planned_skips_and_still_require_secret_scan(self):
        plan = selection.select_checks(["docs/index.md"])
        results = self.results(plan)
        self.assertEqual(selection.required_failures(plan, results), [])
        results["gitleaks"]["result"] = "skipped"
        self.assertEqual(selection.required_failures(plan, results), ["gitleaks"])
        results = self.results(plan)
        results["docs"]["result"] = "skipped"
        self.assertEqual(selection.required_failures(plan, results), ["docs"])

    def test_selected_skipped_failed_cancelled_and_missing_checks_block(self):
        plan = selection.select_checks(["services/core/Program.cs"])
        for job in ("select", "gitleaks", "backend", "semgrep", "images"):
            for result in ("skipped", "failure", "cancelled"):
                with self.subTest(job=job, result=result):
                    results = self.results(plan)
                    results[job]["result"] = result
                    self.assertIn(job, selection.required_failures(plan, results))
        results = self.results(plan)
        del results["backend"]
        with self.assertRaises(ValueError):
            selection.required_failures(plan, results)

    def test_action_updates_still_require_each_consumer_and_secret_scan(self):
        plan = selection.action_pin_plan(ActionPinTests.workflow, ActionPinTests.update_action("actions/setup-node"))
        self.assertEqual(selection.required_failures(plan, self.results(plan)), [])
        for job in ("select", "gitleaks", "workflow", "frontend", "cli", "docs"):
            results = self.results(plan)
            results[job]["result"] = "skipped"
            self.assertIn(job, selection.required_failures(plan, results))

    def test_full_run_rejects_every_skip_and_malformed_plan(self):
        plan = selection.select_checks([], full=True)
        for job in self.results(plan):
            results = self.results(plan)
            results[job]["result"] = "skipped"
            self.assertIn(job, selection.required_failures(plan, results))
        plan["jobs"]["backend"] = "false"
        with self.assertRaises(ValueError):
            selection.required_failures(plan, self.results(plan))


class GitComparisonTests(unittest.TestCase):
    def test_action_pin_entrypoint_uses_merge_base_and_rechecks_after_rebase(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)

            def git(*arguments):
                return subprocess.check_output(["git", *arguments], cwd=root, stderr=subprocess.DEVNULL).decode().strip()

            def commit_workflow(content):
                target.write_text(content)
                git("add", selection.CHECKS_WORKFLOW)
                git("commit", "-m", "fixture")
                return git("rev-parse", "HEAD")

            def run_plan(name, payload):
                (root / "event.json").write_text(json.dumps(payload))
                environment = {**os.environ, "SELECT_CHANGES": "true", "RELEASE_VERSION": "dev",
                               "GITHUB_EVENT_NAME": name, "GITHUB_EVENT_PATH": str(root / "event.json"),
                               "GITHUB_OUTPUT": str(root / "outputs"), "GITHUB_STEP_SUMMARY": str(root / "summary")}
                subprocess.check_call([sys.executable, str(SCRIPT), "select"], cwd=root, env=environment,
                                      stdout=subprocess.DEVNULL)
                outputs = dict(line.split("=", 1) for line in (root / "outputs").read_text().splitlines())
                return json.loads(outputs["plan"])

            git("init")
            git("config", "user.name", "CI selection test")
            git("config", "user.email", "ci-test@example.invalid")
            target = root / selection.CHECKS_WORKFLOW
            target.parent.mkdir(parents=True)
            base = commit_workflow(ActionPinTests.workflow)
            git("checkout", "-b", "dependency")
            head = commit_workflow(ActionPinTests.update_action("actions/setup-node"))
            git("checkout", "--detach", base)
            advanced_base = commit_workflow(ActionPinTests.update_action("actions/setup-dotnet"))
            expected = selection.make_plan(["workflow", "frontend", "cli", "docs"])
            self.assertEqual(run_plan("push", {"before": base, "after": head}), expected)
            self.assertEqual(run_plan("pull_request", {
                "pull_request": {"base": {"sha": advanced_base}, "head": {"sha": head}}
            }), expected)
            git("rebase", advanced_base, "dependency")
            rebased_head = git("rev-parse", "HEAD")
            self.assertNotEqual(head, rebased_head)
            self.assertEqual(run_plan("pull_request", {
                "pull_request": {"base": {"sha": advanced_base}, "head": {"sha": rebased_head}}
            }), expected)
            self.assertEqual(run_plan("push", {"before": advanced_base, "after": rebased_head}), expected)
            git("rm", selection.CHECKS_WORKFLOW)
            git("commit", "-m", "remove workflow")
            removed = git("rev-parse", "HEAD")
            full = selection.make_plan(selection.JOBS, selection.COMPONENTS)
            self.assertEqual(run_plan("push", {"before": rebased_head, "after": removed}), full)
            self.assertEqual(run_plan("push", {"before": removed, "after": rebased_head}), full)

    def test_docs_entrypoint_and_pr_merge_base_ignore_unrelated_base_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def git(*arguments):
                return subprocess.check_output(["git", *arguments], cwd=root, stderr=subprocess.DEVNULL).decode().strip()
            git("init")
            git("config", "user.name", "CI selection test")
            git("config", "user.email", "ci-test@example.invalid")
            (root / "README.md").write_text("base")
            git("add", "README.md")
            git("commit", "-m", "base")
            base = git("rev-parse", "HEAD")
            git("checkout", "-b", "docs")
            (root / "README.md").write_text("updated docs")
            git("commit", "-am", "docs: explain releases")
            head = git("rev-parse", "HEAD")
            git("checkout", "--detach", base)
            (root / "runtime.cs").write_text("unrelated base change")
            git("add", "runtime.cs")
            git("commit", "-m", "advance base")
            advanced_base = git("rev-parse", "HEAD")
            (root / "event.json").write_text(json.dumps({
                "pull_request": {"base": {"sha": advanced_base}, "head": {"sha": head}}
            }))
            environment = {**os.environ, "SELECT_CHANGES": "true", "RELEASE_VERSION": "dev",
                           "GITHUB_EVENT_NAME": "pull_request", "GITHUB_EVENT_PATH": str(root / "event.json"),
                           "GITHUB_OUTPUT": str(root / "outputs"), "GITHUB_STEP_SUMMARY": str(root / "summary")}
            subprocess.check_call([sys.executable, str(SCRIPT), "select"], cwd=root, env=environment,
                                  stdout=subprocess.DEVNULL)
            outputs = dict(line.split("=", 1) for line in (root / "outputs").read_text().splitlines())
            self.assertFalse(any(json.loads(outputs["plan"])["jobs"].values()))
            self.assertEqual(json.loads(outputs["image-matrix"]), {"include": []})
            self.assertIn("1 changed paths", (root / "summary").read_text())

    def test_pr_and_push_ranges_include_deletions_renames_and_all_push_commits(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def git(*arguments):
                return subprocess.check_output(["git", *arguments], cwd=root, stderr=subprocess.DEVNULL).decode().strip()
            def commit(name, text):
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(text)
                git("add", ".")
                git("commit", "-m", "fixture")
                return git("rev-parse", "HEAD")
            git("init")
            git("config", "user.name", "CI selection test")
            git("config", "user.email", "ci-test@example.invalid")
            base = commit("services/core/old.cs", "original")
            git("checkout", "-b", "feature")
            commit("README.md", "docs")
            git("mv", "services/core/old.cs", "README-renamed.md")
            git("commit", "-m", "rename across scopes")
            head = commit("frontend/app/page.tsx", "code")
            with patch.object(selection.subprocess, "check_output", wraps=lambda args, **kwargs: subprocess.run(args, cwd=root, check=True, stdout=subprocess.PIPE, **kwargs).stdout):
                for event, payload in (("push", {"before": base, "after": head}),
                                       ("pull_request", {"pull_request": {"base": {"sha": base}, "head": {"sha": head}}})):
                    paths = selection.changed_paths(event, payload)
                    self.assertIn("services/core/old.cs", paths)
                    self.assertIn("README-renamed.md", paths)
                    self.assertIn("frontend/app/page.tsx", paths)
                    plan = selection.select_checks(paths)
                    self.assertEqual(plan["components"], ["core", "frontend"])

    def test_unknown_event_and_invalid_comparison_cannot_produce_empty_selection(self):
        for name, event in (("schedule", {}), ("push", {"before": "0" * 40, "after": "a" * 40}),
                            ("push", {"before": "--output=invalid", "after": "a" * 40})):
            with self.subTest(name=name):
                with self.assertRaises(ValueError):
                    selection.changed_paths(name, event)

    def test_entrypoint_forces_full_release_manual_and_missing_comparison_runs(self):
        for selective, version, event, payload in (("true", "1.2.3", "pull_request", {}),
                                                    ("false", "dev", "workflow_dispatch", {}),
                                                    ("false", "dev", "schedule", {}),
                                                    ("true", "dev", "push", {}),
                                                    ("true", "dev", "push", {"before": "a" * 40, "after": "b" * 40})):
            with self.subTest(event=event), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                (root / "event.json").write_text(json.dumps(payload))
                environment = {**os.environ, "SELECT_CHANGES": selective, "RELEASE_VERSION": version,
                               "GITHUB_EVENT_NAME": event, "GITHUB_EVENT_PATH": str(root / "event.json"),
                               "GITHUB_OUTPUT": str(root / "outputs"), "GITHUB_STEP_SUMMARY": str(root / "summary")}
                subprocess.check_call([sys.executable, str(SCRIPT), "select"], env=environment, stdout=subprocess.DEVNULL)
                outputs = dict(line.split("=", 1) for line in (root / "outputs").read_text().splitlines())
                self.assertTrue(all(json.loads(outputs["plan"])["jobs"].values()))
                self.assertEqual(len(json.loads(outputs["image-matrix"])["include"]), 8)


if __name__ == "__main__":
    unittest.main()
