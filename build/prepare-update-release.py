#!/usr/bin/env python3
"""Assemble a release index from already tested native artifacts; does not sign or publish."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import xml.etree.ElementTree as ET

ROOT=pathlib.Path(__file__).resolve().parents[1]
RIDS=('win-x64','win-arm64','osx-x64','osx-arm64')

def prepare(packages, output, version, repository=None):
    repository=os.environ.get("GITHUB_REPOSITORY", "") if repository is None else repository
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}",repository) or repository.split("/")[1] in (".",".."):raise ValueError("A valid release repository is required")
    if not re.fullmatch(r'\d+\.\d+\.\d+',version):raise ValueError('Use a three-part stable version')
    if output.exists():raise FileExistsError(output)
    output.mkdir(parents=True)
    assets=[]
    for rid in RIDS:
        name=f'EasySellPut-Desktop-{version}-{rid}.zip'
        matches=list(packages.rglob(name))
        if len(matches)!=1:raise ValueError('Exactly one tested package is required: '+name)
        package=matches[0]
        checksum=package.with_suffix('.zip.sha256')
        digest,filename=checksum.read_text(encoding='ascii').strip().split('  ',1)
        with package.open('rb') as stream:actual=hashlib.file_digest(stream,'sha256').hexdigest()
        if filename!=name or actual!=digest or not 0<package.stat().st_size<=768*1024*1024:raise ValueError('Package verification failed: '+name)
        shutil.copy2(package,output/name);shutil.copy2(checksum,output/checksum.name)
        assets.append(dict(rid=rid,fileName=name,sha256=actual,size=package.stat().st_size))
    manifest=dict(schemaVersion=1,version=version,repository=repository,assets=assets)
    (output/'EasySellPut-update.json').write_bytes(json.dumps(manifest,sort_keys=True,separators=(',',':')).encode('utf-8'))
    return manifest

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--packages',type=pathlib.Path,required=True);parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args();version=ET.parse(ROOT/'Directory.Build.props').find('.//Version').text
    prepare(args.packages,args.output,version)
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'],'a') as stream:stream.write('version='+version+'\n')
