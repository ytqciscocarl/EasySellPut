#requires -Version 5.1
<#
.Synopsis
  Build tests, the portable self-contained exe, and the per-user Inno Setup installer into dist\.
.Notes
  Run after `. C:\tyuStockApp\tools\env.ps1`. Does not launch the installer UI or the app.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not $env:DOTNET_ROOT) {
    . (Join-Path $Root "tools\env.ps1")
}

$ProductName = "EasySellPut"
$ExeFileName = "$ProductName.exe"
$PortableLabel = "$ProductName-便携版"
$SetupLabel = "$ProductName-安装程序"
# Read the version with a regex so a missing XML declaration or the Chinese metadata in the props file cannot break [xml].
$Version = ([regex]::Match((Get-Content (Join-Path $Root "Directory.Build.props") -Raw), "<Version>([^<]+)</Version>")).Groups[1].Value
if (-not $Version) { throw "Version not found in Directory.Build.props" }
# The README must show the version being packed, so a release cannot ship with a stale number.
$ReadmeVersion = ([regex]::Match((Get-Content (Join-Path $Root "README.md") -Raw), "Current version:\s*\*\*([^*\s]+)\*\*")).Groups[1].Value
if ($ReadmeVersion -ne $Version) { throw "README.md says version '$ReadmeVersion' but Directory.Build.props says '$Version'. Update README.md before packing." }
$PortablePackage = "$ProductName-$Version-windows-portable"
$SetupPackage = "$ProductName-$Version-windows-installer"

$PublishDir = Join-Path $Root "build\out\publish"
$PayloadDir = Join-Path $Root "build\out\payload"
$PortableDir = Join-Path $Root "dist\$PortablePackage"
$DistDir = Join-Path $Root "dist"
$Iscc = Join-Path $Root "tools\InnoSetup\ISCC.exe"
$AppProj = Join-Path $Root "src\Meili.App\Meili.App.csproj"
$Sln = Join-Path $Root "MeiliTerminal.slnx"

function Write-Utf8([string]$Path, [string]$Content, [bool]$Bom = $true) {
    $enc = New-Object System.Text.UTF8Encoding $Bom
    [System.IO.File]::WriteAllText($Path, $Content, $enc)
}

if (-not $SkipTests) {
    Write-Host "=== tests ==="
    dotnet test $Sln -c Release -nologo --verbosity minimal --filter "Category!=Live"
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($LASTEXITCODE)" }
}

# Single-file, self-contained, NOT compressed and NOT ReadyToRun (C6 measurements, docs\04):
#   compressed JIT 83 MB, first render ~1.29 s, working set ~340 MB;
#   uncompressed JIT 199 MB, first render ~1.17 s, working set ~200 MB (no in-memory decompressed copy);
#   ReadyToRun adds 30-100 MB and gave no reliable gain. The zip and the installer (lzma2) compress the exe anyway.
Write-Host "=== publish self-contained win-x64 (single-file, uncompressed) ==="
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PublishDir, $PayloadDir, $DistDir | Out-Null

dotnet publish $AppProj -c Release -r win-x64 --self-contained true -o $PublishDir -nologo --verbosity minimal `
    /p:PublishSingleFile=true `
    /p:EnableCompressionInSingleFile=false `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$published = Join-Path $PublishDir "MeiliTerminal.exe"
if (-not (Test-Path $published)) { throw "Published exe not found: $published" }

Get-ChildItem $PayloadDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
New-Item -ItemType Directory -Force -Path $PayloadDir | Out-Null
Copy-Item $published (Join-Path $PayloadDir $ExeFileName) -Force

# Verify user-visible file metadata contains no "tyu".
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PayloadDir $ExeFileName))
Write-Host "FileDescription=$($info.FileDescription)"
Write-Host "ProductName=$($info.ProductName)"
Write-Host "CompanyName=$($info.CompanyName)"
Write-Host "FileVersion=$($info.FileVersion)"
foreach ($label in @($info.FileDescription, $info.ProductName, $info.CompanyName, $ExeFileName)) {
    if ($label -match 'tyu') { throw "Visible metadata contains tyu: $label" }
}
if ($info.ProductName -ne $ProductName) { Write-Warning "ProductName is '$($info.ProductName)', expected '$ProductName'" }

# Portable folder: exe + portable.flag (data will be created beside the exe at first run).
if (Test-Path $PortableDir) { Remove-Item $PortableDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PortableDir | Out-Null
Copy-Item (Join-Path $PayloadDir $ExeFileName) (Join-Path $PortableDir $ExeFileName) -Force
Write-Utf8 (Join-Path $PortableDir "portable.flag") "" $false
$portableReadme = @"
这是「$ProductName」的便携版（$Version）。

使用方法：
1. 把本文件夹整份拷到任意位置（例如 U 盘或文档目录）。
2. 双击「$ExeFileName」即可运行，无需安装，也不需要管理员权限。
3. 设置、自选和提醒会保存在本文件夹里的 data\ 目录，不会写入系统安装位置。
4. 关闭主窗口会退出程序。若希望点关闭时最小化到托盘，可在设置中更改。

请勿删除同目录下的 portable.flag，否则程序会改用当前用户的本地数据目录。
"@
Write-Utf8 (Join-Path $PortableDir "使用说明.txt") $portableReadme

$portableZip = Join-Path $DistDir "$PortablePackage.zip"
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
# Zip the folder contents so extracting yields the exe and flag at the top level of the named folder.
Push-Location $DistDir
try {
    Compress-Archive -Path $PortablePackage -DestinationPath $portableZip -CompressionLevel Optimal
} finally { Pop-Location }

Write-Host "=== Inno Setup installer ==="
if (-not (Test-Path $Iscc)) { throw "ISCC.exe not found at $Iscc" }
& $Iscc "/DMyAppVersion=$Version" (Join-Path $Root "build\installer\setup.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$readme = @"
EasySellPut（版本 $Version）
================================

本目录有两种使用方式，请任选其一。都不必使用管理员权限。

1）安装包
    文件：${SetupPackage}.exe
    双击运行即可安装到当前用户目录（%LOCALAPPDATA%\Programs\EasySellPut）。
    可选择是否创建桌面快捷方式。开始菜单和“应用和功能”里显示的名称都是「$ProductName」。
    卸载也可在系统设置的应用列表中完成。
    设置和自选数据保存在 %LOCALAPPDATA%\EasySellPut，卸载时默认保留。

2）便携版（免安装）
    推荐：解压「${PortablePackage}.zip」，或直接打开「$PortablePackage」文件夹，双击「$ExeFileName」。
    没有 portable.flag 时，便携版 exe 会改用上面的当前用户数据目录。

使用提示
    关闭主窗口会退出程序。若希望点关闭时最小化到托盘，可在设置中更改。
    本软件仅作参考，不构成投资建议。行情和期权数据来自第三方公开接口，可能延迟或不准确。
    免费公开源不提供 Level 2 盘口（买卖档位）和逐笔成交，因此本软件不显示这两类数据。

公司：美丽稻谷
"@
Write-Utf8 (Join-Path $DistDir "使用说明.txt") $readme

Write-Host "=== Installers ==="
# Each edition lives under Installers\1 or \2, but every build goes into a
# package-named version folder (e.g. 1-Windows便携版\EasySellPut-0.5.0-windows-portable).
# Never delete the parent folders or older versions.
$Installers = Join-Path $Root "Installers"
$PortableOut = Join-Path $Installers (Join-Path "1-Windows便携版" $PortablePackage)
$SetupOut = Join-Path $Installers (Join-Path "2-Windows安装版" $SetupPackage)
New-Item -ItemType Directory -Force -Path $PortableOut, $SetupOut | Out-Null
# Re-packing the same version may replace that version folder only.
Get-ChildItem $PortableOut, $SetupOut -Force | Remove-Item -Recurse -Force
Copy-Item $PortableDir (Join-Path $PortableOut $PortablePackage) -Recurse
Copy-Item $portableZip (Join-Path $PortableOut "$PortablePackage.zip")
Write-Utf8 (Join-Path $PortableOut "使用说明.txt") @"
「$ProductName」Windows 便携版（$Version），免安装，不需要管理员权限。

1. 解压「${PortablePackage}.zip」，或直接使用同目录下的「$PortablePackage」文件夹。
2. 双击文件夹里的「$ExeFileName」运行。
3. 设置、自选、提醒保存在该文件夹的 data\ 目录；整个文件夹可拷到 U 盘或其他电脑继续使用。
4. 请勿删除 portable.flag，否则数据会改存到 %LOCALAPPDATA%\EasySellPut。
5. 关闭主窗口会退出程序。若希望点关闭时最小化到托盘，可在设置中更改。
6. 访问海外行情（Yahoo、CBOE 等）需要本机代理（如 Clash），程序会自动检测。

系统要求：Windows 10 2004 及以上（推荐 Windows 11），64 位。
本软件仅作参考，不构成投资建议。免费公开源不提供 Level 2 盘口和逐笔成交。
"@
Copy-Item (Join-Path $DistDir "${SetupLabel}.exe") (Join-Path $SetupOut "$SetupPackage.exe")
Write-Utf8 (Join-Path $SetupOut "使用说明.txt") @"
「$ProductName」Windows 安装版（$Version）。

1. 双击「${SetupPackage}.exe」，按提示安装。只安装到当前用户（%LOCALAPPDATA%\Programs\EasySellPut），不需要管理员权限。
2. 可选创建桌面快捷方式；开始菜单里的名称为「$ProductName」。
3. 设置和自选数据保存在 %LOCALAPPDATA%\EasySellPut，卸载时默认保留。
4. 卸载：系统设置 → 应用 → 已安装的应用 → 「$ProductName」→ 卸载。
5. 关闭主窗口会退出程序。若希望点关闭时最小化到托盘，可在设置中更改。
6. 访问海外行情（Yahoo、CBOE 等）需要本机代理（如 Clash），程序会自动检测。

系统要求：Windows 10 2004 及以上（推荐 Windows 11），64 位。
本软件仅作参考，不构成投资建议。免费公开源不提供 Level 2 盘口和逐笔成交。
"@
Write-Host "=== artifacts ==="
Get-ChildItem $DistDir, $Installers -Recurse -File | Where-Object { $_.FullName -notmatch '\\data\\' } | ForEach-Object {
    "{0,12:N0}  {1}" -f $_.Length, $_.FullName.Replace($Root + '\', '')
} | Write-Host