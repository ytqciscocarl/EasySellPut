import hashlib
import importlib.util
import pathlib
import tempfile
import unittest

ROOT=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('update_release',ROOT/'build/prepare-update-release.py')
release=importlib.util.module_from_spec(spec);spec.loader.exec_module(release)

class UpdateReleaseTests(unittest.TestCase):
    def test_all_native_packages_are_required_and_checksums_are_verified(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);packages=root/'packages';packages.mkdir()
            for rid in release.RIDS:
                name=f'EasySellPut-Desktop-0.7.0-{rid}.zip';package=packages/name;package.write_bytes(b'test-only archive')
                package.with_suffix('.zip.sha256').write_text(hashlib.sha256(package.read_bytes()).hexdigest()+'  '+name+'\n')
            manifest=release.prepare(packages,root/'valid','0.7.0','owner/repo')
            self.assertEqual({a['rid'] for a in manifest['assets']},set(release.RIDS))
            self.assertTrue((root/'valid/EasySellPut-update.json').exists())
            victim=next(packages.glob('*.zip'));victim.write_bytes(b'tampered')
            with self.assertRaises(ValueError):release.prepare(packages,root/'tampered','0.7.0','owner/repo')
            victim.unlink()
            with self.assertRaises(ValueError):release.prepare(packages,root/'missing','0.7.0','owner/repo')
    def test_only_stable_versions_are_allowed(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):release.prepare(pathlib.Path(directory),pathlib.Path(directory)/'out','0.7.0-preview','owner/repo')

if __name__=='__main__':unittest.main()
