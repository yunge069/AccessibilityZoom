using AccessibilityZoom;
using Microsoft.Win32;
using System.Xml.Linq;

internal static class StartupTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        int applied = 0, waited = 0, warnings = 0;
        var snapshots = new Queue<(string, bool)>(new[] { ("arriving", true), ("ready", true), ("ready", true) });
        await StartupColorSequence.RunAsync(() => true, () => false, () => snapshots.Dequeue(),
            () => { applied++; return true; }, _ => warnings++, _ => { }, CancellationToken.None,
            _ => { waited++; return Task.CompletedTask; });
        check(applied == 1 && waited == 3 && warnings == 0,
            "Startup must apply once after two stable layouts, allowing outputs to arrive first.");

        applied = waited = warnings = 0;
        await StartupColorSequence.RunAsync(() => true, () => false, () => ("connected", false),
            () => { applied++; return true; }, _ => warnings++, _ => { }, CancellationToken.None,
            _ => { waited++; return Task.CompletedTask; });
        check(applied == 0 && waited == 8 && warnings == 1,
            "Missing saved monitors must stop after a bounded wait without applying to another output.");

        applied = warnings = 0;
        await StartupColorSequence.RunAsync(() => true, () => false, () => ("ready", true),
            () => { applied++; return false; }, _ => warnings++, _ => { }, CancellationToken.None,
            _ => Task.CompletedTask);
        check(applied == 1 && warnings == 1, "Startup apply failures must be reported without repeated driver writes.");

        foreach (bool alreadyEnabled in new[] { false, true })
        {
            applied = warnings = 0;
            await StartupColorSequence.RunAsync(() => alreadyEnabled, () => alreadyEnabled,
                () => throw new Exception("No display lookup needed"), () => { applied++; return true; },
                _ => warnings++, _ => { }, CancellationToken.None, _ => Task.CompletedTask);
            check(applied == 0 && warnings == 0,
                "Disabled preferences and already enabled color must stop startup without changing the display.");
        }

        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await StartupColorSequence.RunAsync(() => true, () => false,
                () => throw new Exception("Must not inspect displays after shutdown"), () => false,
                _ => { }, _ => { }, cancellation.Token,
                _ => { cancellation.Cancel(); return Task.CompletedTask; });
        }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "Exiting during startup must cancel before any display change.");

        string testKey = @"Software\AccessibilityZoom.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var registration = new WindowsStartupRegistration(@"C:\Test Folder\AccessibilityZoom.exe", testKey);
            check(registration.Read(out bool enabled, out string error) && !enabled,
                "Windows startup must remain opt-in when its entry does not exist: " + error);
            check(registration.Command == "\"C:\\Test Folder\\AccessibilityZoom.exe\" --start-minimized",
                "Startup must quote paths with spaces and launch into the tray.");
            check(registration.SetEnabled(true, out error), "Test registration must enable: " + error);
            check(registration.Read(out enabled, out error) && enabled, "Enabled startup must survive a fresh registry read.");
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey, true))
            {
                check((string)key.GetValue("AccessibilityZoom") == registration.Command,
                    "The registered command must match the current executable and tray argument.");
                key.SetValue("UnrelatedEntry", "keep");
            }
            var moved = new WindowsStartupRegistration(@"C:\New Folder\AccessibilityZoom.exe", testKey);
            check(moved.SetEnabled(true, out error), "Updating to a new app location must succeed: " + error);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey))
                check((string)key.GetValue("AccessibilityZoom") == moved.Command,
                    "Windows startup must update to the version currently opened.");
            check(moved.SetEnabled(false, out error) && moved.Read(out enabled, out error) && !enabled,
                "Turning off startup must remove the application entry.");
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey))
                check((string)key.GetValue("UnrelatedEntry") == "keep", "Disabling startup must preserve other entries.");
            check(moved.SetEnabled(false, out error), "Disabling an absent entry must be harmless: " + error);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, false); }

        RunLegacyTaskChecks(check);
    }

    private static void RunLegacyTaskChecks(Action<bool, string> check)
    {
        const string sid = "S-1-5-21-1-2-3-1001", user = @"PC\Owner";
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        const string legacyArguments = "-NoProfile -WindowStyle Hidden -Command \"Start-Process -FilePath " +
            "'C:\\Old Folder\\AccessibilityZoom.exe' -ArgumentList '--start-minimized' -Verb RunAs\"";
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Xml(string principal = sid, string triggerUser = user, string command = null,
            string arguments = legacyArguments, bool extraAction = false, string trigger = "LogonTrigger") =>
            new XElement(ns + "Task",
                new XElement(ns + "Principals", new XElement(ns + "Principal", new XElement(ns + "UserId", principal))),
                new XElement(ns + "Triggers", new XElement(ns + trigger, new XElement(ns + "UserId", triggerUser))),
                new XElement(ns + "Actions", new XElement(ns + "Exec",
                    new XElement(ns + "Command", command ?? powershell), new XElement(ns + "Arguments", arguments)),
                    extraAction ? new XElement(ns + "Exec") : null)).ToString();
        bool Owned(string xml) => LegacyStartupTask.IsOwned(LegacyStartupTask.TaskName, xml, sid, user);
        check(Owned(Xml()), "Recognize the actual historical PowerShell/UAC task for the current user.");
        check(Owned(Xml(principal: user, triggerUser: sid)), "Task ownership accepts the current SID or qualified account name.");
        check(!LegacyStartupTask.IsOwned("Other Task", Xml(), sid, user), "Never migrate another task name.");
        check(!Owned(Xml(principal: "S-1-5-18")), "Never migrate another user's task.");
        check(!Owned(Xml(triggerUser: @"PC\SomeoneElse")), "Never migrate another user's sign-in trigger.");
        check(!Owned(Xml(command: @"C:\Other\powershell.exe")), "Only recognize the Windows PowerShell executable.");
        check(!Owned(Xml(arguments: legacyArguments.Replace("AccessibilityZoom.exe", "Other.exe"))),
            "A matching name must not grant ownership of a task running another application.");
        check(!Owned(Xml(arguments: legacyArguments.Replace(@"C:\Old Folder\AccessibilityZoom.exe", "AccessibilityZoom.exe"))),
            "Historical startup commands must identify an absolute application path.");
        check(!Owned(Xml(arguments: legacyArguments + " ; Start-Process Other.exe")),
            "Edited task commands must remain untouched.");
        check(!Owned(Xml(extraAction: true)) && !Owned(Xml(trigger: "TimeTrigger")) && !Owned("invalid XML"),
            "Unknown task actions, triggers and malformed definitions must remain untouched.");

        string testKey = @"Software\AccessibilityZoom.Tests\" + Guid.NewGuid().ToString("N");
        var task = new FakeLegacyTask();
        var registration = new WindowsStartupRegistration(@"C:\Managed\Accessibility Zoom.exe", testKey, legacyTask: task);
        try
        {
            check(registration.RefreshEnabledRegistration(out _) && registration.Read(out bool enabled, out _) && !enabled,
                "A fresh installation without an enabled legacy task must remain opted out.");
            task.IsEnabled = true;
            check(registration.Read(out enabled, out _) && enabled, "A task-only sign-in registration is an existing opt-in.");
            task.BeforeDisable = () =>
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(testKey);
                check((string)key?.GetValue("AccessibilityZoom") == registration.Command,
                    "Persist the stable launcher before disabling a legacy sign-in task.");
            };
            check(registration.RefreshEnabledRegistration(out _) && !task.Enabled,
                "Task-only startup migrates to the stable launcher and disables the historical task.");
            check(registration.Read(out enabled, out _) && enabled, "Task migration must preserve the startup opt-in.");
            int disables = task.DisableCount;
            check(registration.RefreshEnabledRegistration(out _) && task.DisableCount == disables,
                "Repeated startup migration is idempotent.");

            task.BeforeDisable = null;
            task.IsEnabled = true;
            check(registration.SetEnabled(false, out _) && !task.Enabled && registration.Read(out enabled, out _) && !enabled,
                "Turning off startup disables both historical task and Run entry.");
            task.IsEnabled = true;
            task.FailDisable = true;
            check(!registration.RefreshEnabledRegistration(out string error) && error.Contains("denied") &&
                registration.Read(out enabled, out _) && enabled,
                "A denied task migration reports failure while retaining the replacement launcher and existing opt-in.");
            check(!registration.SetEnabled(false, out _) && registration.Read(out enabled, out _) && enabled,
                "A task that cannot be disabled must not be reported as a successful startup opt-out.");
            task.FailDisable = false;
            check(registration.SetEnabled(false, out _), "Startup opt-out can be retried after task access is restored.");
            task.IsEnabled = true;
            check(registration.SetEnabled(true, out _) && !task.Enabled && registration.Read(out enabled, out _) && enabled,
                "Explicitly enabling startup also consolidates a historical task into the stable launcher.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, false); }
    }

    private sealed class FakeLegacyTask : ILegacyStartupTask
    {
        internal bool IsEnabled, FailDisable;
        internal int DisableCount;
        internal Action BeforeDisable;
        public bool Enabled => IsEnabled;
        public void Disable()
        {
            if (FailDisable) throw new UnauthorizedAccessException("Task access denied");
            BeforeDisable?.Invoke();
            IsEnabled = false;
            DisableCount++;
        }
    }
}
