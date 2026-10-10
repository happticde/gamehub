"""Gate a complete Trivy report while retaining unfixed and lower-severity findings."""

import json
from pathlib import Path
import sys


def blocking_findings(report):
    if report.get("SchemaVersion") != 2 or not isinstance(report.get("Results"), list) or not report["Results"]:
        raise ValueError("Expected a nonempty Trivy schema-v2 report")
    findings = []
    for result in report["Results"]:
        for vulnerability in result.get("Vulnerabilities") or []:
            if vulnerability["Severity"] in {"HIGH", "CRITICAL"} and vulnerability.get("FixedVersion"):
                findings.append((result["Target"], vulnerability["VulnerabilityID"]))
        for configuration in result.get("Misconfigurations") or []:
            if configuration["Severity"] in {"HIGH", "CRITICAL"} and configuration["Status"] == "FAIL":
                findings.append((result["Target"], configuration["ID"]))
    return findings


if __name__ == "__main__":
    findings = blocking_findings(json.loads(Path(sys.argv[1]).read_text()))
    for target, finding in findings:
        print(f"Blocking security finding: {finding} in {target}")
    sys.exit(1 if findings else 0)
