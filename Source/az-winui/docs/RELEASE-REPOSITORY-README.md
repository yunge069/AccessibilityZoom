# Accessibility Zoom for Windows

Accessibility Zoom is a free Windows desktop tool for zoom, monitor color presets, crosshairs and application volume shortcuts. This repository contains reviewable source code, build instructions, downloads and updates.

## Source and verification

The current application source is in `Source/az-winui`. See
`Source/az-winui/BUILDING.md` for build instructions and
`Source/az-winui/docs/UPDATES.md` for packaging and update behavior.
The AMD bridge's native source is under `Source/az-winui/native`.

For published releases, the release tag should identify the exact source commit
used to build the installer. Release assets include SHA-256 checksums. Public
source and checksums alone do not prove that an executable was built from that
source; independently reproducible builds have not yet been established.

## Install

Open **Releases**, choose the latest stable release and download the file ending in **-Setup.exe**. Exit any previously running version from its system tray menu, then run the installer. Windows 10/11 on x64 is required. Administrator rights are not required.

## Updates

The installed app checks GitHub at startup and every six hours. You can also use **Updates → Check for updates**. New versions are downloaded and installed only after you choose **Update and restart**. **Later** keeps your current version running. Automatic checks can be switched off.

**Start with Windows** uses a permanent launcher that opens the currently installed version. An update replaces the installed program files. Previously extracted portable folders are separate from the installation.

## Your settings

Settings, hotkeys, color profiles and crosshair designs stay in `%LOCALAPPDATA%\AccessibilityZoom`. The program itself installs in `%LOCALAPPDATA%\AccessibilityZoom.App`. Before an in-app update, a ZIP backup is saved in `%LOCALAPPDATA%\AccessibilityZoom\UpdateBackups`. These settings are retained when updating or uninstalling the program.

## Files in a release

- `*-Setup.exe`: installer for users.
- `*-full.nupkg` and `releases.win.json`: files used by the automatic updater.
- `SHA256SUMS.json`: SHA-256 checksums for the release files.

## Licensing

Project-original software is licensed under the **PolyForm Noncommercial
License 1.0.0**. See [LICENSE.md](LICENSE.md), [NOTICE.md](NOTICE.md), and the
plain-English [license guide](LICENSE-GUIDE.md).

Inspection, personal use, and noncommercial modification and sharing are
permitted under the license. Commercial exploitation is not generally licensed;
the license contains express permissions for certain noncommercial organizations.
This is source-available software, not OSI-approved open-source software.
Third-party components retain their original licenses, including Reticly's MIT
license. The project license does not grant rights to imported Crosshair X designs
or services.
