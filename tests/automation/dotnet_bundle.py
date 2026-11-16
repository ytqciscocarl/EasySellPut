"""Inspect the .NET single-file bundle manifest (SDK-pinned format v2 through v6)."""
import json
import pathlib
import struct

MARKER=bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')

def inspect_bundle(executable):
    blob=pathlib.Path(executable).read_bytes()
    marker=blob.find(MARKER)
    assert marker>=8,'Missing .NET single-file marker'
    cursor=struct.unpack_from('<Q',blob,marker-8)[0]
    assert 0<cursor<len(blob),'Missing bundle manifest'
    major,minor,count=struct.unpack_from('<IIi',blob,cursor);cursor+=12
    assert major in (2,6) and 0<count<10000,'Unsupported or invalid bundle format'
    def string():
        nonlocal cursor
        length=0
        for shift in range(0,35,7):
            value=blob[cursor];cursor+=1;length|=(value&127)<<shift
            if value<128:break
        else:raise AssertionError('Invalid string length')
        assert length<100000 and cursor+length<=len(blob)
        value=blob[cursor:cursor+length].decode('utf-8');cursor+=length;return value
    bundle_id=string()
    deps_offset,deps_size,config_offset,config_size,flags=struct.unpack_from('<qqqqQ',blob,cursor);cursor+=40
    files=[]
    for _ in range(count):
        offset,size=struct.unpack_from('<qq',blob,cursor);cursor+=16
        compressed=0
        if major>=6:compressed=struct.unpack_from('<q',blob,cursor)[0];cursor+=8
        kind=blob[cursor];cursor+=1
        path=string();assert 0<=offset<len(blob) and size>=0
        files.append(path)
    assert 0<config_size<1000000 and config_offset+config_size<=len(blob)
    config=json.loads(blob[config_offset:config_offset+config_size])
    return config,files

def assert_self_contained(executable):
    config,files=inspect_bundle(executable)
    options=config['runtimeOptions']
    assert not options.get('framework') and not options.get('frameworks'),'Framework-dependent app'
    assert any(f['name']=='Microsoft.NETCore.App' for f in options.get('includedFrameworks',[])),'Runtime not declared bundled'
    assert 'System.Private.CoreLib.dll' in files,'Core managed runtime missing from bundle'
