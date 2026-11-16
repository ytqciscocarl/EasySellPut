#!/usr/bin/env python3
"""Validate release workflow boundaries in CI; requires pinned PyYAML."""
import pathlib
import yaml
from policy import GATE_NAME, RID_LABELS

ROOT = pathlib.Path(__file__).resolve().parents[2]


def require(value, message):
    if not value:
        raise ValueError(message)


def check(workflows):
    prepare = workflows['update-release.yml']
    publish = workflows['publish-release.yml']
    ci = workflows['ci.yml']
    require(prepare['on']['workflow_run']['branches'] == ['main'], 'Automatic release must only follow main CI')
    require(prepare['on']['workflow_run']['workflows'] == [ci['name']], 'Release must follow the required CI workflow')
    select = prepare['jobs']['source']
    require("github.ref == 'refs/heads/main'" in select['if'] and "github.event.workflow_run.head_branch == 'main'" in select['if'], 'Source selection must reject non-main runs')
    require(prepare['jobs']['packages']['needs'] == 'source', 'Signing must depend on trusted source validation')
    require(set(prepare['jobs']['draft']['needs']) == {'source', 'packages'}, 'Draft must wait for all signed native packages')
    require(set(publish['on']) == {'workflow_dispatch'}, 'Publication must be explicitly dispatched')
    require(set(publish['jobs']) == {'publish'}, 'Unexpected publication jobs')
    matrix = prepare['jobs']['packages']['strategy']['matrix']['include']
    require({m['rid'] for m in matrix} == {'linux-x64', 'win-x64', 'win-arm64', 'osx-x64', 'osx-arm64'}, 'Release platform coverage changed')
    for name, workflow in workflows.items():
        require(workflow.get('permissions', {}).get('contents') != 'write', 'Use job-scoped release write permissions: ' + name)
        for job_name, job in workflow.get('jobs', {}).items():
            if job.get('permissions', {}).get('contents') == 'write':
                require("github.ref == 'refs/heads/main'" in job.get('if', ''), 'Write job needs explicit main restriction')
                require(job.get('environment') == 'release', 'Write job needs release environment')
    require(prepare['jobs']['packages']['environment'] == 'release', 'Signing secrets need release environment')
    require("github.ref == 'refs/heads/main'" in prepare['jobs']['packages'].get('if', ''), 'Signing must explicitly require main')
    commands = '\n'.join(step.get('run', '') for step in prepare['jobs']['packages']['steps'])
    require('dotnet publish' not in commands and 'pack-desktop.py' not in commands and 'pack-cli.' not in commands, 'Release cannot rebuild application packages')
    require('check_desktop_package.py' in commands and 'cli_mcp_smoke.py' in commands and 'stapler validate' in commands, 'Final signed packages need native execution and notarization checks')
    require(set(ci['jobs']['required']['needs']) == {'cli', 'ui', 'desktop', 'release-policy'}, 'All job families must remain required')
    require(ci['jobs']['required']['name'] == GATE_NAME, 'Aggregate check name differs from release policy')
    require(ci['jobs']['ui']['name'] == 'UI · Layout & Charts' and ci['jobs']['release-policy']['name'] == 'Release · main guard', 'Required job labels differ')
    require(ci['jobs']['cli']['name'] == '${{ matrix.label }} · CLI/MCP' and ci['jobs']['desktop']['name'] == '${{ matrix.label }} · Desktop', 'Use short platform-first job labels')
    for group in ('cli', 'desktop'):
        rows = ci['jobs'][group]['strategy']['matrix']['include']
        expected = RID_LABELS if group == 'cli' else {r: label for r, label in RID_LABELS.items() if r != 'linux-x64'}
        require({r['rid']: r['label'] for r in rows} == expected, 'Platform labels differ from release job validation')


if __name__ == '__main__':
    workflows = {p.name: yaml.load(p.read_text(encoding='utf-8-sig'), Loader=yaml.BaseLoader) for p in (ROOT / '.github/workflows').glob('*.yml')}
    check(workflows)
    print('PASS: main-only release, native package reuse, explicit publication and required policy gate')
