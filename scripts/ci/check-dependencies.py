"""Reject per-project NuGet version drift and a stale EF migration tool."""

from pathlib import Path
import re
import xml.etree.ElementTree as ET


def check(root):
    central = ET.parse(root / "Directory.Packages.props").getroot()
    if central.findtext(".//ManagePackageVersionsCentrally") != "true":
        raise ValueError("Central NuGet package management must be enabled")
    versions = {}
    for package in central.findall(".//PackageVersion"):
        name, version = package.get("Include"), package.get("Version")
        if not name or not version or name in versions:
            raise ValueError("Central package versions must have unique names and explicit versions")
        versions[name] = version
    for area in ("services", "tests", "tools"):
        for project in (root / area).rglob("*.csproj"):
            if any(part in {"bin", "obj"} for part in project.relative_to(root).parts):
                continue
            for reference in ET.parse(project).findall(".//PackageReference"):
                name = reference.get("Include")
                if (reference.get("Version") or reference.get("VersionOverride")
                        or reference.find("Version") is not None
                        or reference.find("VersionOverride") is not None):
                    raise ValueError(f"{project.relative_to(root)}: keep {name} in Directory.Packages.props")
                if name not in versions:
                    raise ValueError(f"{project.relative_to(root)}: missing central version for {name}")
    ef_version = versions["Microsoft.EntityFrameworkCore.Sqlite"]
    for name, version in versions.items():
        if (name.startswith("Microsoft.EntityFrameworkCore.")
                or name == "Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore"):
            if version != ef_version:
                raise ValueError(f"{name} must match EF Core {ef_version}")
    dockerfile = (root / ".devcontainer/Dockerfile").read_text()
    tool_version = re.search(r"^ARG DOTNET_EF_VERSION=([^\s]+)$", dockerfile, re.MULTILINE)
    if not tool_version or tool_version.group(1) != ef_version:
        raise ValueError(f"The devcontainer dotnet-ef version must match EF Core {ef_version}")


if __name__ == "__main__":
    check(Path(__file__).resolve().parents[2])
    print("Central NuGet versions and the EF migration tool are consistent.")
