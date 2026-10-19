"""Verify immutable release source and exclusion of non-source or private paths."""
import importlib.util
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest


spec = importlib.util.spec_from_file_location('source_package', Path(__file__).with_name('package-source.py'))
source_package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(source_package)


class SourcePublicationTests(unittest.TestCase):
    def test_archive_uses_committed_source_and_excludes_untracked_and_environment_entries(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / 'checkout'
            root.mkdir()
            def git(*args, **kwargs):
                return subprocess.check_output(['git', *args], cwd=root, stderr=subprocess.DEVNULL, **kwargs)
            git('init')
            git('config', 'user.name', 'Source test')
            git('config', 'user.email', 'source-test@example.invalid')
            (root / 'application.txt').write_text('committed source')
            git('add', 'application.txt')
            # Add an index entry only: no environment file is created, read or inspected.
            oid = git('hash-object', '-w', '--stdin', input=b'excluded fixture').decode().strip()
            git('update-index', '--add', '--cacheinfo', f'100644,{oid},.env.fixture')
            git('commit', '-qm', 'test: source fixture')
            (root / 'application.txt').write_text('uncommitted change')
            (root / 'untracked.txt').write_text('not for publication')
            archive = Path(temporary) / 'source.tar.gz'
            revision = source_package.package_source(root, archive)
            with tarfile.open(archive) as bundle:
                self.assertEqual(set(bundle.getnames()), {
                    f'gamehub-{revision}/application.txt', f'gamehub-{revision}/SOURCE-README.txt'})
                self.assertEqual(bundle.extractfile(f'gamehub-{revision}/application.txt').read(), b'committed source')
            second = Path(temporary) / 'second.tar.gz'
            source_package.package_source(root, second)
            self.assertEqual(archive.read_bytes(), second.read_bytes())

    def test_source_boundaries_and_output_location(self):
        for path in ['.env', '.env.example', 'frontend/.env.local', 'frontend/node_modules/a.js', 'services/core/obj/a.cs']:
            self.assertFalse(source_package.include_source(path), path)
        self.assertTrue(source_package.include_source('.github/workflows/release.yml'))
        self.assertTrue(source_package.include_source('services/core/Dockerfile'))
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(ValueError):
                source_package.package_source(temporary, Path(temporary) / 'source.tar.gz')
