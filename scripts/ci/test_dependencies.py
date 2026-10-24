"""Cover dependency drift that previously broke grouped update PRs."""

import importlib.util
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("dependencies", Path(__file__).with_name("check-dependencies.py"))
dependencies = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dependencies)


class DependencyTests(unittest.TestCase):
    def fixture(self, root):
        (root / "Directory.Packages.props").write_text(
            '<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>'
            '</PropertyGroup><ItemGroup>'
            '<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.13" />'
            '<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.13" />'
            '</ItemGroup></Project>')
        (root / "tests").mkdir()
        (root / "tests/Test.csproj").write_text(
            '<Project><ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />'
            '</ItemGroup></Project>')
        (root / ".devcontainer").mkdir()
        (root / ".devcontainer/Dockerfile").write_text(
            'FROM golang:1.27.3-bookworm AS go\nARG DOTNET_EF_VERSION=10.0.13\n')
        (root / "services/cli").mkdir(parents=True)
        (root / "services/cli/go.mod").write_text('module example.test/cli\n\ngo 1.26.0\ntoolchain go1.27.3\n')
        for path in ("frontend/Dockerfile", "services/core/Dockerfile", "services/ingest/Dockerfile"):
            file = root / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text('FROM golang:1.27.3-bookworm AS builder\n')

    def test_shared_versions_apply_to_test_projects(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root)
            dependencies.check(root)

    def test_project_override_or_missing_central_version_fails(self):
        for reference in (
            '<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.2" />',
            '<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" VersionOverride="10.0.2" />',
            '<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite"><Version>10.0.2</Version></PackageReference>',
            '<PackageReference Include="Missing.Package" />',
        ):
            with self.subTest(reference=reference), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                self.fixture(root)
                (root / "tests/Test.csproj").write_text('<Project><ItemGroup>' + reference + '</ItemGroup></Project>')
                with self.assertRaises(ValueError):
                    dependencies.check(root)

    def test_ef_design_package_and_tool_cannot_drift(self):
        for path in ("Directory.Packages.props", ".devcontainer/Dockerfile"):
            with self.subTest(path=path), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                self.fixture(root)
                file = root / path
                file.write_text(file.read_text().replace('10.0.13', '10.0.2', 1))
                with self.assertRaises(ValueError):
                    dependencies.check(root)

    def test_images_wait_for_all_source_checks(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/checks.yml').read_text()
        image_job = workflow.split('\n  images:\n', 1)[1].split('\n  required:\n', 1)[0]
        needs = image_job.split('needs: [', 1)[1].split(']', 1)[0]
        self.assertEqual({value.strip() for value in needs.split(',')},
                         {'select', 'workflow', 'gitleaks', 'semgrep', 'dependencies', 'backend', 'frontend', 'cli', 'docs'})
        self.assertIn("!contains(needs.*.result, 'failure')", image_job)
        self.assertIn("!contains(needs.*.result, 'cancelled')", image_job)
        self.assertIn("needs.select.outputs.images == 'true'", image_job)

    def test_a_go_builder_cannot_keep_an_older_toolchain(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root)
            file = root / 'frontend/Dockerfile'
            file.write_text(file.read_text().replace('1.27.3', '1.27.2'))
            with self.assertRaisesRegex(ValueError, 'Go builders must match'):
                dependencies.check(root)
