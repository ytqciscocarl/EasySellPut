"""Keep the shipped companion skill's guide in sync with the compiled MCP catalog."""
import pathlib
import re

START = '<!-- mcp-tool-guide:start -->'
END = '<!-- mcp-tool-guide:end -->'
SKILL = pathlib.Path(__file__).resolve().parents[2] / 'skills/easysellput-local/SKILL.md'


def validate_skill(operations, content):
    if content.count(START) != 1 or content.count(END) != 1:
        raise AssertionError('Skill needs exactly one MCP tool guide section.')
    before, section = content.split(START)
    section, after = section.split(END)
    rows = []
    for line in section.strip().splitlines():
        if line in ('| Tool | Access | When to use / constraints |', '| --- | --- | --- |'):
            continue
        match = re.fullmatch(r'\| `([a-z_]+)` \| (read|write) \| (.+) \|', line)
        if not match:
            raise AssertionError(f'Invalid skill guide row: {line}')
        name, access, guidance = match.groups()
        if len(guidance.split()) < 5 or guidance.strip().lower() in ('todo', 'tbd'):
            raise AssertionError(f'Skill tool {name} needs useful guidance.')
        rows.append((name, access))
    names = [name for name, _ in rows]
    if len(names) != len(set(names)):
        raise AssertionError('Duplicate skill tool guide entries.')
    missing, stale = set(operations) - set(names), set(names) - set(operations)
    if missing or stale:
        raise AssertionError(f'Skill coverage mismatch: missing={sorted(missing)}, stale={sorted(stale)}')
    for name, access in rows:
        expected = 'write' if operations[name]['writes'] else 'read'
        if access != expected:
            raise AssertionError(f'Skill access mismatch: {name} needs {expected}.')
    # Catch stale explicitly named tools in the workflow as well as in the table.
    for name in re.findall(r'`([a-z]+_[a-z_]+)`', before + after):
        if name not in operations and name not in {'data_conflict'}:
            raise AssertionError(f'Stale skill workflow tool reference: {name}')
    return len(rows)


def check_skill(operations):
    count = validate_skill(operations, SKILL.read_text(encoding='utf-8'))
    print(f'PASS: companion skill guides all {count} compiled MCP tools with matching access.', flush=True)
