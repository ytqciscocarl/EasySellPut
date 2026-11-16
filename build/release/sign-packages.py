#!/usr/bin/env python3
"""Sign existing CI binaries on their native OS; never rebuild the application."""
import argparse
import base64
import hashlib
import json
import os
import pathlib
import re
import secrets
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]


def run(*args, **kwargs):
    try:
        return subprocess.run([str(a) for a in args], check=True, **kwargs)
    except subprocess.CalledProcessError as error:
        # Never print command arguments: security import/unlock includes passwords.
        raise RuntimeError(pathlib.Path(str(args[0])).name + ' failed with exit code ' + str(error.returncode)) from None


def secret(name):
    value = os.environ.get(name, '')
    if not value:
        raise ValueError('Configure release environment secret: ' + name)
    return value


def verified_package(folder, name):
    matches = list(folder.rglob(name))
    if len(matches) != 1:
        raise ValueError('Exactly one package required: ' + name)
    package = matches[0]
    expected, filename = package.with_suffix(package.suffix + '.sha256').read_text(encoding='ascii').strip().split('  ', 1)
    with package.open('rb') as f:
        actual = hashlib.file_digest(f, 'sha256').hexdigest()
    if filename != name or expected != actual:
        raise ValueError('CI package checksum mismatch: ' + name)
    return package


def extract(package, destination):
    destination.mkdir()
    if package.suffix == '.zip':
        with zipfile.ZipFile(package) as z:
            for item in z.infolist():
                name = item.orig_filename  # ZipInfo normalizes backslashes on Windows; validate the raw name.
                if '\\' in name or name.startswith('/') or '..' in pathlib.PurePosixPath(name).parts or ':' in name:
                    raise ValueError('Unsafe package path')
        if sys.platform == 'darwin':
            run('ditto', '-x', '-k', package, destination)
        else:
            with zipfile.ZipFile(package) as z:
                z.extractall(destination)
    else:
        with tarfile.open(package, 'r:gz') as t:
            t.extractall(destination, filter='data')
    roots = list(destination.iterdir())
    if len(roots) != 1 or not roots[0].is_dir():
        raise ValueError('Expected one package root')
    return roots[0]


def archive(root, target):
    if target.suffix == '.zip':
        if sys.platform == 'darwin':
            run('ditto', '-c', '-k', '--keepParent', '--norsrc', root, target)
        else:
            with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED) as z:
                for p in sorted(root.rglob('*')):
                    z.write(p, p.relative_to(root.parent).as_posix())
    else:
        with tarfile.open(target, 'w:gz') as t:
            t.add(root, arcname=root.name)
    with target.open('rb') as f:
        digest = hashlib.file_digest(f, 'sha256').hexdigest()
    target.with_suffix(target.suffix + '.sha256').write_text(digest + '  ' + target.name + '\n', encoding='ascii')


def macho(path):
    if not path.is_file() or path.is_symlink():
        return False
    with path.open('rb') as f:
        return f.read(4) in (b'\xcf\xfa\xed\xfe', b'\xca\xfe\xba\xbe')


def sign_mac(roots, scratch):
    identity = secret('APPLE_SIGNING_IDENTITY')
    if not re.fullmatch(r'Developer ID Application: .+ \([A-Z0-9]{10}\)', identity):
        raise ValueError('Use a Developer ID Application identity')
    cert = scratch / 'certificate.p12'
    cert.write_bytes(base64.b64decode(secret('APPLE_CERTIFICATE_P12_BASE64'), validate=True))
    key = scratch / 'notary.p8'
    key.write_text(secret('APPLE_NOTARY_KEY_P8'), encoding='utf-8')
    key.chmod(0o600)
    key_id, issuer = secret('APPLE_NOTARY_KEY_ID'), secret('APPLE_NOTARY_ISSUER')
    keychain = scratch / 'release.keychain-db'
    password = secrets.token_urlsafe(32)
    original = subprocess.check_output(['security', 'list-keychains', '-d', 'user'], text=True)
    original_paths = re.findall(r'"([^"]+)"', original)
    try:
        run('security', 'create-keychain', '-p', password, keychain)
        run('security', 'set-keychain-settings', '-lut', '21600', keychain)
        run('security', 'unlock-keychain', '-p', password, keychain)
        run('security', 'import', cert, '-k', keychain, '-P', secret('APPLE_CERTIFICATE_PASSWORD'), '-T', '/usr/bin/codesign', stdout=subprocess.DEVNULL)
        run('security', 'set-key-partition-list', '-S', 'apple-tool:,apple:', '-s', '-k', password, keychain, stdout=subprocess.DEVNULL)
        run('security', 'list-keychains', '-d', 'user', '-s', keychain, *original_paths)
        for root in roots:
            files = sorted((p for p in root.rglob('*') if macho(p)), key=lambda p: len(p.parts), reverse=True)
            if not files:
                raise ValueError('No Mach-O binaries in package')
            for p in files:
                command = ['codesign', '--force', '--sign', identity, '--keychain', keychain, '--timestamp', '--options', 'runtime']
                with p.open('rb') as f:
                    header = f.read(16)
                if header[:4] == b'\xcf\xfa\xed\xfe' and struct.unpack_from('<I', header, 12)[0] == 2:
                    command += ['--entitlements', ROOT / 'build/release/macos-entitlements.plist']
                run(*command, p)
            apps = list(root.glob('*.app'))
            for app in apps:
                run('codesign', '--force', '--sign', identity, '--keychain', keychain, '--timestamp', '--options', 'runtime', '--entitlements', ROOT / 'build/release/macos-entitlements.plist', app)
                run('codesign', '--verify', '--deep', '--strict', app)
            submission = scratch / (root.name + '-notary.zip')
            run('ditto', '-c', '-k', '--keepParent', root, submission)
            result = subprocess.check_output(['xcrun', 'notarytool', 'submit', str(submission), '--key', str(key), '--key-id', key_id, '--issuer', issuer, '--wait', '--timeout', '20m', '--output-format', 'json'], text=True, timeout=1320)
            report = json.loads(result)
            if report.get('status') != 'Accepted':
                raise ValueError('Apple notarization was not accepted: ' + str(report.get('id')))
            for app in apps:
                run('xcrun', 'stapler', 'staple', app)
                run('xcrun', 'stapler', 'validate', app)
                run('spctl', '--assess', '--type', 'execute', '--verbose=2', app)
            if not apps:
                run('codesign', '--verify', '--strict', root / 'easysellput')
                run('spctl', '--assess', '--type', 'execute', '--verbose=2', root / 'easysellput')
    finally:
        run('security', 'list-keychains', '-d', 'user', '-s', *original_paths)
        subprocess.run(['security', 'delete-keychain', str(keychain)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cert.unlink(missing_ok=True)
        key.unlink(missing_ok=True)


def sign_windows(roots, scratch):
    cert = scratch / 'certificate.pfx'
    cert.write_bytes(base64.b64decode(secret('WINDOWS_CERTIFICATE_PFX_BASE64'), validate=True))
    secret('WINDOWS_CERTIFICATE_PASSWORD')
    os.environ['RELEASE_CERT_PATH'] = str(cert)
    script = "$ErrorActionPreference='Stop'; $p=ConvertTo-SecureString $env:WINDOWS_CERTIFICATE_PASSWORD -AsPlainText -Force; $c=@(Import-PfxCertificate -FilePath $env:RELEASE_CERT_PATH -CertStoreLocation Cert:\\CurrentUser\\My -Password $p | Where-Object {$_.HasPrivateKey}); if($c.Count -ne 1){throw 'Expected one signing certificate'}; $c.Thumbprint"
    thumb = subprocess.check_output(['pwsh', '-NoProfile', '-Command', script], text=True).strip()
    if not re.fullmatch('[A-Fa-f0-9]{40}', thumb):
        raise ValueError('Invalid signing certificate thumbprint')
    sdk = pathlib.Path(os.environ['ProgramFiles(x86)']) / 'Windows Kits/10/bin'
    architecture = 'arm64' if os.environ.get('PROCESSOR_ARCHITECTURE', '').upper() == 'ARM64' else 'x64'
    tools = sorted(sdk.glob('*/' + architecture + '/signtool.exe'))
    if not tools:
        raise ValueError('Windows SDK signtool is required')
    try:
        for root in roots:
            executables = list(root.rglob('*.exe'))
            if not executables:
                raise ValueError('No executable in package')
            for p in executables:
                run(tools[-1], 'sign', '/sha1', thumb, '/s', 'My', '/fd', 'SHA256', '/tr', 'https://timestamp.digicert.com', '/td', 'SHA256', p)
                run(tools[-1], 'verify', '/pa', '/all', p)
    finally:
        run('pwsh', '-NoProfile', '-Command', "Remove-Item -LiteralPath 'Cert:\\CurrentUser\\My\\" + thumb + "' -Force -DeleteKey")
        cert.unlink(missing_ok=True)
        os.environ.pop('RELEASE_CERT_PATH', None)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--packages', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--rid', required=True, choices=('linux-x64', 'win-x64', 'win-arm64', 'osx-x64', 'osx-arm64'))
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    if os.environ.get('GITHUB_REF') != 'refs/heads/main':
        raise ValueError('Signing is restricted to main')
    if not re.fullmatch(r'\d+\.\d+\.\d+', args.version):
        raise ValueError('Use a stable version')
    if args.output.exists():
        raise FileExistsError(args.output)
    args.output.mkdir(parents=True)
    cli_ext = '.zip' if args.rid.startswith('win-') else '.tar.gz'
    names = [f'EasySellPut-CLI-{args.version}-{args.rid}' + cli_ext]
    if args.rid != 'linux-x64':
        names.insert(0, f'EasySellPut-Desktop-{args.version}-{args.rid}.zip')
    with tempfile.TemporaryDirectory(prefix='release signing ') as temp:
        scratch = pathlib.Path(temp)
        packages = [verified_package(args.packages, name) for name in names]
        if args.rid == 'linux-x64':
            # Linux artifacts stay byte-for-byte identical; the release index authenticates them.
            for p in packages:
                shutil.copy2(p, args.output / p.name)
                shutil.copy2(p.with_suffix(p.suffix + '.sha256'), args.output)
            return
        roots = [extract(p, scratch / str(i)) for i, p in enumerate(packages)]
        for root in roots:
            readme = root / 'README.txt'
            if readme.exists():
                readme.write_text('EasySellPut desktop + CLI/MCP\nOfficial release: platform signatures verified; macOS Developer ID signed and notarized.\nmacOS: move EasySellPut.app to Applications.\nWindows: system Evergreen WebView2 runtime required.\n', encoding='utf-8')
        if args.rid.startswith('osx-'):
            if sys.platform != 'darwin':
                raise ValueError('Use a native macOS runner')
            sign_mac(roots, scratch)
        else:
            if sys.platform != 'win32':
                raise ValueError('Use a native Windows runner')
            sign_windows(roots, scratch)
        for root, name in zip(roots, names):
            archive(root, args.output / name)


if __name__ == '__main__':
    main()
