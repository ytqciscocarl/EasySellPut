#!/usr/bin/env python3
"""Opt-in real-provider acceptance through production CLI and stdio MCP, no fixtures."""
import argparse
import datetime as dt
import json
import math
import os
import pathlib
import subprocess
import tempfile
import time

from cli_mcp_smoke import Mcp


def instant(value):
    return dt.datetime.fromisoformat(value.replace('Z', '+00:00'))


def fresh_sourced(data, market_time=True):
    assert data.get('source') and data.get('fetchedAt'), 'Missing provenance or download time'
    age = (dt.datetime.now(dt.timezone.utc) - instant(data['fetchedAt'])).total_seconds()
    assert -60 <= age <= 600, f'Unexpected download age {age}s'
    if market_time and data.get('asOf'):
        market_age = (dt.datetime.now(dt.timezone.utc) - instant(data['asOf'])).total_seconds()
        assert -300 <= market_age <= 4 * 86400, f'Stale/future market snapshot ({market_age}s)'


def verify(operation, data):
    if operation == 'get_quotes':
        assert not data['missingSymbols'], data['missingSymbols']
        assert {q['symbol'] for q in data['quotes']} == {'AAPL', 'MSFT', 'SPY'}
        for quote in data['quotes']:
            assert math.isfinite(quote['price']) and quote['price'] > 0
            assert quote.get('asOf'), 'A release quote check requires a real market timestamp'
            fresh_sourced(quote)
        return [{'symbol': q['symbol'], 'price': q['price'], 'source': q['source'], 'asOf': q['asOf'], 'fetchedAt': q['fetchedAt'], 'isRealtime': q['isRealtime'], 'delayMinutes': q['delayMinutes']} for q in data['quotes']]
    if operation == 'get_bars':
        fresh_sourced(data)
        assert not data['offlineCache'], 'A cached offline response does not validate live access'
        bars = data['page']['items']
        assert len(bars) >= 100
        times = [instant(bar['time']) for bar in bars]
        assert times == sorted(set(times)), 'Bars must be sorted with unique timestamps'
        assert all(math.isfinite(bar['close']) and bar['close'] > 0 for bar in bars)
        assert (dt.datetime.now(dt.timezone.utc) - times[-1]).total_seconds() < 4 * 86400, 'Latest daily bar is stale'
        for bar in bars:
            if not bar['incomplete']:
                assert all(bar[k] is not None for k in ['open', 'high', 'low'])
                assert bar['low'] <= min(bar['open'], bar['close']) + abs(bar['close']) * 1e-9
                assert bar['high'] >= max(bar['open'], bar['close']) - abs(bar['close']) * 1e-9
        return {'source': data['source'], 'count': len(bars), 'first': bars[0]['time'], 'last': bars[-1]['time'], 'adjustment': data['adjustment'], 'offlineCache': data['offlineCache']}
    if operation == 'get_expirations':
        assert data and any(dt.date.fromisoformat(x) >= dt.datetime.now(dt.timezone.utc).date() for x in data)
        return {'count': len(data), 'first': data[0]}
    if operation == 'get_option_chain':
        fresh_sourced(data)
        contracts = data['page']['items']
        assert data['underlying'] == 'AAPL' and contracts
        assert all(c['underlying'] == 'AAPL' and c['strike'] > 0 and c['contractSymbol'] and c['expiration'] == data['expiration'] for c in contracts)
        assert any(c.get('bid') is not None or c.get('ask') is not None or c.get('last') is not None for c in contracts)
        return {'source': data['source'], 'expiry': data['expiration'], 'totalContracts': data['page']['total'], 'returned': len(contracts), 'asOf': data['asOf'], 'delayMinutes': data['delayMinutes']}
    if operation == 'get_fundamentals':
        fresh_sourced(data, market_time=False)
        assert data['symbol'] == 'AAPL' and data.get('name')
        return {'source': data['source'], 'name': data['name'], 'marketCap': data.get('marketCap')}
    if operation == 'get_financials':
        fresh_sourced(data, market_time=False)
        assert data['symbol'] == 'AAPL' and data['lines']
        return {'source': data['source'], 'lines': len(data['lines'])}
    if operation == 'get_news':
        assert data and all(n['title'] and n['source'] and n['url'].startswith('https://') for n in data)
        return {'count': len(data), 'sources': sorted({n['source'] for n in data}), 'latestPublished': data[0].get('published')}
    if operation == 'search_symbols':
        assert any(x['symbol'] == 'AAPL' for x in data)
        return {'count': len(data), 'containsAAPL': True}
    raise AssertionError('No live validator for '+operation)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dll', default='src/Meili.Cli/bin/Release/net10.0/easysellput.dll')
    parser.add_argument('--executable')
    parser.add_argument('--timeout', type=int, default=60)
    parser.add_argument('--output', default='build/out/live-readiness/acceptance')
    args = parser.parse_args()
    if not 1 <= args.timeout <= 600:
        parser.error('--timeout must be between 1 and 600 seconds.')
    if os.environ.get('MEILI_LIVE') != '1':
        parser.error('Real network calls require an explicit MEILI_LIVE=1.')
    output = pathlib.Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    command = [str(pathlib.Path(args.executable).resolve())] if args.executable else ['dotnet', str(pathlib.Path(args.dll).resolve())]
    results = []
    for transport in ['cli', 'mcp']:
        with tempfile.TemporaryDirectory(prefix=f'easysellput-live-{transport}-') as data:
            server = Mcp([*command, '--timeout', str(args.timeout)], data, response_timeout=args.timeout + 15) if transport == 'mcp' else None
            expiry = None
            try:
                cases = [
                    ('get_quotes', {'symbols': ['AAPL', 'MSFT', 'SPY']}),
                    ('get_bars', {'symbol': 'AAPL', 'interval': '1d', 'limit': 500}),
                    ('get_expirations', {'symbol': 'AAPL'}),
                    ('get_option_chain', {'symbol': 'AAPL', 'limit': 50}),
                    ('get_fundamentals', {'symbol': 'AAPL'}),
                    ('get_financials', {'symbol': 'AAPL'}),
                    ('get_news', {'symbol': 'AAPL', 'limit': 5}),
                    ('search_symbols', {'query': 'apple', 'limit': 10}),
                ]
                for operation, arguments in cases:
                    if operation == 'get_option_chain' and expiry:
                        arguments['expiry'] = expiry
                    start = time.monotonic()
                    record = {'transport': transport, 'operation': operation, 'arguments': arguments}
                    try:
                        def invoke(request_arguments):
                            if server:
                                response = server.call(operation, request_arguments)
                                assert not response.get('isError'), response['structuredContent']
                                return response['structuredContent']
                            spec = output / 'request.json'
                            spec.write_text(json.dumps(request_arguments), encoding='utf-8')
                            child = subprocess.run([*command, operation, '--input', str(spec), '--json', '--data-dir', data, '--timeout', str(args.timeout)], capture_output=True, text=True, timeout=args.timeout + 15)
                            response = json.loads(child.stdout)
                            assert child.returncode == 0, response
                            return response
                        body = invoke(arguments)
                        (output / f'{transport}-{operation}.json').write_text(json.dumps(body, indent=2), encoding='utf-8')
                        assert body['success'], body
                        record['summary'] = verify(operation, body['data'])
                        if operation == 'get_bars' and body['data']['page']['nextOffset'] is not None:
                            latest_arguments = {**arguments, 'offset': max(0, body['data']['page']['total'] - 100), 'limit': 100}
                            latest = invoke(latest_arguments)
                            latest_summary = verify(operation, latest['data'])
                            assert latest['data']['page']['nextOffset'] is None
                            (output / f'{transport}-get_bars-latest.json').write_text(json.dumps(latest, indent=2), encoding='utf-8')
                            record['summary']['latestPage'] = latest_summary

                        if operation == 'get_expirations':
                            minimum = dt.datetime.now(dt.timezone.utc).date() + dt.timedelta(days=7)
                            expiry = next((x for x in body['data'] if dt.date.fromisoformat(x) >= minimum), body['data'][0])
                        record['status'] = 'passed'
                    except Exception as error:
                        record['status'] = 'failed'
                        record['error'] = str(error)[:1000]
                    record['seconds'] = round(time.monotonic() - start, 2)
                    results.append(record)
                    (output / 'report.json').write_text(json.dumps({'testedAt': dt.datetime.now(dt.timezone.utc).isoformat(), 'results': results}, indent=2), encoding='utf-8')
                    print(json.dumps(record), flush=True)
            finally:
                if server:
                    server.close()
    failed = [r for r in results if r['status'] != 'passed']
    print(f"Live acceptance: {len(results)-len(failed)} passed, {len(failed)} failed; evidence: {output}")
    raise SystemExit(1 if failed else 0)


if __name__ == '__main__':
    main()
