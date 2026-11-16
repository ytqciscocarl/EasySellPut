# Native Windows publisher. Does not modify/replace an existing version directory.
param([string]$Runtime = '', [string]$OutputParent = '')
$ErrorActionPreference = 'Stop'
if (-not $Runtime) {
    $machineArch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $Runtime = switch ($machineArch) {
        'ARM64' { 'win-arm64' }
        'AMD64' { 'win-x64' }
        default { throw 'Cannot detect Windows architecture; pass -Runtime explicitly.' }
    }
}
if ($Runtime -notin @('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')) { throw 'Unsupported runtime identifier' }
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'tools/env.ps1')
if (-not $OutputParent) { $OutputParent = Join-Path $root 'build/out/cli' }
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$name = "EasySellPut-CLI-$version-$Runtime"
$dest = Join-Path $OutputParent $name
if ((Test-Path $dest) -or (Test-Path "$dest.zip")) { throw "Distribution already exists: $dest" }
New-Item -ItemType Directory -Force $OutputParent | Out-Null
$temp = Join-Path $OutputParent ('.publish-' + [guid]::NewGuid().ToString('N'))
try {
    dotnet publish (Join-Path $root 'src/Meili.Cli/Meili.Cli.csproj') -c Release -r $Runtime --self-contained true -m:3 -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PackAsTool=false -o $temp -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
    New-Item -ItemType Directory -Force -Path (Join-Path $temp 'skills/easysellput-local') | Out-Null
    Copy-Item (Join-Path $root 'skills/easysellput-local/SKILL.md') (Join-Path $temp 'skills/easysellput-local/SKILL.md')
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $temp 'LICENSE')
    Copy-Item (Join-Path $root 'docs/08-CLI与MCP使用说明.md') (Join-Path $temp 'CLI-MCP.md')
    Copy-Item (Join-Path $root 'docs/12-Windows与macOS的CLI和MCP.md') $temp
    $config = if ($Runtime.StartsWith('win-')) { 'docs/mcp-config.windows.example.json' } elseif ($Runtime.StartsWith('osx-')) { 'docs/mcp-config.macos.example.json' } else { 'docs/mcp-config.example.json' }
    Copy-Item (Join-Path $root $config) (Join-Path $temp 'mcp-config.example.json')
    Move-Item $temp $dest
    Compress-Archive -Path $dest -DestinationPath "$dest.zip"
    $hash = (Get-FileHash "$dest.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content "$dest.zip.sha256" -Encoding ascii
    Write-Output "$dest.zip"
} finally {
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}
