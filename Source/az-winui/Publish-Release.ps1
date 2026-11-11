param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$build = Get-Content -LiteralPath (Join-Path $release 'release-build.json') -Raw | ConvertFrom-Json
if ($build.repository -notmatch '^https://github\.com/([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)$') { throw 'Invalid release repository.' }
$repository = $Matches[1]
if ($build.version -notmatch '^\d+\.\d+\.\d+$' -or $build.packageId -ne 'AccessibilityZoom.App') { throw 'Invalid release metadata.' }
$tag = 'v' + $build.version
$assets = @(Get-ChildItem -LiteralPath $release -File | Where-Object {
    $_.Name -like '*-Setup.exe' -or $_.Name -like '*-full.nupkg' -or $_.Name -eq 'releases.win.json' -or $_.Name -eq 'SHA256SUMS.json'
} | ForEach-Object FullName)
if ($assets.Count -ne 4) { throw 'Expected one installer, one full update package, releases.win.json and SHA256SUMS.json.' }
foreach ($entry in (Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS.json') -Raw | ConvertFrom-Json)) {
    if ([IO.Path]::GetFileName($entry.File) -ne $entry.File) { throw 'Invalid checksum path.' }
    if ((Get-FileHash -LiteralPath (Join-Path $release $entry.File) -Algorithm SHA256).Hash -ne $entry.Hash) {
        throw "Release changed after build: $($entry.File)"
    }
}
gh auth status
if ($LASTEXITCODE) { throw 'Sign in first with gh auth login.' }
$releaseArguments = @('release', 'create', $tag, '--repo', $repository, '--title', "Accessibility Zoom $($build.version)",
    '--verify-tag', '--notes-file', (Join-Path $release 'release-notes.md'))
if (!$Publish) { $releaseArguments += '--draft' }
& gh @releaseArguments @assets
if ($LASTEXITCODE) { throw 'Release creation failed. Check GitHub before retrying.' }
