#!/usr/bin/env python3
"""Validate a native desktop bundle, then run its real GUI self-check and bundled CLI/MCP."""
import argparse
import os
import hashlib
import json
import pathlib
import plistlib
import stat
import subprocess
import sys
import tempfile
import zipfile
from check_cli_package import binary_architecture
from dotnet_bundle import assert_self_contained


def check(archive,rid,destination):
    expected,name=archive.with_suffix('.zip.sha256').read_text(encoding='ascii').strip().split('  ',1)
    with archive.open('rb') as stream:assert hashlib.file_digest(stream,'sha256').hexdigest()==expected
    assert name==archive.name and not destination.exists()
    destination.mkdir(parents=True)
    # macOS ditto preserves code signatures and executable modes; Windows has no Unix mode requirement.
    with zipfile.ZipFile(archive) as bundle:
        for entry in bundle.infolist():
            parts=pathlib.PurePosixPath(entry.filename).parts
            assert not entry.filename.startswith('/') and '..' not in parts
        if rid.startswith('osx-'):
            subprocess.run(['ditto','-x','-k',str(archive),str(destination)],check=True)
        else:bundle.extractall(destination)
    root=next(destination.iterdir());assert len(list(destination.iterdir()))==1
    if rid.startswith('win-'):
        gui=root/'EasySellPut.exe';base=root
        assert (root/'portable.flag').is_file()
        assert (root/'coreclr.dll').is_file()
        assert list(root.rglob('WebView2Loader.dll'))
    else:
        app=root/'EasySellPut.app';base=app/'Contents/MacOS';gui=base/'MeiliMac'
        info=plistlib.loads((app/'Contents/Info.plist').read_bytes());assert info['CFBundleExecutable']=='MeiliMac'
        assert (app/'Contents/Resources/AppIcon.icns').is_file()
        assert_self_contained(gui)
        subprocess.run(['codesign','--verify','--deep','--strict',str(app)],check=True)
    cli=(root/'EasySellPut.app/Contents/Resources/automation' if rid.startswith('osx-') else base/'automation')/('easysellput.exe' if rid.startswith('win-') else 'easysellput')
    from check_update_source import check as check_update_source
    check_update_source(cli)
    for executable in (gui,cli):binary_architecture(executable,rid)
    if rid.startswith('osx-'):assert_self_contained(cli)
    report=destination.parent/'desktop-smoke.json'
    with tempfile.TemporaryDirectory(prefix='meili desktop data ') as data:
        result=subprocess.run([str(gui),'--smoke-offline','--background','--no-alerts','--data-dir',data,'--smoke-report',str(report.resolve())],timeout=180)
        assert result.returncode==0, report.read_text(encoding='utf-8') if report.exists() else 'GUI exited without report'
        evidence=json.loads(report.read_text(encoding='utf-8'));assert evidence['Failures']==0,evidence
        if os.environ.get('GITHUB_ACTIONS')=='true':
            from check_update_source import validate, ROOT
            validate(evidence['UpdateSource'],os.environ['GITHUB_REPOSITORY'],(ROOT/'src/Meili.Application/Updates/update-public-key.pem').read_text())
    # Exercise the CLI taken from the extracted GUI bundle, not a developer build.
    subprocess.run([sys.executable,str(pathlib.Path(__file__).with_name('cli_mcp_smoke.py')),'--executable',str(cli)],check=True,timeout=180)
    # Exercise the actual managed updater on the extracted package, then launch that replaced GUI.
    repository=pathlib.Path(__file__).resolve().parents[2]
    subprocess.run(['dotnet','build',str(repository/'tests/Meili.Automation.Host/Meili.Automation.Host.csproj'),'-c','Release','-m:3','-v','quiet'],check=True,cwd=repository,timeout=180)
    host=repository/'tests/Meili.Automation.Host/bin/Release/net10.0/Meili.Automation.Host.dll'
    with tempfile.TemporaryDirectory(prefix='update transaction ',dir=destination.parent) as temporary:
        scratch=pathlib.Path(temporary)
        target=root if rid.startswith('win-') else root/'EasySellPut.app'
        data=target/'data' if rid.startswith('win-') else scratch/'user profile'
        subprocess.run(['dotnet',str(host),'--update-transaction',str(archive),str(target),str(data),str(scratch/'stage')],check=True,timeout=180)
        updated_report=destination.parent/'desktop-smoke-after-update.json'
        result=subprocess.run([str(gui),'--smoke-offline','--background','--no-alerts','--data-dir',str(data),'--smoke-report',str(updated_report.resolve())],timeout=180)
        assert result.returncode==0,updated_report.read_text(encoding='utf-8') if updated_report.exists() else 'Updated GUI exited without report'
        assert (data/'updater-user-data.marker').read_text()=='retain exact user data'
        if rid.startswith('osx-'):subprocess.run(['codesign','--verify','--deep','--strict',str(target)],check=True)
    print('PASS: desktop checksum, resources, architecture, real GUI startup/clipboard and bundled CLI/MCP')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--archive',type=pathlib.Path,required=True);p.add_argument('--rid',required=True);p.add_argument('--extract-to',type=pathlib.Path,required=True)
    a=p.parse_args();check(a.archive.resolve(),a.rid,a.extract_to.resolve())
