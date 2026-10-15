"""Block high/critical findings; unfixed highs require scoped, unexpired exceptions."""

import argparse
from datetime import date
import json
from pathlib import Path, PurePosixPath
import sys


def has_exception(target, vulnerability, exceptions, today):
    for exception in exceptions:
        if (exception["vulnerability"] == vulnerability["VulnerabilityID"]
                and exception["package"] == vulnerability.get("PkgName")
                and exception["version"] == vulnerability.get("InstalledVersion")
                and date.fromisoformat(exception["expires"]) >= today
                and exception.get("owner") and exception.get("reason")
                and any(target == path or target.endswith("/" + path) for path in exception["targets"])):
            return True
    return False


def repository_target(target, target_prefix):
    scanned_path = PurePosixPath(target)
    prefix = PurePosixPath(target_prefix)
    if (scanned_path.is_absolute() or ".." in scanned_path.parts
            or prefix.is_absolute() or ".." in prefix.parts):
        raise ValueError("Trivy targets and prefixes must be relative repository paths")
    return (prefix / scanned_path).as_posix()


def blocking_findings(report, exceptions=(), today=None, target_prefix=None):
    if report.get("SchemaVersion") != 2 or not isinstance(report.get("Results"), list) or not report["Results"]:
        raise ValueError("Expected a nonempty Trivy schema-v2 report")
    findings = []
    for result in report["Results"]:
        target = result["Target"]
        if target_prefix:
            target = repository_target(target, target_prefix)
        for vulnerability in result.get("Vulnerabilities") or []:
            if (vulnerability["Severity"] in {"HIGH", "CRITICAL"}
                    and (vulnerability.get("FixedVersion") or vulnerability["Severity"] == "CRITICAL"
                         or not has_exception(target, vulnerability, exceptions, today or date.today()))):
                findings.append((result["Target"], vulnerability["VulnerabilityID"]))
        for configuration in result.get("Misconfigurations") or []:
            if configuration["Severity"] in {"HIGH", "CRITICAL"} and configuration["Status"] == "FAIL":
                findings.append((result["Target"], configuration["ID"]))
    return findings


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    parser.add_argument("--target-prefix", help="Repository-relative prefix for scans rooted in a subdirectory")
    arguments = parser.parse_args()
    policy = json.loads((Path(__file__).resolve().parents[2] / ".ci/security-exceptions.json").read_text())
    if policy.get("schemaVersion") != 1 or not isinstance(policy.get("exceptions"), list):
        raise ValueError("Expected reviewed security exception policy")
    findings = blocking_findings(json.loads(arguments.report.read_text()), policy["exceptions"],
                                 target_prefix=arguments.target_prefix)
    for target, finding in findings:
        print(f"Blocking security finding: {finding} in {target}")
    sys.exit(1 if findings else 0)
