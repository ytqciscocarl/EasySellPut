"""Check immutable update trust metadata from an actual packaged CLI, without network access."""
import base64
import hashlib
import json
import os
import pathlib
import re
import subprocess

ROOT=pathlib.Path(__file__).resolve().parents[2]

def validate(report,repository,public_key):
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}',repository) or repository.split('/')[1] in ('.','..'):raise ValueError('Invalid expected repository')
    match=re.search(r'-----BEGIN PUBLIC KEY-----([A-Za-z0-9+/=\s]+)-----END PUBLIC KEY-----',public_key)
    if match is None:raise ValueError('Missing pinned public key')
    der=base64.b64decode(''.join(match[1].split()),validate=True)
    if report.get('enabled') is not True or report.get('repository')!=repository:raise ValueError('Packaged update repository differs from release repository')
    if report.get('publicKeySha256')!=hashlib.sha256(der).hexdigest():raise ValueError('Packaged update key differs from pinned source key')
    if report.get('historyUrl')!='https://github.com/'+repository+'/releases':raise ValueError('Packaged release history differs from repository')

def check(executable):
    if os.environ.get('GITHUB_ACTIONS')!='true':return
    command=[str(x) for x in executable] if isinstance(executable,(list,tuple)) else [str(executable)]
    result=subprocess.run(command+['update','source','--json'],capture_output=True,text=True,check=True,timeout=30)
    envelope=json.loads(result.stdout)
    if envelope.get('success') is not True:raise ValueError('Update source inspection failed')
    validate(envelope['data'],os.environ['GITHUB_REPOSITORY'],(ROOT/'src/Meili.Application/Updates/update-public-key.pem').read_text())
    print('PASS: packaged update repository and pinned key match this CI/release source')

if __name__=='__main__':
    import sys
    check(sys.argv[1:])
