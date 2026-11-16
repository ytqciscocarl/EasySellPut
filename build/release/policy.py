#!/usr/bin/env python3
"""Main-only release provenance checks. No credentials or publishing side effects."""
import argparse
import json
import os
import pathlib
import re
import subprocess

CLI_RIDS = ('linux-x64', 'win-x64', 'win-arm64', 'osx-x64', 'osx-arm64')
DESKTOP_RUNNERS = ('windows-latest', 'windows-11-arm', 'macos-15-intel', 'macos-26')
ARTIFACTS = tuple('Desktop-' + r for r in DESKTOP_RUNNERS) + tuple('CLI-MCP-' + r for r in CLI_RIDS)
RID_LABELS = {
    'linux-x64': 'Linux x64', 'win-x64': 'Windows x64', 'win-arm64': 'Windows ARM64',
    'osx-x64': 'Mac Intel', 'osx-arm64': 'Mac Apple Silicon',
}
GATE_NAME = 'CI · Required checks'
REQUIRED_JOBS = {GATE_NAME, 'UI · Layout & Charts', 'Release · main guard'} | {
    label + ' · CLI/MCP' for label in RID_LABELS.values()
} | {RID_LABELS[r] + ' · Desktop' for r in CLI_RIDS if r != 'linux-x64'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_run(run, repository, expected_sha):
    require(re.fullmatch(r'[0-9a-f]{40}', expected_sha) is not None, 'Invalid main SHA')
    require(run.get('status') == 'completed' and run.get('conclusion') == 'success', 'CI must have completed successfully')
    require(run.get('event') == 'push' and run.get('head_branch') == 'main', 'Release requires a main push CI run')
    require(run.get('path') == '.github/workflows/ci.yml', 'Unexpected CI workflow')
    require(run.get('repository', {}).get('full_name') == repository, 'Wrong repository')
    require(run.get('head_repository', {}).get('full_name') == repository, 'Fork artifacts are not releasable')
    require(run.get('head_sha') == expected_sha, 'CI must match the current main commit')


def validate_jobs(jobs):
    names = [j.get('name') for j in jobs]
    require(REQUIRED_JOBS.issubset(names), 'Missing required platform or aggregate checks')
    require(len(names) == len(set(names)), 'Ambiguous CI job names')
    require(all(j.get('status') == 'completed' and j.get('conclusion') == 'success' for j in jobs), 'Every CI job must pass')


def validate_artifacts(artifacts):
    selected = {}
    for name in ARTIFACTS:
        matches = [a for a in artifacts if a.get('name') == name]
        require(len(matches) == 1, 'Exactly one artifact required: ' + name)
        artifact = matches[0]
        require(not artifact.get('expired') and artifact.get('size_in_bytes', 0) > 0, 'Expired or empty artifact: ' + name)
        require(isinstance(artifact.get('id'), int) and artifact['id'] > 0, 'Invalid artifact identity')
        selected[name] = {k: artifact.get(k) for k in ('id', 'name', 'digest', 'size_in_bytes')}
    return selected


def gh_json(endpoint):
    return json.loads(subprocess.check_output(['gh', 'api', endpoint], text=True))


def resolve_tag(repository, tag):
    require(re.fullmatch(r'v\d+\.\d+\.\d+', tag) is not None, 'Invalid release tag')
    obj = gh_json(f'repos/{repository}/git/ref/tags/{tag}')['object']
    for _ in range(5):
        require(re.fullmatch('[0-9a-f]{40}', obj.get('sha', '')) is not None, 'Invalid tag object')
        if obj.get('type') == 'commit':
            return obj['sha']
        require(obj.get('type') == 'tag', 'Tag does not resolve to a commit')
        obj = gh_json(f'repos/{repository}/git/tags/' + obj['sha'])['object']
    raise ValueError('Too many nested annotated tags')


def current_main(repository):
    return gh_json('repos/' + repository + '/git/ref/heads/main')['object']['sha']


def select(repository, run_id):
    require(re.fullmatch(r'[1-9][0-9]{0,19}', str(run_id)) is not None, 'Invalid CI run ID')
    base = f'repos/{repository}/actions/runs/{run_id}'
    run = gh_json(base)
    sha = current_main(repository)
    validate_run(run, repository, sha)
    jobs = gh_json(base + '/jobs?per_page=100&filter=latest')
    require(jobs['total_count'] == len(jobs['jobs']), 'Truncated CI job list')
    validate_jobs(jobs['jobs'])
    artifacts = gh_json(base + '/artifacts?per_page=100')
    require(artifacts['total_count'] == len(artifacts['artifacts']), 'Truncated artifact list')
    return dict(schemaVersion=1, repository=repository, sourceCommit=sha, ciRunId=int(run_id),
                artifacts=validate_artifacts(artifacts['artifacts']))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    require(os.environ.get('GITHUB_REF') == 'refs/heads/main', 'Release workflow must run on main')
    plan = select(os.environ['GITHUB_REPOSITORY'], args.run_id)
    args.output.write_text(json.dumps(plan, indent=2) + '\n', encoding='utf-8')
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as f:
            f.write('sha=' + plan['sourceCommit'] + '\nrun_id=' + str(plan['ciRunId']) + '\n')
