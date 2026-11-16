#!/usr/bin/env python3
"""Native desktop + bundled automation packages. Does not publish releases or touch existing distributions."""
import argparse
import hashlib
import json
import os
import pathlib
import platform
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT=pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'build/mac'))
import bundle

def run(*args): subprocess.run([str(a) for a in args],cwd=ROOT,check=True)

def pack(rid,output):
    host=('win' if sys.platform=='win32' else 'osx' if sys.platform=='darwin' else 'linux')
    arch='arm64' if platform.machine().lower() in ('arm64','aarch64') else 'x64'
    if host=='osx':
        probe=subprocess.run(['/usr/sbin/sysctl','-in','hw.optional.arm64'],capture_output=True,text=True)
        if probe.returncode==0 and probe.stdout.strip()=='1':arch='arm64'
    if host=='win':
        import ctypes
        from ctypes import wintypes
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.GetCurrentProcess.restype=wintypes.HANDLE
        kernel.IsWow64Process2.argtypes=[wintypes.HANDLE,ctypes.POINTER(ctypes.c_ushort),ctypes.POINTER(ctypes.c_ushort)]
        process_machine=ctypes.c_ushort();native_machine=ctypes.c_ushort()
        if not kernel.IsWow64Process2(kernel.GetCurrentProcess(),ctypes.byref(process_machine),ctypes.byref(native_machine)):
            raise ctypes.WinError(ctypes.get_last_error())
        arch='arm64' if native_machine.value==0xAA64 else 'x64' if native_machine.value==0x8664 else 'unsupported'

    if rid!=f'{host}-{arch}' or host not in ('win','osx'):raise ValueError('Build and test on the native Windows/macOS architecture')
    version=ET.parse(ROOT/'Directory.Build.props').find('.//Version').text
    name=f'EasySellPut-Desktop-{version}-{rid}';output=output.resolve();output.mkdir(parents=True,exist_ok=True)
    archive=output/(name+'.zip')
    if archive.exists():raise FileExistsError(archive)
    with tempfile.TemporaryDirectory(prefix='.desktop-',dir=output) as work:
        work=pathlib.Path(work);payload=work/name;payload.mkdir()
        publish=work/'publish'
        project=ROOT/('src/Meili.App/Meili.App.csproj' if host=='win' else 'src/Meili.Mac/Meili.Mac.csproj')
        run('dotnet','publish',project,'-c','Release','-r',rid,'--self-contained','true','-m:3','-p:PublishTrimmed=false',f'-p:PublishSingleFile={str(host=="osx").lower()}','-p:IncludeNativeLibrariesForSelfExtract=false','-p:DebugType=None','-o',publish)
        automation=work/'automation'
        run('dotnet','publish',ROOT/'src/Meili.Cli/Meili.Cli.csproj','-c','Release','-r',rid,'--self-contained','true','-m:3','-p:PublishTrimmed=false',f'-p:PublishSingleFile={str(host=="osx").lower()}','-p:IncludeNativeLibrariesForSelfExtract=false','-p:PackAsTool=false','-p:DebugType=None','-p:GenerateDocumentationFile=false','-p:PublishReferencesDocumentationFiles=false','-o',automation)
        if host=='osx':
            icon=work/'AppIcon.icns';run(sys.executable,ROOT/'build/mac/make_icon.py',icon)
            app=payload/'EasySellPut.app';bundle.make_app(publish,app,version,icon)
            shutil.copytree(automation,app/'Contents/Resources/automation')
            # All managed assemblies are single-file; sign native libraries and executables from the inside out.
            for file in sorted(app.rglob('*'),key=lambda p:len(p.parts),reverse=True):
                if not file.is_file() or file.is_symlink() or file==app/'Contents/MacOS/MeiliMac':continue
                with file.open('rb') as stream:magic=stream.read(4)
                if magic in (b'\xcf\xfa\xed\xfe',b'\xca\xfe\xba\xbe'):
                    run('codesign','--force','--sign','-',file)
            run('codesign','--force','--deep','--sign','-',app)
            run('codesign','--verify','--deep','--strict',app)
        else:
            shutil.copytree(publish,payload,dirs_exist_ok=True)
            shutil.copytree(automation,payload/'automation')
            (payload/'MeiliTerminal.exe').rename(payload/'EasySellPut.exe')
            (payload/'portable.flag').write_text('')
        shutil.copytree(ROOT/'skills/easysellput-local',payload/'skills/easysellput-local')
        shutil.copy(ROOT/'LICENSE',payload/'LICENSE')
        shutil.copy(ROOT/'docs/08-CLI与MCP使用说明.md',payload/'CLI-MCP.md')
        (payload/'README.txt').write_text('EasySellPut desktop + CLI/MCP\nmacOS: move EasySellPut.app to Applications before opening.\nOpen EasySellPut, then Settings > Connect AI tools. MCP is read-only by default.\nWindows: installed Evergreen WebView2 runtime required. macOS: ad-hoc signed, not notarized.\nNo Developer ID / Windows publisher signature is included in CI builds.\n',encoding='utf-8')
        with __import__('zipfile').ZipFile(archive,'w') as zipped:
            for file in sorted(payload.rglob('*')):
                bundle.add(zipped,str(file),str(file.relative_to(work)).replace('\\','/'))
    with archive.open('rb') as file:digest=hashlib.file_digest(file,'sha256').hexdigest()
    archive.with_suffix('.zip.sha256').write_text(digest+'  '+archive.name+'\n',encoding='ascii')
    print(archive)
    return archive

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--rid',required=True,choices=['win-x64','win-arm64','osx-x64','osx-arm64']);parser.add_argument('--output',type=pathlib.Path,default=ROOT/'build/out/desktop')
    args=parser.parse_args();pack(args.rid,args.output)
