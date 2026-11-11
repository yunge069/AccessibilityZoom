[CmdletBinding(SupportsShouldProcess)]
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$builds = (Resolve-Path -LiteralPath (Join-Path $workspace 'Builds')).Path
$installed = Join-Path $env:LOCALAPPDATA 'AccessibilityZoom.App\current\AccessibilityZoom.exe'
$manifest = Join-Path (Split-Path $installed) 'sq.version'
if (!(Test-Path -LiteralPath $installed) -or !(Test-Path -LiteralPath $manifest)) {
    throw 'Install and verify the managed version first. No old builds were removed.'
}
if ((Get-Content -LiteralPath $manifest -Raw) -notmatch '<id>AccessibilityZoom\.App</id>') {
    throw 'The installed package identity could not be verified.'
}
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($installed)
$installedVersion = [version]::new($fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart)
$targets = @(Get-ChildItem -LiteralPath $builds -Force | Where-Object {
    $_.Name -match '^AccessibilityZoom-(\d+\.\d+(?:\.\d+)?)(?:-win-x64\.zip)?$' -and
    [version]$Matches[1] -lt $installedVersion
})
# Validate every path before any removal. Do not traverse links or leave Builds.
if ((Get-Item -LiteralPath $builds).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Builds cannot be a linked directory.' }
foreach ($target in $targets) {
    $resolved = (Resolve-Path -LiteralPath $target.FullName).Path
    if (![string]::Equals((Split-Path $resolved -Parent), $builds, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a path outside the expected build directory: $resolved"
    }
    if ($target.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked path: $resolved" }
    if ($target.PSIsContainer) {
        $links = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object {
            $_.Attributes -band [IO.FileAttributes]::ReparsePoint
        })
        if ($links.Count) { throw "Refusing a build containing linked files or folders: $resolved" }
        if (!(Test-Path -LiteralPath (Join-Path $resolved 'AccessibilityZoom.exe'))) { throw "Not an application build: $resolved" }
    }
}
foreach ($process in @(Get-Process AccessibilityZoom -ErrorAction SilentlyContinue)) {
    foreach ($target in $targets) {
        if ($process.Path -and $process.Path.StartsWith($target.FullName.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Exit the older running application from its tray menu before cleanup.'
        }
    }
}
Write-Host "Installed version: $installedVersion"
$targets | Select-Object FullName
if (!$Apply) {
    Write-Host 'Preview only. After checking the installed app, repeat with -Apply to remove these old extracted builds and ZIPs.'
    return
}
foreach ($target in $targets) {
    if ($PSCmdlet.ShouldProcess($target.FullName, 'Remove old Accessibility Zoom build')) {
        Remove-Item -LiteralPath $target.FullName -Recurse -Force
    }
}
Write-Host 'Legacy build cleanup completed. User settings and source backups were preserved.'
