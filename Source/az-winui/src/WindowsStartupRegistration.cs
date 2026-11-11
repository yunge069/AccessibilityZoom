using System;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace AccessibilityZoom
{
    internal sealed class WindowsStartupRegistration
    {
        private const string DefaultKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private readonly string executable;
        private readonly string keyPath;
        private readonly string valueName;
        private readonly ILegacyStartupTask legacyTask;

        internal WindowsStartupRegistration(string executable, string keyPath = DefaultKey, string valueName = "AccessibilityZoom",
            ILegacyStartupTask legacyTask = null)
        {
            if (string.IsNullOrWhiteSpace(executable) || executable.Contains('"'))
                throw new ArgumentException("A valid application path is required.", nameof(executable));
            this.executable = Path.GetFullPath(executable);
            this.keyPath = keyPath;
            this.valueName = valueName;
            this.legacyTask = legacyTask;
        }

        internal string Command => "\"" + executable + "\" --start-minimized";

        internal bool RefreshEnabledRegistration(out string error)
        {
            try
            {
                // A task-only installation is also an existing startup opt-in.
                // Write its replacement before disabling the historical task.
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true);
                string command = key?.GetValue(valueName) as string;
                bool registered = !string.IsNullOrWhiteSpace(command);
                bool taskEnabled = legacyTask?.Enabled == true;
                if (registered || taskEnabled)
                {
                    string previous = ExecutableFromCommand(command);
                    if (CanReplace(previous, executable, ReadVersion))
                    {
                        using RegistryKey destination = Registry.CurrentUser.CreateSubKey(keyPath, true);
                        destination.SetValue(valueName, Command, RegistryValueKind.String);
                        if (taskEnabled) legacyTask.Disable();
                    }
                }
                error = "";
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        internal bool RemoveIfOwned(out string error)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true);
                if (key?.GetValue(valueName) is string command &&
                    string.Equals(ExecutableFromCommand(command), executable, StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue(valueName, false);
                error = "";
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        internal static string ExecutableFromCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            command = command.Trim();
            if (command[0] == '"')
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : "";
            }
            int separator = command.IndexOf(' ');
            return separator < 0 ? command : command.Substring(0, separator);
        }

        internal static bool CanReplace(string previous, string next, Func<string, Version> versionOf)
        {
            if (string.Equals(previous, next, StringComparison.OrdinalIgnoreCase)) return true;
            Version previousVersion = versionOf(previous);
            Version nextVersion = versionOf(next);
            return previousVersion == null || (nextVersion != null && nextVersion >= previousVersion);
        }

        private static Version ReadVersion(string path)
        {
            try
            {
                // Velopack's stable root launcher has the updater's version;
                // compare the actual application inside current instead.
                string installed = Path.Combine(Path.GetDirectoryName(path), "current", "AccessibilityZoom.exe");
                if (File.Exists(installed) && File.Exists(Path.Combine(Path.GetDirectoryName(path), "Update.exe")))
                    path = installed;
                if (!File.Exists(path)) return null;
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            }
            catch { return null; }
        }

        internal bool Read(out bool enabled, out string error)
        {
            enabled = false;
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath);
                enabled = key?.GetValue(valueName) is string command && !string.IsNullOrWhiteSpace(command);
                enabled |= legacyTask?.Enabled == true;
                error = "";
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        internal bool SetEnabled(bool enabled, out string error)
        {
            try
            {
                if (enabled)
                {
                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, true);
                    key.SetValue(valueName, Command, RegistryValueKind.String);
                    legacyTask?.Disable();
                }
                else
                {
                    // If this fails, retain the Run entry and report that
                    // startup could not be turned off completely.
                    legacyTask?.Disable();
                    using RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true);
                    key?.DeleteValue(valueName, false);
                }
                error = "";
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }
    }
}
