# Accessibility Zoom 4.3.20

- Adds a per-user installer and a permanent launcher for Windows startup.
- Adds GitHub update checks at startup, every six hours, and on demand.
- Updates require confirmation before download and restart. Choosing Later keeps the current version running.
- Saves and backs up settings, profiles, hotkeys and crosshair designs before applying an update.
- Replaces the installed application files instead of creating another numbered installation.

For the first managed installation, exit the previous version from its tray menu and run AccessibilityZoom.App-win-Setup.exe once. Existing preferences are reused automatically. Future updates are available from the Updates card in the app.

The installer manages its own application directory. Previously extracted portable builds are separate and need a one-time cleanup. Settings remain in %LOCALAPPDATA%\AccessibilityZoom; the application installs separately in %LOCALAPPDATA%\AccessibilityZoom.App.
