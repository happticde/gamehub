"""Reject NuGet version drift and keep the EF tool tied to central versions."""

from pathlib import Path
import re
import xml.etree.ElementTree as ET


def package_versions(path):
    central = ET.parse(path).getroot()
    if central.findtext(".//ManagePackageVersionsCentrally") != "true":
        raise ValueError("Central NuGet package management must be enabled")
    versions = {}
    for package in central.findall(".//PackageVersion"):
        name, version = package.get("Include"), package.get("Version")
        if not name or not version or name in versions:
            raise ValueError("Central package versions must have unique names and explicit versions")
        versions[name] = version
    ef_version = versions.get("Microsoft.EntityFrameworkCore.Sqlite")
    for name, version in versions.items():
        if ef_version and (name.startswith("Microsoft.EntityFrameworkCore.")
                or name == "Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore"):
            if version != ef_version:
                raise ValueError(f"{name} must match EF Core {ef_version}")
    return versions


def check(root):
    versions = package_versions(root / "Directory.Packages.props")
    for area in ("services", "tests", "tools"):
        for project in (root / area).rglob("*.csproj"):
            if any(part in {"bin", "obj"} for part in project.relative_to(root).parts):
                continue
            # NuGet uses the closest manifest. Isolated proofs must not alter production versions.
            manifest = next(parent / "Directory.Packages.props" for parent in project.parents
                            if (parent / "Directory.Packages.props").is_file())
            project_versions = package_versions(manifest)
            for reference in ET.parse(project).findall(".//PackageReference"):
                name = reference.get("Include")
                if (reference.get("Version") or reference.get("VersionOverride")
                        or reference.find("Version") is not None
                        or reference.find("VersionOverride") is not None):
                    raise ValueError(f"{project.relative_to(root)}: keep {name} in Directory.Packages.props")
                if name not in project_versions:
                    raise ValueError(f"{project.relative_to(root)}: missing central version for {name}")
    dockerfile = (root / ".devcontainer/Dockerfile").read_text()
    if ("COPY Directory.Packages.props /tmp/Directory.Packages.props" not in dockerfile
            or "dotnet msbuild /tmp/Directory.Packages.props -getItem:PackageVersion" not in dockerfile
            or 'p.Identity === "Microsoft.EntityFrameworkCore.Design"' not in dockerfile
            or 'dotnet-ef --version "${DOTNET_EF_VERSION}"' not in dockerfile
            or re.search(r"^ARG DOTNET_EF_VERSION=", dockerfile, re.MULTILINE)):
        raise ValueError("The devcontainer dotnet-ef version must come from Directory.Packages.props")
    check_go_toolchain(root)


def check_go_toolchain(root):
    module = (root / "services/cli/go.mod").read_text()
    toolchain = re.search(r"^toolchain go(\S+)$", module, re.MULTILINE)
    if not toolchain:
        raise ValueError("The CLI must declare its verified Go toolchain")
    for path in ("frontend/Dockerfile", "services/core/Dockerfile",
                 "services/ingest/Dockerfile", "services/mcp/Dockerfile", ".devcontainer/Dockerfile"):
        versions = re.findall(r"^FROM golang:([^\s-]+)-", (root / path).read_text(), re.MULTILINE)
        if not versions or any(version != toolchain.group(1) for version in versions):
            raise ValueError(f"{path}: Go builders must match toolchain go{toolchain.group(1)}")


if __name__ == "__main__":
    check(Path(__file__).resolve().parents[2])
    print("Central NuGet versions, the EF migration tool, and Go builders are consistent.")
