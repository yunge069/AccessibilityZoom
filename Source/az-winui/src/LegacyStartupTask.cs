using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AccessibilityZoom
{
    internal interface ILegacyStartupTask
    {
        bool Enabled { get; }
        void Disable();
    }

    // This historical task is separate from the Run key and starts an old
    // portable copy with a UAC prompt. Keep it disabled for reversible migration.
    internal sealed class LegacyStartupTask : ILegacyStartupTask
    {
        internal const string TaskName = "AccessibilityZoom Admin Prompt";

        public bool Enabled => AccessTask(disable: false);
        public void Disable() => AccessTask(disable: true);

        private static bool AccessTask(bool disable)
        {
            object service = null, folder = null, task = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                ((dynamic)service).Connect();
                folder = ((dynamic)service).GetFolder(@"\");
                try { task = ((dynamic)folder).GetTask(TaskName); }
                catch (COMException exception) when (exception.HResult == unchecked((int)0x80070002))
                { return false; } // No historical task on a fresh installation.

                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                if (!IsOwned(TaskName, (string)((dynamic)task).Xml, identity.User.Value, identity.Name))
                    return false;
                bool enabled = (bool)((dynamic)task).Enabled;
                if (disable && enabled) ((dynamic)task).Enabled = false;
                return enabled;
            }
            finally
            {
                if (task != null) Marshal.FinalReleaseComObject(task);
                if (folder != null) Marshal.FinalReleaseComObject(folder);
                if (service != null) Marshal.FinalReleaseComObject(service);
            }
        }

        internal static bool IsOwned(string name, string xml, string userSid, string userName)
        {
            if (name != TaskName) return false;
            try
            {
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                XElement root = XElement.Parse(xml);
                bool SameUser(string value) => !string.IsNullOrWhiteSpace(value) &&
                    (string.Equals(value, userSid, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value, userName, StringComparison.OrdinalIgnoreCase));
                XElement[] principals = root.Element(ns + "Principals")?.Elements().ToArray();
                XElement[] triggers = root.Element(ns + "Triggers")?.Elements().ToArray();
                XElement[] actions = root.Element(ns + "Actions")?.Elements().ToArray();
                if (principals?.Length != 1 || !SameUser((string)principals[0].Element(ns + "UserId")) ||
                    triggers?.Length != 1 || triggers[0].Name != ns + "LogonTrigger" ||
                    !SameUser((string)triggers[0].Element(ns + "UserId")) ||
                    actions?.Length != 1 || actions[0].Name != ns + "Exec") return false;

                string command = (string)actions[0].Element(ns + "Command");
                string arguments = (string)actions[0].Element(ns + "Arguments");
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                if (!string.Equals(command, powershell, StringComparison.OrdinalIgnoreCase)) return false;
                Match match = Regex.Match(arguments ?? "",
                    "^-NoProfile -WindowStyle Hidden -Command \"Start-Process -FilePath '(?<path>[^']+)' " +
                    "-ArgumentList '--start-minimized' -Verb RunAs\"$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                string path = match.Groups["path"].Value;
                return match.Success && Path.IsPathFullyQualified(path) &&
                    string.Equals(Path.GetFileName(path), "AccessibilityZoom.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; } // Unknown or edited tasks must remain untouched.
        }
    }
}
