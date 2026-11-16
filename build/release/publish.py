#!/usr/bin/env python3
"""Verify a main-only signed draft and publish it only with an explicit --publish."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import tempfile
from policy import current_main, require, resolve_tag, select

ROOT = pathlib.Path(__file__).resolve().parents[2]


def verify_assets(directory, index):
    version = index['version']
    require(re.fullmatch(r'\d+\.\d+\.\d+', version) is not None, 'Invalid stable version')
    expected = {(kind, rid) for kind, rids in [('desktop', ('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64')),
                                             ('cli', ('linux-x64', 'win-x64', 'win-arm64', 'osx-x64', 'osx-arm64'))] for rid in rids}
    found = set()
    for asset in index['assets']:
        pair = (asset['kind'], asset['rid'])
        require(pair in expected and pair not in found, 'Unexpected or duplicate release target')
        found.add(pair)
        suffix = '.tar.gz' if pair[0] == 'cli' and not pair[1].startswith('win-') else '.zip'
        name = f'EasySellPut-{"Desktop" if pair[0] == "desktop" else "CLI"}-{version}-{pair[1]}' + suffix
        require(asset['fileName'] == name, 'Unexpected release asset name')
        package = directory / name
        require(0 < asset['size'] <= 768 * 1024 * 1024 and package.stat().st_size == asset['size'], 'Asset size mismatch')
        with package.open('rb') as f:
            actual = hashlib.file_digest(f, 'sha256').hexdigest()
        require(actual == asset['sha256'], 'Asset hash mismatch')
        require(package.with_suffix(package.suffix + '.sha256').read_text(encoding='ascii').strip() == actual + '  ' + name, 'Checksum file mismatch')
    require(found == expected, 'Missing release targets')
    update = json.loads((directory / 'EasySellPut-update.json').read_text(encoding='utf-8'))
    require(update.get('schemaVersion') == 1 and update.get('version') == version and update.get('repository') == index['repository'], 'Update index differs')
    desktop = [{k: a[k] for k in ('rid', 'fileName', 'sha256', 'size')} for a in index['assets'] if a['kind'] == 'desktop']
    require(update['assets'] == desktop, 'Update asset list differs from signed release')
    expected_files = {a['fileName'] for a in index['assets']}
    expected_files |= {n + '.sha256' for n in list(expected_files)}
    expected_files |= {'EasySellPut-update.json', 'EasySellPut-update.json.sig', 'EasySellPut-release.json', 'EasySellPut-release.json.sig'}
    require({p.name for p in directory.iterdir()} == expected_files, 'Unexpected release attachments')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', required=True)
    parser.add_argument('--publish', action='store_true')
    args = parser.parse_args()
    require(os.environ.get('GITHUB_REF') == 'refs/heads/main', 'Publish workflow must run on main')
    require(re.fullmatch(r'\d+\.\d+\.\d+', args.version) is not None, 'Use a stable three-part version')
    repo = os.environ['GITHUB_REPOSITORY']
    tag = 'v' + args.version
    release = json.loads(subprocess.check_output(['gh', 'release', 'view', tag, '--repo', repo, '--json', 'isDraft,isPrerelease'], text=True))
    require(release['isDraft'] and not release['isPrerelease'], 'Only a stable draft may be published')
    # Validate the actual tag, not the editable targetCommitish label.
    sha = resolve_tag(repo, tag)
    require(sha == current_main(repo), 'Draft must target the current main commit')
    public_key = ROOT / 'src/Meili.Application/Updates/update-public-key.pem'
    with tempfile.TemporaryDirectory(prefix='verify release ') as temp:
        directory = pathlib.Path(temp)
        subprocess.run(['gh', 'release', 'download', tag, '--repo', repo, '--dir', temp], check=True)
        for name in ('EasySellPut-update.json', 'EasySellPut-release.json'):
            subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(public_key), '-signature', str(directory / (name + '.sig')), str(directory / name)], check=True)
        index = json.loads((directory / 'EasySellPut-release.json').read_text(encoding='utf-8'))
        require(index.get('schemaVersion') == 1 and index['version'] == args.version and index['repository'] == repo and index['sourceCommit'] == sha, 'Draft provenance differs from main/tag')
        plan = select(repo, index['ciRunId'])
        require(plan['sourceCommit'] == sha and plan['artifacts'] == index['ciArtifacts'], 'CI provenance differs')
        verify_assets(directory, index)
    require(current_main(repo) == sha, 'main advanced during verification; prepare a fresh draft')
    if args.publish:
        subprocess.run(['gh', 'release', 'edit', tag, '--repo', repo, '--draft=false', '--latest'], check=True)
    print(('Published ' if args.publish else 'Verified draft ') + tag + ' at ' + sha)


if __name__ == '__main__':
    main()
