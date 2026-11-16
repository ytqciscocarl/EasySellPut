#!/usr/bin/env python3
"""Real-process CLI and MCP protocol checks; standard library only, no network calls."""
import argparse, json, os, pathlib, queue, subprocess, tempfile, threading, time
from connection_smoke import check_connections


def run(command, data, *args, code=0):
    result = subprocess.run([*command, *args, '--data-dir', data, '--json'], capture_output=True, text=True, timeout=20)
    assert result.returncode == code, (args, result.returncode, result.stderr, result.stdout)
    body = json.loads(result.stdout)
    assert body['schemaVersion'] == 1 and body['success'] == (code == 0), body
    return body


class Mcp:
    def __init__(self, command, data, writes=False, response_timeout=15):
        self.process = subprocess.Popen([*command, 'mcp', 'serve', '--data-dir', data, *(['--allow-writes'] if writes else [])], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
        self.data = pathlib.Path(data)
        self.response_timeout = response_timeout
        self.messages = queue.Queue()
        self.errors = []
        self.next_id = 0
        def read():
            try:
                for line in self.process.stdout:
                    self.messages.put(json.loads(line))
            except Exception as exc:
                self.messages.put(exc)
        def errors():
            for line in self.process.stderr:
                self.errors.append(line)
        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=errors, daemon=True).start()
        initialized = self.request('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'EasySellPut smoke', 'version': '1.0'}})
        assert initialized['result']['serverInfo']['name'] == 'EasySellPut', initialized
        assert 'tools' in initialized['result']['capabilities']
        self.send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + '\n')
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.next_id += 1
        ident = self.next_id
        self.send({'jsonrpc': '2.0', 'id': ident, 'method': method, 'params': params or {}})
        while True:
            try:
                message = self.messages.get(timeout=self.response_timeout)
            except queue.Empty:
                raise AssertionError(('MCP response timed out', method, self.process.poll(), ''.join(self.errors)))
            if isinstance(message, Exception):
                raise message
            if message.get('id') == ident:
                return message

    def call(self, name, arguments):
        response = self.request('tools/call', {'name': name, 'arguments': arguments})
        assert 'result' in response, response
        result = response['result']
        assert json.loads(result['content'][0]['text']) == result['structuredContent'], result
        return result

    def check_live_status(self, writes):
        deadline=time.monotonic()+8
        while time.monotonic()<deadline:
            records=[json.loads(p.read_text(encoding='utf-8')) for p in (self.data/'.mcp-runtime').glob('*.json')]
            own=[r for r in records if r['processId']==self.process.pid]
            if own:
                assert own[0]['client']=='EasySellPut smoke' and own[0]['readOnly'] is not writes, own
                return
            time.sleep(.1)
        raise AssertionError('Initialized MCP process never published live diagnostics')

    def close(self):
        self.process.stdin.close()
        try:
            status = self.process.wait(timeout=10)
            assert not list((self.data/'.mcp-runtime').glob('*.json')), 'Session lease did not clean up'
            assert status == 0, (status, ''.join(self.errors))
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=5)
            raise AssertionError('MCP server did not exit on stdin EOF')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dll', default='src/Meili.Cli/bin/Debug/net10.0/easysellput.dll')
    parser.add_argument('--executable', help='Test a published self-contained executable instead of dotnet DLL')
    args = parser.parse_args()
    command = [str(pathlib.Path(args.executable).resolve())] if args.executable else ['dotnet', str(pathlib.Path(args.dll).resolve())]
    with tempfile.TemporaryDirectory(prefix='easysellput-process-') as data:
        for client,folder in [('codex','.agents'),('claude-code','.claude')]:
            project=pathlib.Path(data)/('skill project '+client)
            def install(expected=0):
                process=subprocess.run([*command,'install-skill','--client',client,'--project',str(project),'--json'],capture_output=True,text=True,timeout=20)
                assert process.returncode==expected,(process.stdout,process.stderr)
                return json.loads(process.stdout)
            first=install();assert first['data']['status']=='installed'
            skill=project/folder/'skills/easysellput-local/SKILL.md'
            assert skill.is_file() and 'name: easysellput-local' in skill.read_text(encoding='utf-8')
            assert install()['data']['status']=='already_installed'
            skill.write_text('user custom skill',encoding='utf-8')
            assert install(4)['error']['code']=='skill_conflict'
            assert skill.read_text(encoding='utf-8')=='user custom skill'
        check_connections(command,data)
        catalog = run(command, data, '--help')['data']
        assert 'install-skill' not in {op['name'] for op in catalog['operations']}
        operations = {op['name']: op for op in catalog['operations']}
        readonly = {name for name, op in operations.items() if not op['writes']}
        sources = run(command, data, 'sources')['data']
        assert sources['scope'] == 'current_process' and sources['sources']
        assert all(source['state'] == 'Unknown' for source in sources['sources'])
        version = subprocess.run([*command, '--version'], capture_output=True, text=True, timeout=10)
        assert version.returncode == 0 and version.stdout.strip() == catalog['version']
        price = run(command, data, 'price', '--right', 'call', '--spot', '100', '--strike', '100', '--years', '1', '--rate', '0.05', '--volatility', '0.2')
        assert abs(price['data']['greeks']['price'] - 10.45058357) < 1e-6
        for tail in [('quote', 'INVALID/NAME'), ('get_settings', '--invented', 'x'), ('price', '--spot', '100'), ('get_settings', '--timeout', '0'), ('get_bars', 'AAPL', '--limit', '999999999999999999999')]:
            assert run(command, data, *tail, code=2)['error']['code'] == 'invalid_arguments'
        group = run(command, data, 'watchlist', 'create-group', '--name', 'CLI')['data']['group']['id']
        run(command, data, 'watchlist', 'add', 'AAPL', '--group-id', group)
        assert 'AAPL' in next(g for g in run(command, data, 'watchlist', 'list')['data'] if g['id'] == group)['symbols']
        # Concurrent writers may serialize successfully or report an explicit conflict; never silently lose a successful edit.
        children = [(name, subprocess.Popen([*command, 'watchlist', 'create-group', '--name', name, '--data-dir', data, '--json'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)) for name in ['Concurrent1', 'Concurrent2']]
        successful = []
        for name, child in children:
            output, errors = child.communicate(timeout=15)
            assert child.returncode in (0, 4), (child.returncode, errors)
            result = json.loads(output)
            if child.returncode == 0:
                successful.append(name)
            else:
                assert result['error']['code'] == 'data_conflict'
        names = [g['name'] for g in run(command, data, 'watchlist', 'list')['data']]
        assert all(name in names for name in successful)
        spec = pathlib.Path(data) / 'strategy-input.json'
        spec.write_text(json.dumps({'name': 'put', 'symbol': 'AAPL', 'legs': [{'type': 'Put', 'side': 'Short', 'quantity': 1, 'strike': 100, 'premium': 3, 'expiry': '2027-06-18'}]}))
        run(command, data, 'strategy', 'save', '--input', str(spec))
        assert run(command, data, 'strategy', 'list')['data'][0]['name'] == 'put'
        server = Mcp(command, data)
        try:
            tools = server.request('tools/list')['result']['tools']
            server.check_live_status(False)
            assert {tool['name'] for tool in tools} == readonly
            assert all(tool['inputSchema'] == operations[tool['name']]['inputSchema'] for tool in tools)
            assert all(t['annotations']['readOnlyHint'] for t in tools)
            assert not any(t['name'] == 'save_strategy' for t in tools)
            assert all(t['inputSchema']['additionalProperties'] is False for t in tools)
            assert server.call('get_watchlists', {})['structuredContent']['data']
            assert server.call('get_saved_strategies', {})['structuredContent']['data'][0]['name'] == 'put'
            actual = server.call('calculate_option', {'right': 'call', 'spot': 100, 'strike': 100, 'years': 1, 'rate': 0.05, 'volatility': 0.2})
            assert actual['structuredContent']['data'] == price['data']
            assert server.call('calculate_indicators', {'closes': [1, 2, 3, 4], 'period': 3})['structuredContent']['data']['sma'] == [None, None, 2, 3]
            prompts=server.request('prompts/list')['result']['prompts']
            assert {p['name'] for p in prompts}=={'market_brief','option_research','strategy_review','connection_check'}
            for prompt in prompts:
                response=server.request('prompts/get',{'name':prompt['name'],'arguments':{'symbol':'NVDA'}})
                assert response['result']['messages'][0]['role']=='user'
            assert 'error' in server.request('prompts/get',{'name':'market_brief','arguments':{'symbol':'invalid/name'}})
            assert 'error' in server.request('prompts/get',{'name':'market_brief','arguments':{'symbol':123}})
            resources=server.request('resources/list')['result']['resources']
            assert len(resources)==3
            for resource in resources:
                body=server.request('resources/read',{'uri':resource['uri']})['result']['contents'][0]
                assert body['text'] and body['mimeType']
                if resource['uri']=='easysellput://capabilities':
                    allowed=json.loads(body['text']);assert allowed['readOnly'] is True
                    assert {op['name'] for op in allowed['operations']}==readonly
            assert 'error' in server.request('resources/read',{'uri':'file:///private/not-allowed'})
            assert server.call('get_settings', {'invented': True})['isError']
            assert server.call('not_a_tool', {})['isError']
            denied = server.call('create_watchlist_group', {'name': 'forbidden'})
            assert denied['isError'] and denied['structuredContent']['error']['code'] == 'permission_denied' and denied['structuredContent']['error']['recovery']
            # A cancellation notification for a completed request must not break the session.
            server.send({'jsonrpc': '2.0', 'method': 'notifications/cancelled', 'params': {'requestId': server.next_id, 'reason': 'smoke'}})
            assert server.request('ping')['result'] == {}
        finally:
            server.close()
        writer = Mcp(command, data, writes=True)
        try:
            write_tools = writer.request('tools/list')['result']['tools']
            writer.check_live_status(True)
            assert {tool['name'] for tool in write_tools} == set(operations)
            assert not writer.call('rename_watchlist_group', {'groupId': group, 'name': 'MCP'}).get('isError', False)
            # A second process writes, then the long-running MCP host must read the new snapshot.
            run(command, data, 'watchlist', 'add', 'MSFT', '--group-id', group)
            groups = writer.call('get_watchlists', {})['structuredContent']['data']
            assert 'MSFT' in next(g for g in groups if g['id'] == group)['symbols']
        finally:
            writer.close()
        assert next(g for g in run(command, data, 'watchlist', 'list')['data'] if g['id'] == group)['name'] == 'MCP'
    print('PASS: native SQLite/provider composition, catalog/discovery parity, real CLI processes, official SDK MCP stdio handshake/discovery/calls/errors/read-only/write mode/EOF, shared local data and pricing parity')


if __name__ == '__main__':
    main()
