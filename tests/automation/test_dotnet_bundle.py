import json
import pathlib
import struct
import tempfile
import unittest
from dotnet_bundle import MARKER,assert_self_contained

def string(value):
    encoded=value.encode();assert len(encoded)<128
    return bytes([len(encoded)])+encoded

class DotnetBundleTests(unittest.TestCase):
    def file(self,path,framework_dependent=False,with_core=True):
        config=json.dumps({'runtimeOptions':{'framework':{'name':'Microsoft.NETCore.App','version':'10.0.0'}} if framework_dependent else {'includedFrameworks':[{'name':'Microsoft.NETCore.App','version':'10.0.0'}]}}).encode()
        blob=bytearray(128);blob[24:24+len(MARKER)]=MARKER
        config_offset=len(blob);blob.extend(config);manifest=len(blob)
        blob[16:24]=struct.pack('<Q',manifest)
        name='System.Private.CoreLib.dll' if with_core else 'Other.dll'
        blob.extend(struct.pack('<IIi',6,0,1)+string('fixture')+struct.pack('<qqqqQ',0,0,config_offset,len(config),0))
        blob.extend(struct.pack('<qqqB',config_offset,len(config),0,1)+string(name))
        path.write_bytes(blob)
    def test_embedded_runtime_is_detected_without_separate_coreclr_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path=pathlib.Path(directory)/'app';self.file(path);assert_self_contained(path)
    def test_framework_dependent_or_incomplete_bundle_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path=pathlib.Path(directory)/'app'
            for framework,core in [(True,True),(False,False)]:
                self.file(path,framework,core)
                with self.assertRaises(AssertionError):assert_self_contained(path)
