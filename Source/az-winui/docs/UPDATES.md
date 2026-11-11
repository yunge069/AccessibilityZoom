# Managed installation and GitHub updates

## Layout and retention

Package ID: `AccessibilityZoom.App`. Per-user application root:
`%LOCALAPPDATA%\AccessibilityZoom.App`. Startup uses the root execution stub
`Accessibility Zoom.exe` (named after packTitle), which launches
`current\AccessibilityZoom.exe`.
Velopack replaces `current` when an update is applied; it manages staging and
package cleanup. A failed replacement may require reinstalling Setup.exe; the
separate settings directory and ZIP backups remain available. Temporary/package cache
files are not additional active installations and may remain after a failure.

All existing user data stays in `%LOCALAPPDATA%\AccessibilityZoom`, outside the
installer's root. No settings migration is needed. An uninstall therefore also
preserves user data. Never change the package ID to `AccessibilityZoom`, which
would overlap that data directory.

Before applying a confirmed update, the UI flushes pending settings/crosshair
edits and creates `UpdateBackups\settings-<timestamp>-<id>.zip` under the data
directory. Backup/save failure prevents installation. Backups include the whole
existing data tree except UpdateBackups; linked files/directories cause a clear
failure rather than being followed. Recovery backups are retained deliberately.
To restore, exit the app and extract the chosen ZIP over the data directory.

## Behavior

- Only installer-managed builds check for updates; loose development builds
  explain that Setup.exe is needed.
- Public HTTPS GitHub repository is embedded at build time. No GitHub token is
  shipped to users. Draft/prerelease releases and downgrades are excluded.
- One automatic check 10 seconds after startup, if enabled; manual check in
  the Updates card or tray menu. There are no recurring checks during a session.
  An available update appears in an in-app banner. Turning the startup switch
  off still allows manual checks; turning it on takes effect at the next launch.
- The confirmation dialog defaults to Later. Nothing downloads before consent.
- Velopack verifies downloaded package checksums. Apply waits for normal app
  shutdown, restoring zoom/color/cursor/audio and then restarting the new build.
- Auto-apply-on-startup is disabled: a downloaded package left after a failed
  backup or app exit cannot install without a new confirmation.
- Install/update hooks migrate an existing startup opt-in to the stable launcher.
  Fresh installs remain opt-out. Uninstall removes only its own startup entry.
- From 4.3.22, managed builds also recognize the historical
  `AccessibilityZoom Admin Prompt` sign-in task for the current user. An enabled
  task counts as an existing opt-in, even without a Run entry. Migration writes
  the stable launcher first, then disables the task; it does not delete the task.
  Turning startup off disables both registrations. Only the known Windows
  PowerShell command targeting `AccessibilityZoom.exe --start-minimized` with
  `RunAs` is recognized. Edited tasks, other users and unrelated tasks are left
  alone. Task-access failures are reported in the startup setting warning.
- From 4.3.20 onward, launching an older loose build does not silently retarget
  startup to a lower version. Older binaries cannot be retroactively patched;
  remove old portable copies after the first installation.

## Release repository

The intended repository now includes public source for inspection as well as
compiled releases. Maintain the repository-root `README.md` as the project page and
use the workspace `.gitignore` to exclude builds, caches, credentials and editor
files. Project-original software uses the unmodified PolyForm Noncommercial
License 1.0.0 in the workspace `LICENSE.md`; `NOTICE.md` defines scope and
preserves third-party licensing. `LICENSE-GUIDE.md` explains permitted purposes
and the license's express organization exceptions. All three files are included
in published builds. The configured public repository is
https://github.com/yunge069/AccessibilityZoom.

Associate each published release with the exact source commit and retain its
build instructions. Checksums verify asset integrity, not correspondence to
published source. Independently reproducible builds have not been verified.

Build locally with the installed .NET 8 SDK:

```powershell
.\Build-Release.ps1
```

Release builds omit embedded debug symbols, which can contain local builder
paths and usernames. The packaging script rejects embedded symbols and PDB
files for the project assembly before packaging.

This runs regression tests, publishes to a fresh staging directory, runs both
packaged headless self-tests, and packages using pinned Velopack/vpk 1.2.161.
Review `Builds\Releases\<version>`. It does not install or upload anything.
For later releases, increment the project Version and pass `-ReleaseNotes` with
the matching notes. `-Version` can override the build version explicitly.

```powershell
# Creates a draft release with all required assets, using GitHub CLI login.
.\Publish-Release.ps1 -ReleaseDirectory ..\..\Builds\Releases\4.3.20
# Use -Publish only when the release is approved for public availability.
```

Commit the source, create the matching `v<version>` tag and push both before
uploading. The publishing script requires the tag to exist remotely.
The upload includes Setup.exe, full.nupkg, releases.win.json, SHA256SUMS.json.
Publish the reviewed draft in GitHub. Uploading only the EXE is insufficient for
in-app updates. The uploader does not upload or tag source code; source commits
and matching release tags must be managed separately for the public-source plan.
Code signing is not configured; do not claim the installer is signed.

## First installation and old copies

Exit the running portable app through its tray menu. Run the new Setup.exe,
check your profiles/hotkeys/crosshair and turn Start with Windows on if desired.
Use the installed shortcut from this point onward.

`Cleanup-LegacyBuilds.ps1` previews old numbered folders/ZIPs directly under this
workspace's Builds directory. It refuses to run before the managed installation
exists, rejects links, verifies containment, and refuses running old builds.
After verifying the installed app, `-Apply` removes only those listed older
builds. Source backups, performance captures, tools, settings and unrelated files
are not candidates. Copies elsewhere require separate identification; no full
PC scan or blanket deletion is performed.

## Validation

Unit/regression checks cover consent/Later, cancellation, download and backup
failures, backup contents, version ordering, startup opt-in and uninstall
ownership, in addition to the existing application checks. Packaging validates
the entry-point integration and both published headless self-tests.

Manual acceptance is still required for the actual Setup UI, login startup,
and two successive releases through the update/restart flow. In particular,
verify starting minimized, Settings persistence after restart, cancellation,
offline checks, and the old program files being replaced. Do not describe an
end-to-end GitHub update as tested until two releases have exercised it.

References: [Velopack Windows layout](https://docs.velopack.io/packaging/operating-systems/windows),
[GitHub source](https://docs.velopack.io/integrating/update-sources),
[preserving files](https://docs.velopack.io/integrating/preserved-files).
