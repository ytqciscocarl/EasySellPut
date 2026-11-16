"""Exercise the user-invoked setup against fixture clients, never real client profiles."""
import json
import os
import pathlib
import subprocess
import sys
import tempfile

FAKE = r'''
import json, os, pathlib, sys
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
client=sys.argv[1];args=sys.argv[2:]
state=pathlib.Path(os.environ['ESP_TEST_CLIENT_STATE']+'-'+client+'.json')
if args==['--version']:
    print(client+' fixture 1.0');sys.exit(8 if os.environ.get('ESP_TEST_FAIL_VERSION') else 0)
if args[:3]==['mcp','get','easysellput']:
    if not state.exists():
        print('No MCP server named easysellput found',file=sys.stderr);sys.exit(1)
    value=json.loads(state.read_text(encoding='utf-8'))
    if client=='codex':print(json.dumps({'enabled':True,'transport':{'type':'stdio',**value}}))
    else:print('Scope: User config (available in all your projects)\nType: stdio\nCommand: '+value['command']+'\nArgs: '+' '.join(value['args']))
    sys.exit(0)
if args[:2]==['mcp','remove']:
    state.unlink();print('removed fixture');sys.exit(0)
if args[:2]==['mcp','add']:
    if state.exists():sys.exit(6)
    if os.environ.get('ESP_TEST_FAIL_ADD'):sys.exit(9)
    tail=args[args.index('--')+1:]
    assert '--allow-writes' not in tail
    if client=='claude-code':assert args[args.index('--scope')+1]=='user'
    state.write_text(json.dumps({'command':tail[0],'args':tail[1:]}),encoding='utf-8')
    print('registered fixture');sys.exit(0)
sys.exit(7)
'''


def check_connections(command, data):
    with tempfile.TemporaryDirectory(prefix='easysellput-client-fixtures-') as temporary:
        root=pathlib.Path(temporary);bin_dir=root/'client shims';bin_dir.mkdir()
        source=root/'fixture client.py';source.write_text(FAKE,encoding='utf-8')
        for name,client in [('codex','codex'),('claude','claude-code')]:
            if os.name=='nt':
                (bin_dir/(name+'.cmd')).write_text('@echo off\n"'+sys.executable+'" "'+str(source)+'" '+client+' %*\n',encoding='utf-8')
            else:
                shim=bin_dir/name
                shim.write_text('#!'+sys.executable+'\nimport runpy,sys\nsys.argv=['+repr(str(source))+','+repr(client)+']+sys.argv[1:]\nrunpy.run_path('+repr(str(source))+',run_name="__main__")\n',encoding='utf-8');shim.chmod(0o755)
        environment={**os.environ,'PATH':str(bin_dir)+os.pathsep+os.environ.get('PATH',''),'ESP_TEST_CLIENT_STATE':str(root/'registered'),'PYTHONIOENCODING':'utf-8'}
        # Shell metacharacters are data. No real client executable/profile is touched.
        selected_data=pathlib.Path(data)/"data 中文 '$HOME' $(echo unsafe) & literal"
        for client,folder in [('codex','.agents'),('claude-code','.claude')]:
            project=root/('project '+client)
            def connect(*extra,code=0,env=None):
                result=subprocess.run([*command,'connect',client,'--project',str(project),'--data-dir',str(selected_data),'--json',*extra],capture_output=True,text=True,timeout=90,env=env or environment)
                if result.returncode!=code:
                    diagnostic=subprocess.run([*command,'connect',client,'--project',str(project),'--data-dir',str(selected_data),'--check'],capture_output=True,text=True,timeout=90,env=env or environment)
                    raise AssertionError((client,extra,result.returncode,result.stdout,result.stderr,'safe preflight diagnostics',diagnostic.stdout,diagnostic.stderr))
                return json.loads(result.stdout)
            checked=connect('--check')['data'];assert checked['status']=='checked' and checked['changesApplied'] is False
            assert not project.exists()
            first=connect()['data'];assert first['status']=='configured' and first['readOnly'] is True
            installed=project/folder/'skills/easysellput-local/SKILL.md';assert installed.exists()
            state=root/('registered-'+client+'.json');value=json.loads(state.read_text(encoding='utf-8'))
            assert value['args'][:3]==['mcp','serve','--data-dir'],value
            assert os.path.normcase(os.path.realpath(value['args'][3]))==os.path.normcase(os.path.realpath(selected_data)),(value,str(selected_data))
            assert connect()['data']['status']=='already_configured'
            value['command']='another executable';state.write_text(json.dumps(value),encoding='utf-8');before=state.read_bytes()
            conflict=connect(code=4);assert conflict['error']['code']=='connection_conflict' and conflict['error']['recovery']
            assert state.read_bytes()==before
            assert connect('--replace')['data']['status']=='configured'
            installed.write_text('user custom skill',encoding='utf-8');before=state.read_bytes()
            assert connect(code=4)['error']['code']=='skill_conflict'
            assert installed.read_text(encoding='utf-8')=='user custom skill' and state.read_bytes()==before
        failed_project=root/'failed version'
        failed=subprocess.run([*command,'connect','codex','--project',str(failed_project),'--data-dir',str(selected_data),'--json'],capture_output=True,text=True,timeout=30,env={**environment,'ESP_TEST_FAIL_VERSION':'1'})
        assert failed.returncode==1 and json.loads(failed.stdout)['error']['code']=='client_setup_failed'
        assert not failed_project.exists()
    print('PASS: fixture client preflight, real read-only MCP verification, skill install, repeated setup, config conflict/explicit replacement and edited-skill protection')
