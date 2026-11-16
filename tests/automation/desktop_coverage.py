"""Keep desktop entry-point review separate from registered-tool execution coverage."""
import pathlib
import re

ROOT=pathlib.Path(__file__).resolve().parents[2]
PATTERN=r'\[RelayCommand[^\]]*\]\s*(?:public|private|internal)\s+(?:async\s+)?[\w<>?]+\s+(\w+)\s*\('

def validate_desktop(operations,manifest,sources):
    rows=manifest['viewModels']
    paths=[row['path'] for row in rows]
    if len(paths)!=len(set(paths)) or set(paths)!=set(sources):
        raise AssertionError('Desktop view-model inventory differs from source files; review new/removed features.')
    for row in rows:
        if not row.get('scope'): raise AssertionError('Desktop scope explanation required: '+row['path'])
        commands=set(re.findall(PATTERN,sources[row['path']]))
        if commands!=set(row['commands']): raise AssertionError('Desktop command inventory differs: '+row['path'])
        referenced=list(row['operations'])
        for name,entry in row['commands'].items():
            if bool(entry.get('operations')) == bool(entry.get('uiOnly')):
                raise AssertionError('Each command needs business tools or a specific UI-only reason: '+name)
            referenced.extend(entry.get('operations',[]))
        missing=set(referenced)-set(operations)
        if missing:raise AssertionError('Desktop references missing MCP operations: '+str(sorted(missing)))
    return len(paths),sum(len(row['commands']) for row in rows)

def check_desktop(operations):
    import json
    manifest=json.loads((ROOT/'tests/automation/desktop_capabilities.json').read_text(encoding='utf-8'))
    sources={str(p.relative_to(ROOT)).replace('\\','/'):p.read_text(encoding='utf-8-sig') for p in (ROOT/'src/Meili.Shared/ViewModels').glob('*.cs')}
    files,commands=validate_desktop(operations,manifest,sources)
    print(f'PASS: desktop inventory reviewed: {files} view-model files, {commands} commands; UI exclusions are explicit.')
