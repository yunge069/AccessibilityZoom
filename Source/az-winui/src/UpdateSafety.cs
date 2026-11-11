using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace AccessibilityZoom
{
    internal static class UpdateSafety
    {
        internal static bool TryRepository(string value, out string repository)
        {
            repository = "";
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) || uri.Scheme != "https" ||
                uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 2 || !ValidPart(parts[0]) || !ValidPart(parts[1])) return false;
            repository = "https://github.com/" + parts[0] + "/" + parts[1];
            return true;
        }

        private static bool ValidPart(string part)
        {
            if (part.Length == 0 || part == "." || part == "..") return false;
            foreach (char c in part)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '-' && c != '_' && c != '.') return false;
            return true;
        }

        internal static string BackupSettings(string dataDirectory, string backupDirectory)
        {
            Directory.CreateDirectory(backupDirectory);
            string destination = Path.Combine(backupDirectory,
                "settings-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
            string temporary = destination + ".partial";
            try
            {
                using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    if (Directory.Exists(dataDirectory))
                        AddDirectory(zip, new DirectoryInfo(dataDirectory), "", Path.GetFullPath(backupDirectory));
                }
                File.Move(temporary, destination);
                return destination;
            }
            catch
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }
        }

        private static void AddDirectory(ZipArchive zip, DirectoryInfo directory, string relative, string backupDirectory)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Settings backup cannot follow linked folders: " + directory.Name);
            foreach (FileInfo file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Settings backup cannot follow linked files: " + file.Name);
                zip.CreateEntryFromFile(file.FullName, relative + file.Name, CompressionLevel.Optimal);
            }
            foreach (DirectoryInfo child in directory.EnumerateDirectories())
            {
                if (string.Equals(child.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    backupDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                AddDirectory(zip, child, relative + child.Name + "/", backupDirectory);
            }
        }

        // The UI supplies the consent dialog. No download, backup, shutdown or
        // installation may happen before it returns an explicit Yes.
        internal static async Task<bool> InstallAsync(Func<Task<bool>> consent,
            Func<CancellationToken, Task> download, Action saveAndBackup, Action applyAndExit,
            CancellationToken cancellationToken)
        {
            if (!await consent()) return false;
            cancellationToken.ThrowIfCancellationRequested();
            await download(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            saveAndBackup();
            cancellationToken.ThrowIfCancellationRequested();
            applyAndExit();
            return true;
        }
    }
}
