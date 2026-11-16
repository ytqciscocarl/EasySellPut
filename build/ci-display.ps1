# Configure only the disposable Actions desktop, never a developer's display.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This helper is restricted to the Windows GitHub Actions runner.'
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CiDisplay {
    // DEVMODEW is 220 bytes; these documented fields have fixed Unicode offsets.
    [StructLayout(LayoutKind.Explicit, Size=220)]
    public struct Mode {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(172)] public uint Width;
        [FieldOffset(176)] public uint Height;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string device, int mode, ref Mode value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int ChangeDisplaySettings(ref Mode mode, uint flags);
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
}
'@
$current = New-Object CiDisplay+Mode
$current.Size = 220
# Hosted runner sessions may expose no enumerated display device. A minimal
# DEVMODE request is still valid; GetSystemMetrics records the actual pixels.
$null = [CiDisplay]::EnumDisplaySettings($null, -1, [ref]$current)
$current.Width = 1920
$current.Height = 1080
$current.Fields = 0x180000 # DM_PELSWIDTH | DM_PELSHEIGHT
$result = [CiDisplay]::ChangeDisplaySettings([ref]$current, 0)
$width = [CiDisplay]::GetSystemMetrics(0)
$height = [CiDisplay]::GetSystemMetrics(1)
Write-Host "Native desktop: ${width}x${height}; display result: $result"
if ($width -ne 1920 -or $height -ne 1080) {
    Write-Warning 'Runner did not accept Full HD. Keep actual dimensions in evidence; never upscale.'
}
