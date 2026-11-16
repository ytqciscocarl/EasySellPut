"""Round-trip actual C# templates, including non-BMP Unicode and shell metacharacters."""
import json
import pathlib
import shlex
import subprocess
import tomllib
import unittest
ROOT=pathlib.Path(__file__).resolve().parents[2]

class ConnectionSetupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        dll=ROOT/'tests/Meili.Automation.Host/bin/Release/net10.0/Meili.Automation.Host.dll'
        if not dll.exists():raise unittest.SkipTest('Build the deterministic host before template tests')
        cls.fixtures=json.loads(subprocess.check_output(['dotnet',str(dll),'--connection-fixtures'],cwd=ROOT,timeout=30))
    def test_json_and_toml_are_readonly_and_preserve_paths(self):
        for fixture in self.fixtures:
            expected=dict(command=fixture['Executable'],args=fixture['Arguments'])
            self.assertEqual(json.loads(fixture['Json'])['mcpServers']['easysellput'],expected)
            self.assertEqual(tomllib.loads(fixture['Toml'])['mcp_servers']['easysellput'],expected)
            self.assertNotIn('--allow-writes',fixture['Arguments'])
            self.assertEqual(fixture['Arguments'][-1],fixture['DataDirectory'])
    def test_setup_is_one_user_invoked_connection_command(self):
        for fixture in self.fixtures:
            for key,client in [('CodexSetupCommand','codex'),('ClaudeSetupCommand','claude-code')]:
                setup=fixture[key]
                self.assertNotIn('--allow-writes',setup)
                self.assertNotIn('&&',setup)
                self.assertNotIn('mcp add',setup)
                if fixture['Windows']:
                    self.assertTrue(setup.startswith("& '"))
                    self.assertIn(' connect '+client+' --data-dir ',setup)
                else:
                    self.assertEqual(shlex.split(setup),[fixture['Executable'],'connect',client,'--data-dir',fixture['DataDirectory']])
            self.assertIn('--scope user',fixture['ClaudeCommand'])
    def test_posix_shell_roundtrip_and_powershell_literal_escaping(self):
        for fixture in self.fixtures:
            command=fixture['CodexCommand']
            if fixture['Windows']:
                self.assertIn("My App''s",command)
                self.assertIn("'C:\\Users\\测试\\data ''$HOME'' $(echo unsafe)'",command)
            else:
                self.assertEqual(shlex.split(command),['codex','mcp','add','easysellput','--',fixture['Executable'],*fixture['Arguments']])
