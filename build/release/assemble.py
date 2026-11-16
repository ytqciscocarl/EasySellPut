#!/usr/bin/env python3
"""Assemble all tested release assets and signed provenance; never compile code."""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import re
import shutil
import xml.etree.ElementTree as ET
from policy import CLI_RIDS

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('update_release', ROOT / 'build/prepare-update-release.py')
update = importlib.util.module_from_spec(spec)
spec.loader.exec_module(update)


def prepare(packages, output, version, plan):
    if not re.fullmatch('[0-9a-f]{40}', plan['sourceCommit']) or plan.get('schemaVersion') != 1:
        raise ValueError('Invalid release provenance')
    manifest = update.prepare(packages, output, version, plan["repository"])
    assets = [dict(a, kind='desktop') for a in manifest['assets']]
    for rid in CLI_RIDS:
        extension = '.zip' if rid.startswith('win-') else '.tar.gz'
        name = f'EasySellPut-CLI-{version}-{rid}' + extension
        matches = list(packages.rglob(name))
        if len(matches) != 1:
            raise ValueError('Exactly one tested CLI package required: ' + name)
        package = matches[0]
        checksum = package.with_suffix(package.suffix + '.sha256')
        digest, filename = checksum.read_text(encoding='ascii').strip().split('  ', 1)
        with package.open('rb') as f:
            actual = hashlib.file_digest(f, 'sha256').hexdigest()
        if filename != name or digest != actual or not 0 < package.stat().st_size <= 768 * 1024 * 1024:
            raise ValueError('Invalid CLI package: ' + name)
        shutil.copy2(package, output / name)
        shutil.copy2(checksum, output / checksum.name)
        assets.append(dict(rid=rid, kind='cli', fileName=name, size=package.stat().st_size, sha256=actual))
    provenance = dict(schemaVersion=1, version=version, sourceCommit=plan['sourceCommit'],
                      repository=plan['repository'], ciRunId=plan['ciRunId'],
                      ciArtifacts=plan['artifacts'], assets=assets,
                      platformSigning='Developer ID + Apple notarization / Windows Authenticode; native package checks passed')
    (output / 'EasySellPut-release.json').write_text(json.dumps(provenance, sort_keys=True, separators=(',', ':')), encoding='utf-8')
    return provenance


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--packages', type=pathlib.Path, required=True)
    parser.add_argument('--plan', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    version = ET.parse(ROOT / 'Directory.Build.props').find('.//Version').text
    prepare(args.packages, args.output, version, json.loads(args.plan.read_text(encoding='utf-8')))
    import os
    with open(os.environ['GITHUB_OUTPUT'], 'a') as f:
        f.write('version=' + version + '\n')
