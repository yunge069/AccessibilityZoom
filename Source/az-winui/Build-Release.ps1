param(
    [ValidatePattern('^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$')][string]$RepositoryUrl = 'https://github.com/yunge069/AccessibilityZoom',
    [string]$Version,
    [string]$ReleaseNotes
)
$ErrorActionPreference = 'Stop'
$projectFile = Join-Path $PSScriptRoot 'AccessibilityZoom.WinUI.csproj'
if (!$Version) { $Version = ([xml](Get-Content -LiteralPath $projectFile -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a stable version such as 4.3.20.' }
if (!$ReleaseNotes) { $ReleaseNotes = Join-Path $PSScriptRoot "docs\release-notes-$Version.md" }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$toolDirectory = Join-Path $workspace '.tools\velopack'
$tool = Join-Path $toolDirectory 'vpk.exe'
if (!(Test-Path -LiteralPath $tool)) {
    dotnet tool install vpk --version 1.2.161 --tool-path $toolDirectory
    if ($LASTEXITCODE) { throw 'Could not install the pinned packaging tool.' }
}
if (!(Test-Path -LiteralPath $ReleaseNotes -PathType Leaf)) { throw 'Release notes file is missing.' }
# Unique staging prevents stale DLLs from leaking into a new package.
$staging = Join-Path $workspace ('Builds\Release-staging\' + $Version + '-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $workspace ('Builds\Releases\' + $Version)
dotnet run --project (Join-Path $PSScriptRoot 'Tests\AccessibilityZoom.Tests.csproj') -c Release
if ($LASTEXITCODE) { throw 'Regression tests failed.' }
dotnet publish $projectFile -c Release -o $staging "-p:Version=$Version" "-p:UpdateRepositoryUrl=$($RepositoryUrl.TrimEnd('/'))"
if ($LASTEXITCODE) { throw 'Publish failed.' }
foreach ($selfTest in '--self-test', '--crosshair-self-test') {
    $process = Start-Process -FilePath (Join-Path $staging 'AccessibilityZoom.exe') -ArgumentList $selfTest -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode) { throw "Packaged $selfTest failed with code $($process.ExitCode)." }
}
& $tool pack --packId AccessibilityZoom.App --packVersion $Version --packDir $staging `
    --mainExe AccessibilityZoom.exe --packTitle 'Accessibility Zoom' --channel win --runtime win-x64 `
    --icon (Join-Path $PSScriptRoot 'Assets\AppIcon.ico') --releaseNotes $ReleaseNotes `
    --shortcuts 'Desktop,StartMenuRoot' --delta None --outputDir $output
if ($LASTEXITCODE) { throw 'Installer packaging failed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packagePath = Join-Path $output "AccessibilityZoom.App-$Version-full.nupkg"
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    if (!$archive.GetEntry('lib/app/Accessibility Zoom_ExecutionStub.exe') -or
        !$archive.GetEntry('lib/app/AccessibilityZoom.exe') -or !$archive.GetEntry('lib/app/sq.version') -or
        !$archive.GetEntry('lib/app/LICENSE.md') -or !$archive.GetEntry('lib/app/NOTICE.md') -or
        !$archive.GetEntry('lib/app/LICENSE-GUIDE.md') -or !$archive.GetEntry('lib/app/Assets/ThirdParty/Reticly-LICENSE.txt')) {
        throw 'The package is missing the launcher, application manifest or required license notices.'
    }
} finally { $archive.Dispose() }
$feed = Get-Content -LiteralPath (Join-Path $output 'releases.win.json') -Raw | ConvertFrom-Json
$asset = @($feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version })
if ($asset.Count -ne 1 -or $asset[0].PackageId -ne 'AccessibilityZoom.App' -or
    $asset[0].SHA256 -ne (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash) {
    throw 'Release feed identity, version or checksum does not match the package.'
}
Copy-Item -LiteralPath $ReleaseNotes -Destination (Join-Path $output 'release-notes.md')
@{ version = $Version; repository = $RepositoryUrl.TrimEnd('/'); packageId = 'AccessibilityZoom.App'; channel = 'win' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release-build.json') -Encoding utf8
Get-ChildItem -LiteralPath $output -File | Where-Object {
    $_.Name -like '*-Setup.exe' -or $_.Name -like '*-full.nupkg' -or $_.Name -eq 'releases.win.json'
} |
    Get-FileHash -Algorithm SHA256 | Select-Object @{Name='File';Expression={Split-Path $_.Path -Leaf}}, Hash |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.json') -Encoding utf8
Write-Host "Release ready for review: $output"
Write-Host 'This command has not published or installed anything.'
