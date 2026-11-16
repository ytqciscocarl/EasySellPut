#!/usr/bin/env python3
"""Require an executable CLI + MCP scenario for every compiled catalog operation."""
import argparse
import json
import math
import pathlib
import tempfile

from cli_mcp_smoke import Mcp, run
from desktop_coverage import check_desktop
from skill_coverage import check_skill


def validate_cases(operations, cases):
    names = [case['operation'] for case in cases]
    if len(set(names)) != len(names):
        raise AssertionError('Duplicate capability cases; use exactly one scenario per operation.')
    missing = set(operations) - set(names)
    stale = set(names) - set(operations)
    if missing or stale:
        raise AssertionError(f'Capability coverage mismatch: missing cases={sorted(missing)}, stale cases={sorted(stale)}')
    for case in cases:
        if not isinstance(case.get('arguments'), dict) or not case.get('expect') or not case.get('compare') or not any(path.startswith('/data') for path in case['expect']):
            raise AssertionError(f"{case['operation']} needs arguments, meaningful expectations and CLI/MCP comparison paths.")
        for path, rule in case['expect'].items():
            if not path.startswith('/') or not isinstance(rule, dict) or len(set(rule) & {'equals', 'length', 'approx'}) != 1:
                raise AssertionError(f'Invalid expectation: {path}: {rule}')


def pointer(body, path):
    for part in path.split('/')[1:]:
        key = part.replace('~1', '/').replace('~0', '~')
        body = body[int(key)] if isinstance(body, list) else body[key]
    return body


def resolve(value, context):
    if isinstance(value, str) and value.startswith('${') and value.endswith('}'):
        return context[value[2:-1]]
    if isinstance(value, list):
        return [resolve(v, context) for v in value]
    if isinstance(value, dict):
        return {key: resolve(v, context) for key, v in value.items()}
    return value


def verify(case, body, context):
    if body.get('success') is not True:
        raise AssertionError((case['operation'], body))
    for path, rule in case['expect'].items():
        rule = resolve(rule, context)
        actual = pointer(body, path)
        if 'equals' in rule:
            valid = actual == rule['equals']
        elif 'length' in rule:
            valid = len(actual) == rule['length']
        else:
            valid = isinstance(actual, (int, float)) and math.isclose(actual, rule['approx'], rel_tol=0, abs_tol=rule.get('tolerance', 1e-9))
        if not valid:
            raise AssertionError((case['operation'], path, actual, rule))


def check(command, manifest, report):
    results = []
    with tempfile.TemporaryDirectory(prefix='easysellput-coverage-') as root:
        cli_dir, mcp_dir = str(pathlib.Path(root) / 'cli'), str(pathlib.Path(root) / 'mcp')
        catalog = run(command, cli_dir, '--help')['data']
        operations = {op['name']: op for op in catalog['operations']}
        if not operations or len(operations) != len(catalog['operations']):
            raise AssertionError('Compiled operation catalog must be nonempty with unique names.')
        check_desktop(operations)
        check_skill(operations)
        cases = manifest['cases']
        validate_cases(operations, cases)
        reader = Mcp(command, mcp_dir)
        try:
            readonly = reader.request('tools/list')['result']['tools']
            expected = {name for name, op in operations.items() if not op['writes']}
            if {tool['name'] for tool in readonly} != expected:
                raise AssertionError('Read-only MCP discovery differs from the compiled catalog.')
            # Every writer must be rejected at invocation even when a client bypasses discovery.
            for op in operations.values():
                if not op['writes']:
                    continue
                schema = op['inputSchema']
                arguments = {}
                for name in schema['required']:
                    field = schema['properties'][name]
                    arguments[name] = field.get('enum', ['AAPL'])[0] if field['type'] == 'string' else {'number': 1, 'integer': 1, 'boolean': True, 'array': [{'type': 'Stock', 'side': 'Long', 'quantity': 1, 'premium': 1}]}[field['type']]
                denied = reader.call(op['name'], arguments)
                if not denied.get('isError') or denied['structuredContent']['error']['code'] != 'permission_denied':
                    raise AssertionError(('Read-only authorization bypass', op['name'], denied))
        finally:
            reader.close()
        writer = Mcp(command, mcp_dir, writes=True)
        cli_context, mcp_context = {}, {}
        try:
            discovered = writer.request('tools/list')['result']['tools']
            if {tool['name'] for tool in discovered} != set(operations):
                raise AssertionError('Writable MCP discovery differs from the compiled catalog.')
            for tool in discovered:
                op = operations[tool['name']]
                if tool['inputSchema'] != op['inputSchema'] or tool['annotations']['readOnlyHint'] != (not op['writes']):
                    raise AssertionError(('Tool schema/permissions drift', tool['name']))
            for case in cases:
                cli_arguments = resolve(case['arguments'], cli_context)
                spec = pathlib.Path(root) / 'input.json'
                spec.write_text(json.dumps(cli_arguments), encoding='utf-8')
                cli = run(command, cli_dir, case['operation'], '--input', str(spec))
                verify(case, cli, cli_context)
                result = writer.call(case['operation'], resolve(case['arguments'], mcp_context))
                if result.get('isError'):
                    raise AssertionError((case['operation'], result))
                mcp = result['structuredContent']
                verify(case, mcp, mcp_context)
                for path in case['compare']:
                    if pointer(cli, path) != pointer(mcp, path):
                        raise AssertionError(('CLI/MCP result drift', case['operation'], path))
                for name, path in case.get('capture', {}).items():
                    cli_context[name], mcp_context[name] = pointer(cli, path), pointer(mcp, path)
                results.append({'operation': case['operation'], 'cli': 'passed', 'mcp': 'passed', 'parity': 'passed'})
                report.write_text(json.dumps({'operations': results}, indent=2), encoding='utf-8')
                print('PASS:', case['operation'], flush=True)
        finally:
            writer.close()
    print(f'PASS: all {len(results)} catalog operations executed through both CLI and MCP; permissions and schemas match.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dll', default='tests/Meili.Automation.Host/bin/Release/net10.0/Meili.Automation.Host.dll')
    parser.add_argument('--cases', default='tests/automation/capability_cases.json')
    parser.add_argument('--report', default='build/out/ci/capability-coverage.json')
    args = parser.parse_args()
    report = pathlib.Path(args.report)
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text('{"operations": []}', encoding='utf-8')
    manifest = json.loads(pathlib.Path(args.cases).read_text(encoding='utf-8'))
    if manifest['schemaVersion'] != 1:
        raise AssertionError('Unsupported capability case schema version.')
    check(['dotnet', str(pathlib.Path(args.dll).resolve())], manifest, report)


if __name__ == '__main__':
    main()
