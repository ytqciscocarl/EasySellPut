#!/usr/bin/env python3
"""One-time operator setup. Keeps the private key outside Git; never publishes a release."""
import argparse
import os
import pathlib
import subprocess

ROOT=pathlib.Path(__file__).resolve().parents[1]
PUBLIC=ROOT/'src/Meili.Application/Updates/update-public-key.pem'

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--key',type=pathlib.Path,required=True,help='Private key backup outside this checkout')
    parser.add_argument('--initialize',action='store_true',help='First release only: generate a key and pin its public half')
    parser.add_argument('--upload',action='store_true',help='Upload to the main-only release Environment using your local gh login')
    parser.add_argument('--repo',default='two4i6/EasySellPut')
    args=parser.parse_args();original=args.key.expanduser().absolute();key=original.resolve()
    if key.is_relative_to(ROOT.resolve()) or original.is_symlink():raise SystemExit('Keep the private key outside the repository in a regular user-owned file.')
    if args.initialize:
        published=subprocess.check_output(['gh','release','list','--repo',args.repo,'--exclude-drafts','--limit','1','--json','tagName'])
        if __import__('json').loads(published):raise SystemExit('Published releases exist. Keep the original signing key; key rotation requires a separate migration.')
    if not key.exists():
        if not args.initialize:raise SystemExit('Private key missing. Use --initialize before the first release.')
        key.parent.mkdir(parents=True,exist_ok=True)
        previous=os.umask(0o077)
        try:
            with key.open('xb') as output:subprocess.run(['openssl','ecparam','-name','prime256v1','-genkey','-noout'],stdout=output,check=True)
        finally:os.umask(previous)
    if os.name!='nt' and key.stat().st_mode & 0o077:raise SystemExit('Restrict the private key to owner access (chmod 600).')
    public=subprocess.check_output(['openssl','pkey','-in',str(key),'-pubout'])
    description=subprocess.check_output(['openssl','pkey','-pubin','-text','-noout'],input=public)
    if b'prime256v1' not in description:raise SystemExit('Use an ECDSA P-256 signing key.')
    normalize=lambda value:value.replace(b'\r\n',b'\n').strip()
    if normalize(PUBLIC.read_bytes())!=normalize(public) and not args.initialize:raise SystemExit('This private key does not match the pinned public key.')
    if args.upload:
        subprocess.run(['gh','secret','set','UPDATE_SIGNING_KEY','--repo',args.repo,'--env','release'],input=key.read_bytes(),check=True)
    PUBLIC.write_bytes(public)
    print('Public key pinned. Private key stays in your chosen backup file; keep it secure.')
    print('Commit the public key and wait for native CI before preparing the signed release draft.')
    if not args.upload:print('Upload later with this script --key <same-file> --upload. No release was published.')

if __name__=='__main__':main()
