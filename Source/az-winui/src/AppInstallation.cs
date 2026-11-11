using System;
using System.IO;
using Velopack.Locators;

namespace AccessibilityZoom
{
    internal static class AppInstallation
    {
        // Deliberately different from the existing user-data directory. Even an
        // uninstall must never delete settings, saved profiles or crosshairs.
        internal const string PackageId = "AccessibilityZoom.App";
        // vpk names the execution stub after --packTitle, not --mainExe.
        internal const string LauncherFileName = "Accessibility Zoom.exe";
        internal static string DataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessibilityZoom");

        internal static bool IsManaged => VelopackLocator.Current.AppId == PackageId &&
            VelopackLocator.Current.CurrentlyInstalledVersion != null && !VelopackLocator.Current.IsPortable;

        internal static string StartupExecutable
        {
            get
            {
                if (IsManaged) return Path.Combine(VelopackLocator.Current.RootAppDir, LauncherFileName);
                string installedRoot = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), PackageId);
                string launcher = Path.Combine(installedRoot, LauncherFileName);
                if (File.Exists(launcher) && File.Exists(Path.Combine(installedRoot, "Update.exe")) &&
                    File.Exists(Path.Combine(installedRoot, "current", "sq.version"))) return launcher;
                return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AccessibilityZoom.exe");
            }
        }

        internal static void RefreshStartup()
        {
            var registration = CreateStartupRegistration();
            if (registration.Read(out bool enabled, out _) && enabled)
                registration.RefreshEnabledRegistration(out _);
        }

        internal static WindowsStartupRegistration CreateStartupRegistration() => new(
            StartupExecutable, legacyTask: IsManaged ? new LegacyStartupTask() : null);

        internal static void RemoveStartup()
        {
            // Do not remove an entry redirected to a different installation.
            new WindowsStartupRegistration(StartupExecutable).RemoveIfOwned(out _);
        }
    }
}
