using System.IO.Compression;
using AccessibilityZoom;
using Microsoft.Win32;

internal static class UpdateTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (string invalid in new[] { "", "http://github.com/a/b", "https://github.com/a",
            "https://github.com/a/b/releases", "https://github.com.evil.test/a/b", "https://token@github.com/a/b",
            "https://github.com:444/a/b", "https://github.com/a/b?token=secret", "https://github.com/a/b#x" })
            check(!UpdateSafety.TryRepository(invalid, out _), "Reject invalid update origin: " + invalid);
        check(UpdateSafety.TryRepository("https://github.com/owner/release-repo/", out string repo) &&
            repo == "https://github.com/owner/release-repo", "Use a normalized public HTTPS repository.");

        var events = new List<string>();
        Task Download(CancellationToken _) { events.Add("download"); return Task.CompletedTask; }
        void Backup() => events.Add("save-backup");
        void Apply() => events.Add("apply-exit");
        check(!await UpdateSafety.InstallAsync(() => Task.FromResult(false), Download, Backup, Apply, default)
            && events.Count == 0, "Later must not download, save or start an update.");
        check(await UpdateSafety.InstallAsync(() => Task.FromResult(true), Download, Backup, Apply, default)
            && string.Join(",", events) == "download,save-backup,apply-exit", "Consent must precede download, backup and graceful exit.");
        events.Clear();
        bool failed = false;
        try { await UpdateSafety.InstallAsync(() => Task.FromResult(true),
            _ => throw new IOException("offline or invalid checksum"), Backup, Apply, default); }
        catch (IOException) { failed = true; }
        check(failed && events.Count == 0, "Download or integrity failure must leave the running app untouched.");
        failed = false;
        try { await UpdateSafety.InstallAsync(() => Task.FromResult(true), Download,
            () => throw new IOException("settings disk full"), Apply, default); }
        catch (IOException) { failed = true; }
        check(failed && events.SequenceEqual(new[] { "download" }), "Backup/save failure must prevent installation and exit.");
        events.Clear();
        using var cancellation = new CancellationTokenSource();
        try { await UpdateSafety.InstallAsync(() => Task.FromResult(true), _ =>
            { cancellation.Cancel(); return Task.CompletedTask; }, Backup, Apply, cancellation.Token); }
        catch (OperationCanceledException) { }
        check(events.Count == 0, "Exit during download must not apply later without consent.");

        string temporary = Path.Combine(Path.GetTempPath(), "az-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string data = Path.Combine(temporary, "data");
            string backups = Path.Combine(data, "UpdateBackups");
            Directory.CreateDirectory(Path.Combine(data, "Crosshairs"));
            File.WriteAllText(Path.Combine(data, "zoom.txt"), "zoom=2.25\nhotkeys=keep");
            File.WriteAllText(Path.Combine(data, "profiles.json"), "saved profiles");
            File.WriteAllBytes(Path.Combine(data, "Crosshairs", "design.crosshair.json"), new byte[] { 1, 2, 255, 0 });
            string first = UpdateSafety.BackupSettings(data, backups);
            string second = UpdateSafety.BackupSettings(data, backups);
            check(first != second && File.Exists(first), "Each backup must retain the previous recovery copy.");
            using var zip = ZipFile.OpenRead(second);
            check(zip.Entries.Count == 3 && zip.GetEntry("Crosshairs/design.crosshair.json") != null,
                "Backup includes preferences, profiles and imported designs, without recursively backing up backups.");
            using var content = new StreamReader(zip.GetEntry("zoom.txt").Open());
            check(content.ReadToEnd() == File.ReadAllText(Path.Combine(data, "zoom.txt")),
                "Settings bytes must be preserved without changing the original files.");
        }
        finally { Directory.Delete(temporary, true); }

        var versions = new Dictionary<string, Version> { ["old"] = new(4, 3, 19), ["new"] = new(4, 3, 20) };
        Version VersionOf(string path) => versions.GetValueOrDefault(path);
        check(!WindowsStartupRegistration.CanReplace("new", "old", VersionOf), "Opening an older build must not downgrade autostart.");
        check(WindowsStartupRegistration.CanReplace("old", "new", VersionOf), "A newer build can repair autostart.");
        check(WindowsStartupRegistration.CanReplace("missing", "new", VersionOf), "A missing old build can be replaced.");
        check(!WindowsStartupRegistration.CanReplace("new", "unknown", VersionOf), "An unknown build must not replace a known newer one.");
        check(WindowsStartupRegistration.ExecutableFromCommand("\"C:\\App Folder\\AccessibilityZoom.exe\" --start-minimized") ==
            @"C:\App Folder\AccessibilityZoom.exe", "Read quoted historical startup entries.");
        string testKey = @"Software\AccessibilityZoom.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var first = new WindowsStartupRegistration(@"C:\Old\AccessibilityZoom.exe", testKey);
            var second = new WindowsStartupRegistration(@"C:\Managed\AccessibilityZoom.exe", testKey);
            check(second.RefreshEnabledRegistration(out _) && second.Read(out bool enabled, out _) && !enabled,
                "Installer hooks must preserve an opt-out.");
            first.SetEnabled(true, out _);
            check(second.RefreshEnabledRegistration(out _) && second.Read(out enabled, out _) && enabled,
                "Installer hooks migrate an existing opt-in.");
            first.RemoveIfOwned(out _);
            check(second.Read(out enabled, out _) && enabled, "Uninstalling another copy must preserve managed autostart.");
            second.RemoveIfOwned(out _);
            check(second.Read(out enabled, out _) && !enabled, "Uninstall removes its own autostart.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, false); }
    }
}
