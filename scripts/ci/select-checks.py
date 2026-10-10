"""Select CI work from changed paths and reject unexpected skipped or failed jobs."""

import json
import os
from pathlib import PurePosixPath
import re
import subprocess
import sys


JOBS = ("workflow", "semgrep", "dependencies", "backend", "frontend", "cli")
COMPONENTS = ("core", "ingest", "frontend")


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


def select_checks(paths, full=False):
    if full:
        return make_plan(JOBS, COMPONENTS)
    jobs, components = set(), set()
    for path in paths:
        name = PurePosixPath(path).name
        # Selection, workflow, and security policy changes require full verification.
        if path.startswith((".github/", ".ci/", "scripts/ci/")) or path in {
            ".gitleaks.toml", ".gitleaksignore", ".semgrep.yml", "cliff.toml"
        }:
            return make_plan(JOBS, COMPONENTS)
        if path.startswith(("docs/", ".agents/")) or name.endswith(".md") or path == "LICENSE":
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


def changed_paths(event_name, event):
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
    # Disabling rename detection includes both deleted and added paths, across boundaries.
    output = subprocess.check_output([
        "git", "diff", "--name-only", "--no-renames", "-z", f"{base}{comparison}{head}", "--"
    ], stderr=subprocess.PIPE)
    return [path for path in output.decode().split("\0") if path]


def image_matrix(plan):
    return {"include": [
        {"component": component, "arch": arch, "runner": runner}
        for component in plan["components"]
        for arch, runner in (("amd64", "ubuntu-26.04"), ("arm64", "ubuntu-26.04-arm"))
    ]}


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
    if sys.argv[1] == "gate":
        failures = required_failures(json.loads(os.environ["CHECK_PLAN"]), json.loads(os.environ["CHECK_RESULTS"]))
        if failures:
            print("Required CI outcomes were not met: " + ", ".join(failures), file=sys.stderr)
            return 1
        print("All selected checks succeeded; only explicitly unselected checks were skipped.")
        return 0

    full = os.environ.get("SELECT_CHANGES") != "true" or os.environ.get("RELEASE_VERSION", "dev") != "dev"
    paths = []
    reason = "Full verification requested (release, schedule, manual run, or reusable default)."
    if not full:
        try:
            with open(os.environ["GITHUB_EVENT_PATH"], encoding="utf-8") as stream:
                paths = changed_paths(os.environ["GITHUB_EVENT_NAME"], json.load(stream))
            reason = f"Selected checks from {len(paths)} changed paths. Unknown paths select all checks."
        except (KeyError, ValueError, OSError, subprocess.CalledProcessError):
            full = True
            reason = "Comparison unavailable; falling back to full verification."
    plan = select_checks(paths, full=full)
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
