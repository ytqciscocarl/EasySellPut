import copy
import hashlib
import importlib.util
import json
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/release'))
import policy
import assemble
import publish
spec = importlib.util.spec_from_file_location('release_signing', ROOT / 'build/release/sign-packages.py')
signing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(signing)


class ReleasePolicyTests(unittest.TestCase):
    def setUp(self):
        self.sha = 'a' * 40
        self.run = dict(status='completed', conclusion='success', event='push', head_branch='main', path='.github/workflows/ci.yml',
                        repository={'full_name': 'owner/repo'}, head_repository={'full_name': 'owner/repo'}, head_sha=self.sha)
        self.jobs = [dict(name=n, status='completed', conclusion='success') for n in policy.REQUIRED_JOBS]
        self.artifacts = [dict(id=i + 1, name=n, expired=False, size_in_bytes=100) for i, n in enumerate(policy.ARTIFACTS)]

    def test_only_successful_current_main_push_from_same_repository_is_accepted(self):
        policy.validate_run(self.run, 'owner/repo', self.sha)
        for field, bad in [('head_branch', 'work'), ('head_branch', 'feature'), ('event', 'pull_request'),
                           ('event', 'workflow_dispatch'), ('head_sha', 'b' * 40), ('conclusion', 'failure'),
                           ('status', 'in_progress'), ('path', '.github/workflows/other.yml'),
                           ('head_repository', {'full_name': 'fork/repo'}), ('repository', {'full_name': 'other/repo'})]:
            with self.subTest(field=field, value=bad):
                changed = dict(self.run, **{field: bad})
                with self.assertRaises(ValueError):
                    policy.validate_run(changed, 'owner/repo', self.sha)

    def test_aggregate_alone_cannot_replace_all_native_jobs(self):
        policy.validate_jobs(self.jobs)
        for jobs in [self.jobs[:-1], self.jobs + [self.jobs[0]], [dict(name=policy.GATE_NAME, status='completed', conclusion='success')],
                     [dict(j, conclusion='skipped') if i == 0 else j for i, j in enumerate(self.jobs)]]:
            with self.assertRaises(ValueError):
                policy.validate_jobs(jobs)

    def test_tag_resolution_uses_tag_namespace_and_handles_annotated_tags(self):
        annotated = 'b' * 40
        with mock.patch.object(policy, 'gh_json', side_effect=[{'object': {'type': 'tag', 'sha': annotated}}, {'object': {'type': 'commit', 'sha': self.sha}}]) as api:
            self.assertEqual(policy.resolve_tag('owner/repo', 'v0.7.0'), self.sha)
        self.assertEqual(api.call_args_list[0].args[0], 'repos/owner/repo/git/ref/tags/v0.7.0')
        self.assertNotIn('/commits/', api.call_args_list[0].args[0], 'A same-named branch must not substitute for a tag')
        with mock.patch.object(policy, 'gh_json', return_value={'object': {'type': 'tag', 'sha': annotated}}), self.assertRaisesRegex(ValueError, 'nested'):
            policy.resolve_tag('owner/repo', 'v0.7.0')

    def test_artifact_identity_expiry_duplicates_and_all_nine_targets(self):
        self.assertEqual(len(policy.validate_artifacts(self.artifacts)), 9)
        for artifacts in [self.artifacts[:-1], self.artifacts + [self.artifacts[0]],
                          [dict(a, expired=True) if i == 0 else a for i, a in enumerate(self.artifacts)],
                          [dict(a, size_in_bytes=0) if i == 0 else a for i, a in enumerate(self.artifacts)]]:
            with self.assertRaises(ValueError):
                policy.validate_artifacts(artifacts)

    def test_selection_uses_main_sha_and_rejects_truncated_responses(self):
        responses = [self.run, {'object': {'sha': self.sha}}, {'total_count': len(self.jobs), 'jobs': self.jobs},
                     {'total_count': len(self.artifacts), 'artifacts': self.artifacts}]
        with mock.patch.object(policy, 'gh_json', side_effect=responses):
            plan = policy.select('owner/repo', 123)
        self.assertEqual(plan['sourceCommit'], self.sha)
        responses[2] = {'total_count': len(self.jobs) + 1, 'jobs': self.jobs}
        with mock.patch.object(policy, 'gh_json', side_effect=responses), self.assertRaisesRegex(ValueError, 'Truncated'):
            policy.select('owner/repo', 123)
        for run_id in ['-1', '0', '123; echo x', '../main', '1' * 21]:
            with self.assertRaises(ValueError):
                policy.select('owner/repo', run_id)

    def packages(self, root):
        for kind, rids in [('Desktop', assemble.update.RIDS), ('CLI', policy.CLI_RIDS)]:
            for rid in rids:
                ext = '.tar.gz' if kind == 'CLI' and not rid.startswith('win-') else '.zip'
                name = f'EasySellPut-{kind}-0.7.0-{rid}' + ext
                package = root / name
                package.write_bytes((name + ' test-only fixture').encode())
                package.with_suffix(package.suffix + '.sha256').write_text(hashlib.sha256(package.read_bytes()).hexdigest() + '  ' + name + '\n')

    def test_all_assets_provenance_hashes_and_no_rebuild(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            packages = root / 'input'; packages.mkdir(); self.packages(packages)
            plan = dict(schemaVersion=1, sourceCommit=self.sha, ciRunId=123, repository='owner/repo', artifacts=policy.validate_artifacts(self.artifacts))
            output = root / 'out'
            with mock.patch.object(assemble, 'shutil', wraps=assemble.shutil):
                index = assemble.prepare(packages, output, '0.7.0', plan)
            self.assertEqual(len(index['assets']), 9)
            self.assertEqual(index['sourceCommit'], self.sha)
            # The real publish verifier accepts only the complete attachment set.
            for name in ('EasySellPut-update.json.sig', 'EasySellPut-release.json.sig'):
                (output / name).write_bytes(b'signature tested separately by openssl')
            publish.verify_assets(output, index)
            update_file = output / 'EasySellPut-update.json'
            update_index = json.loads(update_file.read_text())
            self.assertEqual(update_index['repository'], plan['repository'])
            for wrong_repository in ('other/repo', None):
                update_file.write_text(json.dumps(update_index | {'repository': wrong_repository}))
                with self.assertRaises(ValueError): publish.verify_assets(output, index)
            update_file.write_text(json.dumps(update_index))
            victim = output / index['assets'][-1]['fileName']
            victim.write_bytes(b'tampered')
            with self.assertRaises(ValueError):
                publish.verify_assets(output, index)

    def test_missing_cli_or_modified_checksum_prevents_assembly(self):
        for tamper in (False, True):
            with tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory); packages = root / 'input'; packages.mkdir(); self.packages(packages)
                victim = packages / 'EasySellPut-CLI-0.7.0-linux-x64.tar.gz'
                if tamper:
                    victim.write_bytes(b'tampered')
                else:
                    victim.unlink()
                plan = dict(schemaVersion=1, sourceCommit=self.sha, ciRunId=123, repository='owner/repo', artifacts={})
                with self.assertRaises(ValueError):
                    assemble.prepare(packages, root / 'out', '0.7.0', plan)

    def test_publish_rejects_missing_duplicate_or_path_injected_assets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); packages = root / 'input'; packages.mkdir(); self.packages(packages)
            index = assemble.prepare(packages, root / 'out', '0.7.0', dict(schemaVersion=1, sourceCommit=self.sha, ciRunId=123, repository='owner/repo', artifacts={}))
            for mutate in ('missing', 'duplicate', 'path'):
                changed = copy.deepcopy(index)
                if mutate == 'missing': changed['assets'].pop()
                elif mutate == 'duplicate': changed['assets'].append(changed['assets'][0])
                else: changed['assets'][0]['fileName'] = '../../payload.zip'
                with self.subTest(mutate=mutate), self.assertRaises(ValueError):
                    publish.verify_assets(root / 'out', changed)

    def test_missing_signing_secret_fails_instead_of_unsigned_fallback(self):
        with mock.patch.dict('os.environ', {}, clear=True), self.assertRaisesRegex(ValueError, 'APPLE_SIGNING_IDENTITY'):
            signing.sign_mac([], pathlib.Path('/tmp'))
        with mock.patch.dict('os.environ', {}, clear=True), self.assertRaisesRegex(ValueError, 'WINDOWS_CERTIFICATE_PFX_BASE64'):
            signing.sign_windows([], pathlib.Path('/tmp'))

    def test_signing_key_setup_uploads_only_to_release_environment(self):
        import subprocess
        spec = importlib.util.spec_from_file_location('signing_setup', ROOT / 'build/setup-update-signing.py')
        setup = importlib.util.module_from_spec(spec); spec.loader.exec_module(setup)
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); private = root / 'key.pem'; public = root / 'public.pem'
            subprocess.run(['openssl', 'ecparam', '-name', 'prime256v1', '-genkey', '-noout', '-out', str(private)], check=True)
            private.chmod(0o600)
            subprocess.run(['openssl', 'pkey', '-in', str(private), '-pubout', '-out', str(public)], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            # Keep openssl reads real, but never send this fixture key to GitHub.
            real_run = subprocess.run; uploads = []
            def invoke(command, **kwargs):
                if command[0] == 'gh':
                    uploads.append(command); return subprocess.CompletedProcess(command, 0)
                return real_run(command, **kwargs)
            with mock.patch.object(setup, 'PUBLIC', public), mock.patch.object(sys, 'argv', ['setup', '--key', str(private), '--upload', '--repo', 'owner/repo']), mock.patch.object(setup.subprocess, 'run', side_effect=invoke), mock.patch('sys.stdout'):
                setup.main()
            self.assertEqual(len(uploads), 1)
            command = uploads[0]
            self.assertEqual(command[:4], ['gh', 'secret', 'set', 'UPDATE_SIGNING_KEY'])
            self.assertEqual(command[-2:], ['--env', 'release'])

    def test_publish_rejects_work_before_any_network_or_write(self):
        with mock.patch.dict('os.environ', {'GITHUB_REF': 'refs/heads/work'}), mock.patch.object(sys, 'argv', ['publish.py', '--version', '0.7.0', '--publish']), mock.patch.object(publish.subprocess, 'run') as run:
            with self.assertRaisesRegex(ValueError, 'main'):
                publish.main()
            run.assert_not_called()

    def test_real_index_signatures_gate_explicit_publication_and_reject_tampering(self):
        import shutil
        import subprocess
        import os
        real_run = subprocess.run
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); packages = root / 'input'; packages.mkdir(); self.packages(packages)
            public = root / 'src/Meili.Application/Updates/update-public-key.pem'; public.parent.mkdir(parents=True)
            private = root / 'test-only-key.pem'
            real_run(['openssl', 'ecparam', '-name', 'prime256v1', '-genkey', '-noout', '-out', str(private)], check=True)
            real_run(['openssl', 'pkey', '-in', str(private), '-pubout', '-out', str(public)], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            plan = dict(schemaVersion=1, sourceCommit=self.sha, ciRunId=123, repository='owner/repo', artifacts=policy.validate_artifacts(self.artifacts))
            fixtures = root / 'assets'; index = assemble.prepare(packages, fixtures, '0.7.0', plan)
            for name in ('EasySellPut-update.json', 'EasySellPut-release.json'):
                real_run(['openssl', 'dgst', '-sha256', '-sign', str(private), '-out', str(fixtures / (name + '.sig')), str(fixtures / name)], check=True)
            edits = []
            def invoke(command, **kwargs):
                if command[:3] == ['gh', 'release', 'download']:
                    destination = pathlib.Path(command[command.index('--dir') + 1])
                    shutil.copytree(fixtures, destination, dirs_exist_ok=True)
                    return subprocess.CompletedProcess(command, 0)
                if command[:3] == ['gh', 'release', 'edit']:
                    edits.append(command)
                    return subprocess.CompletedProcess(command, 0)
                if command[0] == 'openssl':
                    return real_run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kwargs)
                raise AssertionError('Unexpected external command')
            with mock.patch.dict(os.environ, {'GITHUB_REF': 'refs/heads/main', 'GITHUB_REPOSITORY': 'owner/repo'}), mock.patch('sys.stdout'), mock.patch.object(publish, 'ROOT', root), mock.patch.object(publish, 'current_main', return_value=self.sha), mock.patch.object(publish, 'resolve_tag', return_value=self.sha), mock.patch.object(publish, 'select', return_value=plan), mock.patch.object(publish.subprocess, 'check_output', return_value=json.dumps({'isDraft': True, 'isPrerelease': False})), mock.patch.object(publish.subprocess, 'run', side_effect=invoke):
                with mock.patch.object(sys, 'argv', ['publish.py', '--version', '0.7.0']):
                    publish.main()
                self.assertFalse(edits, 'Verification alone must never publish')
                with mock.patch.object(sys, 'argv', ['publish.py', '--version', '0.7.0', '--publish']):
                    publish.main()
                self.assertEqual(len(edits), 1)
                self.assertIn('--draft=false', edits[0])
                (fixtures / 'EasySellPut-release.json').write_text('{}')
                with mock.patch.object(sys, 'argv', ['publish.py', '--version', '0.7.0', '--publish']), self.assertRaises(subprocess.CalledProcessError):
                    publish.main()
                self.assertEqual(len(edits), 1, 'Tampering must stop before another publication')

    def test_platform_package_paths_are_validated_before_extraction(self):
        import zipfile
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for i, path in enumerate(['../bad', '/absolute', 'root/../../bad', 'C:/bad', 'root\\bad']):
                package = root / f'{i}.zip'
                entry = zipfile.ZipInfo('placeholder'); entry.filename = path; entry.orig_filename = path
                with zipfile.ZipFile(package, 'w') as z: z.writestr(entry, b'bad')
                with self.subTest(path=path), self.assertRaises(ValueError): signing.extract(package, root / f'out{i}')


if __name__ == '__main__':
    unittest.main()
