## Change and user behavior

Describe the problem and resulting behavior.

## Entry-point coverage

- Desktop: supported behavior, or why this change is inapplicable.
- CLI: operation/command and test case, or a specific reason it is inapplicable.
- MCP: tool/schema/permission behavior and test case, or a specific reason it is inapplicable.

For every added/changed shared business operation, update `OperationCatalog` and
`tests/automation/capability_cases.json`. Each operation must have meaningful expected
results and CLI/MCP parity checks. Do not change a tool count to bypass coverage.

## Validation

Record commands and outcomes. Keep offline fixture tests, live-provider tests,
native execution and cross-compilation distinct. `CI · Required checks` must pass.
