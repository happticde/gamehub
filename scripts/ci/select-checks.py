"""Select CI work from changed paths and reject unexpected skipped or failed jobs."""

import json
import os
from pathlib import PurePosixPath
import re
import subprocess
import sys


JOBS = ("workflow", "semgrep", "dependencies", "backend", "frontend", "cli", "docs")
COMPONENTS = ("core", "ingest", "frontend")
CHECKS_WORKFLOW = ".github/workflows/checks.yml"


def is_dependency_file(name):
    return name.endswith((".csproj", ".props", ".targets")) or name.lower() in {
        "package.json", "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml",
        ".npmrc", "go.mod", "go.sum", "packages.lock.json", "nuget.config", "global.json",
    }


def make_plan(jobs=(), components=()):
    return {
        "jobs": {job: job in jobs for job in JOBS},
        "components": [component for component in COMPONENTS if component in components],
    }


def action_pin_plan(before, after):
    """Narrow only SHA/comment replacements in existing steps of the shared workflow."""
    old_lines, new_lines = before.splitlines(), after.splitlines()
    if len(old_lines) != len(new_lines):
        return None
    pin = re.compile(r"(      - uses: |        uses: )([\w.-]+/[\w./-]+)@([0-9a-f]{40})(?:[ \t]+#.*)?")
    jobs, components = {"workflow"}, set()
    in_jobs, job, changed = False, None, False
    for old, new in zip(old_lines, new_lines):
        if old == "jobs:":
            in_jobs = True
        elif old and not old.startswith((" ", "#")):
            in_jobs, job = False, None
        if in_jobs and (match := re.fullmatch(r"  ([\w-]+):", old)):
            job = match[1]
        if old == new:
            continue
        old_pin, new_pin = pin.fullmatch(old), pin.fullmatch(new)
        if (not in_jobs or not old_pin or not new_pin
                or old_pin.group(1, 2) != new_pin.group(1, 2)):
            return None
        changed = True
        if job in JOBS:
            jobs.add(job)
        elif job == "images":
            components.update(COMPONENTS)
        elif job != "gitleaks":
            # Selector, aggregate, and unknown jobs affect the entire check boundary.
            return None
    return make_plan(jobs, components) if changed else None


def select_checks(paths, full=False, action_plan=None):
    if full:
        return make_plan(JOBS, COMPONENTS)
    jobs, components = set(), set()
    for path in paths:
        name = PurePosixPath(path).name
        # Legal files ship with every image and helper, even when no code changes.
        if path in {'LICENSE', 'COPYRIGHT', 'CONTRIBUTING.md', 'REUSE.toml'} or path.startswith(('LICENSES/', 'licensing/')):
            return make_plan(JOBS, COMPONENTS)
        if path == ".github/workflows/docs.yml":
            jobs.update(("workflow", "docs"))
            continue
        if path in {".github/dependabot.yml", ".github/actionlint.yaml"}:
            jobs.add("workflow")
            continue
        if path == CHECKS_WORKFLOW and action_plan is not None:
            jobs.update(job for job, selected in action_plan["jobs"].items() if selected)
            components.update(action_plan["components"])
            continue
        # Selection, workflow, and security policy changes require full verification.
        if path.startswith((".github/", ".ci/", "scripts/ci/")) or path in {
            ".gitleaks.toml", ".gitleaksignore", ".semgrep.yml", "cliff.toml"
        }:
            return make_plan(JOBS, COMPONENTS)
        if path.startswith("docs/agents/") or path.startswith(".agents/"):
            continue
        # The root Makefile contains only documentation shortcuts, never product build inputs.
        if path.startswith("docs/") or path == "Makefile":
            jobs.add("docs")
            continue
        if name.endswith(".md") or path == "LICENSE":
            continue
        if path.startswith("frontend/"):
            if name in {"Dockerfile", ".dockerignore"}:
                jobs.add("dependencies")
                components.add("frontend")
            else:
                jobs.add("frontend")
                if not path.startswith("frontend/tests/"):
                    jobs.add("semgrep")
                    components.add("frontend")
                if is_dependency_file(name):
                    jobs.add("dependencies")
        elif path.startswith("services/cli/"):
            jobs.add("cli")
            if not name.endswith("_test.go") and not path.endswith("/test-macos-installer.mjs"):
                jobs.add("semgrep")
                # The frontend image embeds the CLI and its macOS installer inputs.
                components.add("frontend")
            if is_dependency_file(name) or name == ".dockerignore":
                jobs.add("dependencies")
            if path.startswith("services/cli/scripts/") or path == "services/cli/.dockerignore":
                jobs.add("workflow")
        elif path.startswith(("services/core/", "services/ingest/", "services/shared/")):
            service = path.split("/")[1]
            components.update(("core", "ingest") if service == "shared" else (service,))
            if name == "Dockerfile":
                jobs.add("dependencies")
            else:
                jobs.update(("backend", "semgrep"))
                if is_dependency_file(name):
                    jobs.add("dependencies")
        elif path.startswith("tests/"):
            jobs.add("backend")
            if is_dependency_file(name):
                jobs.add("dependencies")
        elif path.startswith(("tools/", "services/mcp/")):
            jobs.update(("backend", "semgrep"))
            if is_dependency_file(name):
                jobs.add("dependencies")
        elif path == ".dockerignore":
            jobs.add("dependencies")
            components.update(("core", "ingest"))
        elif (name in {"global.json", "NuGet.Config", "nuget.config"}
              or name.startswith("Directory.") or name.endswith((".sln", ".slnx"))):
            jobs.update(("backend", "semgrep", "dependencies"))
            components.update(("core", "ingest"))
        elif name.startswith("docker-compose") and name.endswith((".yml", ".yaml")):
            jobs.add("dependencies")
        else:
            # New directories and unrecognized build inputs must not silently lose coverage.
            return make_plan(JOBS, COMPONENTS)
    return make_plan(jobs, components)


def comparison_revisions(event_name, event):
    if event_name == "pull_request":
        base = event["pull_request"]["base"]["sha"]
        head = event["pull_request"]["head"]["sha"]
        comparison = "..."
    elif event_name == "push":
        base, head = event["before"], event["after"]
        comparison = ".."
    else:
        raise ValueError("This event requires full verification")
    for revision in (base, head):
        if (not isinstance(revision, str) or not re.fullmatch(r"[0-9a-f]{40}", revision)
                or set(revision) == {"0"}):
            raise ValueError("A usable comparison revision is missing")
    return base, head, comparison


def changed_paths(event_name, event):
    base, head, comparison = comparison_revisions(event_name, event)
    # Disabling rename detection includes both deleted and added paths, across boundaries.
    output = subprocess.check_output([
        "git", "diff", "--name-only", "--no-renames", "-z", f"{base}{comparison}{head}", "--"
    ], stderr=subprocess.PIPE)
    return [path for path in output.decode().split("\0") if path]


def changed_action_plan(event_name, event):
    base, head, comparison = comparison_revisions(event_name, event)
    if comparison == "...":
        base = subprocess.check_output(["git", "merge-base", base, head], text=True).strip()
    before, after = [subprocess.check_output([
        "git", "show", f"{revision}:{CHECKS_WORKFLOW}"
    ], stderr=subprocess.PIPE).decode() for revision in (base, head)]
    return action_pin_plan(before, after)


def image_matrix(plan):
    return {"include": [
        {"component": component, "arch": arch, "runner": runner}
        for component in plan["components"]
        for arch, runner in (("amd64", "ubuntu-26.04"), ("arm64", "ubuntu-26.04-arm"))
    ]}


def require_product_changes():
    """Reject docs-only manual releases before restoration, builds, or publication."""
    tags = subprocess.check_output([
        "git", "tag", "--merged", "HEAD", "--sort=-version:refname"
    ], text=True).splitlines()
    latest = next((tag for tag in tags if re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", tag)), None)
    command = (["git", "diff", "--name-only", "--no-renames", "-z", f"{latest}..HEAD", "--"]
               if latest else ["git", "ls-files", "-z"])
    paths = subprocess.check_output(command).decode().split("\0")
    plan = select_checks([path for path in paths if path])
    product_jobs = (selected for job, selected in plan["jobs"].items() if job not in {"docs", "workflow"})
    if not plan["components"] and not any(product_jobs):
        raise ValueError("No product changes since the last release; documentation must be published separately.")


def required_failures(plan, results):
    if set(plan["jobs"]) != set(JOBS) or any(type(value) is not bool for value in plan["jobs"].values()):
        raise ValueError("Invalid check selection")
    if not isinstance(plan["components"], list) or any(component not in COMPONENTS for component in plan["components"]):
        raise ValueError("Invalid image selection")
    expected = {**plan["jobs"], "select": True, "gitleaks": True, "images": bool(plan["components"])}
    if set(results) != set(expected):
        raise ValueError("Missing or unexpected job results")
    return [job for job, selected in expected.items()
            if results[job].get("result") != ("success" if selected else "skipped")]


def main():
    if sys.argv[1] == "release":
        require_product_changes()
        return 0
    if sys.argv[1] == "gate":
        failures = required_failures(json.loads(os.environ["CHECK_PLAN"]), json.loads(os.environ["CHECK_RESULTS"]))
        if failures:
            print("Required CI outcomes were not met: " + ", ".join(failures), file=sys.stderr)
            return 1
        print("All selected checks succeeded; only explicitly unselected checks were skipped.")
        return 0

    full = os.environ.get("SELECT_CHANGES") != "true" or os.environ.get("RELEASE_VERSION", "dev") != "dev"
    paths = []
    action_plan = None
    reason = "Full verification requested (release, schedule, manual run, or reusable default)."
    if not full:
        try:
            with open(os.environ["GITHUB_EVENT_PATH"], encoding="utf-8") as stream:
                event = json.load(stream)
            event_name = os.environ["GITHUB_EVENT_NAME"]
            paths = changed_paths(event_name, event)
            if CHECKS_WORKFLOW in paths:
                action_plan = changed_action_plan(event_name, event)
            reason = f"Selected checks from {len(paths)} changed paths. Unknown paths select all checks."
        except (KeyError, ValueError, OSError, subprocess.CalledProcessError):
            full = True
            reason = "Comparison unavailable; falling back to full verification."
    plan = select_checks(paths, full=full, action_plan=action_plan)
    outputs = {
        **plan["jobs"], "images": bool(plan["components"]),
        "plan": plan, "image-matrix": image_matrix(plan),
    }
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        for name, value in outputs.items():
            stream.write(f"{name}={json.dumps(value, separators=(',', ':'))}\n")
    summary = reason + "\n\n```json\n" + json.dumps(plan, indent=2) + "\n```\n"
    print(summary)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
        stream.write("## CI check selection\n\n" + summary)
    return 0


if __name__ == "__main__":
    sys.exit(main())
