using AccessibilityZoom;
using Microsoft.Win32;

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
    }
}
