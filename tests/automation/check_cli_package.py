#!/usr/bin/env python3
"""Verify and extract a locally built CLI/MCP distribution before native process tests."""
import argparse
import hashlib
import json
import pathlib
import stat
import struct
import tarfile
import zipfile


def binary_architecture(executable, rid):
    with executable.open('rb') as stream:
        header = stream.read(64)
        if rid.startswith('win-'):
            assert header[:2] == b'MZ', 'Expected a Windows executable'
            stream.seek(struct.unpack_from('<I', header, 60)[0])
            pe = stream.read(6)
            assert pe[:4] == b'PE\0\0'
            machine = struct.unpack_from('<H', pe, 4)[0]
            assert machine == (0xAA64 if rid.endswith('arm64') else 0x8664), 'Wrong Windows architecture'
        elif rid.startswith('osx-'):
            assert header[:4] == b'\xcf\xfa\xed\xfe', 'Expected a 64-bit Mach-O executable'
            machine = struct.unpack_from('<I', header, 4)[0]
            assert machine == (0x100000C if rid.endswith('arm64') else 0x1000007), 'Wrong macOS architecture'
        else:
            assert header[:4] == b'\x7fELF', 'Expected an ELF executable'
            machine = struct.unpack_from('<H', header, 18)[0]
            assert machine == (183 if rid.endswith('arm64') else 62), 'Wrong Linux architecture'


def check(archive, rid, destination):
    digest_file = archive.with_suffix(archive.suffix + '.sha256')
    expected, filename = digest_file.read_text(encoding='ascii').strip().split('  ', 1)
    assert filename == archive.name
    with archive.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    assert expected.lower() == actual, 'Archive checksum mismatch'
    assert not destination.exists(), 'Use a new extraction directory; existing files are never overwritten'
    destination.mkdir(parents=True)
    if archive.suffix == '.zip':
        with zipfile.ZipFile(archive) as bundle:
            for name in bundle.namelist():
                assert not name.startswith('/') and '..' not in pathlib.PurePosixPath(name).parts
            bundle.extractall(destination)
    else:
        with tarfile.open(archive, 'r:gz') as bundle:
            bundle.extractall(destination, filter='data')
    roots = list(destination.iterdir())
    assert len(roots) == 1 and roots[0].is_dir(), 'Expected one distribution root'
    root = roots[0]
    executable = root / ('easysellput.exe' if rid.startswith('win-') else 'easysellput')
    assert executable.is_file()
    binary_architecture(executable, rid)
    deps = json.loads((root / 'easysellput.deps.json').read_text(encoding='utf-8-sig'))
    assert deps['runtimeTarget']['name'].endswith('/' + rid)
    forbidden = ('webview2', 'cefsharp', 'chromium', 'meili.shared', 'avalonia')
    assert not any(any(term in name.lower() for term in forbidden) for name in deps['libraries']), 'Desktop/browser dependency leaked into CLI/MCP'
    assert not any('webview2' in path.name.lower() for path in root.rglob('*')), 'Browser payload leaked into CLI/MCP package'
    runtime = json.loads((root / 'easysellput.runtimeconfig.json').read_text(encoding='utf-8-sig'))['runtimeOptions']
    assert 'framework' not in runtime and 'frameworks' not in runtime, 'Expected a self-contained distribution'
    assert any(f['name'] == 'Microsoft.NETCore.App' for f in runtime['includedFrameworks'])
    config = json.loads((root / 'mcp-config.example.json').read_text(encoding='utf-8-sig'))['mcpServers']['easysellput']
    assert config['args'][:2] == ['mcp', 'serve']
    if rid.startswith('win-'):
        assert pathlib.PureWindowsPath(config['command']).is_absolute() and config['command'].endswith('.exe')
    else:
        assert config['command'].startswith('/') and config['command'].endswith('/easysellput')
        assert executable.stat().st_mode & stat.S_IXUSR, 'Executable permission lost in archive'
        if rid.startswith('osx-'):
            assert config['command'].startswith('/Users/'), 'Expected a macOS-specific MCP example'
    assert (root / 'LICENSE').is_file() and (root / 'CLI-MCP.md').is_file()
    assert (root / '12-Windows与macOS的CLI和MCP.md').is_file(), 'Platform quick start is missing'
    from check_update_source import check as check_update_source
    check_update_source(executable)
    print(f'PASS: {rid} checksum, native binary, bundled runtime, MCP configuration and archive layout: {root}')
    return root


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--archive', required=True, type=pathlib.Path)
    p.add_argument('--rid', required=True, choices=['win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64'])
    p.add_argument('--extract-to', required=True, type=pathlib.Path)
    args = p.parse_args()
    check(args.archive.resolve(), args.rid, args.extract_to.resolve())


if __name__ == '__main__':
    main()
