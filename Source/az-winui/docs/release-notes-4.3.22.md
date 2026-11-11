# Accessibility Zoom 4.3.22

- Fixes Windows sign-in launching an old portable version through the historical **AccessibilityZoom Admin Prompt** scheduled task, even after the Run entry has been updated.
- Managed installs and updates move an existing startup opt-in to the permanent **Accessibility Zoom.exe** launcher, then disable the recognized legacy task. Future updates keep the same startup path.
- Turning **Start with Windows** off also disables the recognized legacy task. Fresh installations remain opted out.
- Task migration checks the current user, sign-in trigger and exact historical app command. Unknown or edited tasks are left alone; failures appear in the app's startup warning.

Settings, profiles, shortcuts and imported crosshairs retain their existing locations. Old portable folders are not deleted automatically; use the installed app and its shortcuts from now on.
