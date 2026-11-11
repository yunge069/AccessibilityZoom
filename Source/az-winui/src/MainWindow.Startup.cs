using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace AccessibilityZoom
{
    public sealed partial class MainWindow
    {
        private string windowsStartupWarning = "";
        private bool updatingWindowsStartup;

        private void InitializeWindowsStartupSetting()
        {
            if (previewMode) { WindowsStartupToggle.IsEnabled = false; return; }
            var registration = AppInstallation.CreateStartupRegistration();
            if (!registration.Read(out bool enabled, out string error))
                windowsStartupWarning = "Windows startup setting could not be read: " + error;
            // Managed installations always use the stable launcher; opening an
            // older development build must not silently downgrade registration.
            if (enabled && !registration.RefreshEnabledRegistration(out error))
                windowsStartupWarning = "Windows startup location could not be updated: " + error;
            WindowsStartupToggle.IsOn = enabled;
            WindowsStartupToggle.Toggled += (_, __) =>
            {
                if (updatingWindowsStartup) return;
                if (registration.SetEnabled(WindowsStartupToggle.IsOn, out string writeError))
                    windowsStartupWarning = "";
                else
                {
                    windowsStartupWarning = "Windows startup setting could not be saved: " + writeError;
                    registration.Read(out bool actual, out string readError);
                    updatingWindowsStartup = true;
                    WindowsStartupToggle.IsOn = actual;
                    updatingWindowsStartup = false;
                }
                UpdateWarnings();
            };
        }

        internal void WhenReady(Action action)
        {
            if (ContentRoot.IsLoaded) { action(); return; }
            RoutedEventHandler loaded = null;
            loaded = (_, __) =>
            {
                ContentRoot.Loaded -= loaded;
                if (!closed && !exiting) action();
            };
            ContentRoot.Loaded += loaded;
        }

        internal void HideToTray() => appWindow?.Hide();
    }
}
