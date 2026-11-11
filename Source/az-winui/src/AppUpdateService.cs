using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace AccessibilityZoom
{
    internal sealed class AppUpdateService
    {
        internal string RepositoryUrl { get; }
        internal UpdateManager Manager { get; }
        internal string UnavailableReason { get; }
        internal bool Available => Manager != null;

        internal AppUpdateService()
        {
            string configured = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepositoryUrl")?.Value;
            if (!UpdateSafety.TryRepository(configured, out string repository))
            {
                UnavailableReason = "This build has no GitHub update repository configured.";
                return;
            }
            RepositoryUrl = repository;
            if (!AppInstallation.IsManaged)
            {
                UnavailableReason = "Install Accessibility Zoom using Setup.exe to enable automatic updates.";
                return;
            }
            Manager = new UpdateManager(new GithubSource(repository, null, false),
                new UpdateOptions { AllowVersionDowngrade = false });
        }
    }

    internal sealed class UpdatePreferences
    {
        public bool CheckAutomatically { get; set; } = true;

        private static string FilePath => Path.Combine(AppInstallation.DataDirectory, "update-preferences.json");

        internal static UpdatePreferences Load()
        {
            try { return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(FilePath)) ?? new(); }
            catch { return new(); }
        }

        internal void Save()
        {
            Directory.CreateDirectory(AppInstallation.DataDirectory);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, true);
        }
    }
}
