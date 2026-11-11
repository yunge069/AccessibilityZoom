using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace AccessibilityZoom
{
    internal sealed class CrosshairAsset
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Crosshair";
        public string Source { get; set; } = "";
        public string ModelJson { get; set; } = "[]";
        public double NativeScale { get; set; } = 1;
        public override string ToString() => Name;

        internal static CrosshairAsset Create(string name, string source, string model, double nativeScale = 1)
        {
            if (!double.IsFinite(nativeScale) || nativeScale <= 0 || nativeScale > 16) throw new ArgumentException("Invalid crosshair scale.");
            string identity = model + (nativeScale == 1 ? "" : "\nscale=" + nativeScale.ToString("R", CultureInfo.InvariantCulture));
            return new CrosshairAsset
            {
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant(),
                Name = string.IsNullOrWhiteSpace(name) ? "Crosshair" : new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()),
                Source = source, ModelJson = model, NativeScale = nativeScale
            };
        }

        internal static CrosshairAsset Default => new()
        {
            Id = "builtin", Name = "Green cross", Source = "Built in",
            ModelJson = "[{\"type\":\"model\",\"dot\":{\"diameter\":2,\"opacity\":0,\"color\":\"#00FF66\",\"shape\":\"square\"}," +
                "\"line\":{\"length\":6,\"thickness\":2,\"offset\":6,\"opacity\":1,\"color\":\"#00FF66\",\"shape\":\"rectangle\",\"visible\":[\"top\",\"right\",\"bottom\",\"left\"]}," +
                "\"outline\":{\"thickness\":1,\"opacity\":1,\"color\":\"#000000\"},\"position\":{\"x\":0,\"y\":0},\"layer\":{\"size\":1}}]"
        };
    }

    internal sealed record CrosshairPreferences
    {
        public string SelectedId { get; set; } = "builtin";
        public bool Enabled { get; set; }
        public int SizePercent { get; set; } = 100;
        public int OpacityPercent { get; set; } = 100;
        public string MonitorDevice { get; set; } = "";
        internal CrosshairPreferences Clamped() => this with
        {
            SelectedId = SelectedId ?? "builtin", MonitorDevice = MonitorDevice ?? "",
            SizePercent = Math.Clamp(SizePercent, 25, 400), OpacityPercent = Math.Clamp(OpacityPercent, 5, 100)
        };
    }

    internal sealed class CrosshairStore
    {
        internal string DirectoryPath { get; }
        internal CrosshairStore(string directory = null)
        {
            DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessibilityZoom", "Crosshairs");
        }

        internal List<CrosshairAsset> LoadAssets(out string warning)
        {
            warning = "";
            List<CrosshairAsset> result = new() { CrosshairAsset.Default };
            if (!Directory.Exists(DirectoryPath)) return result;
            int failed = 0;
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.crosshair.json").Take(100))
            {
                try
                {
                    if (new FileInfo(path).Length > CrosshairImporter.MaximumJsonBytes * 2) throw new FormatException();
                    var asset = JsonSerializer.Deserialize<CrosshairAsset>(File.ReadAllText(path));
                    if (asset == null || asset.Id?.Length != 64 || !asset.Id.All(Uri.IsHexDigit)) throw new FormatException();
                    if (!double.IsFinite(asset.NativeScale) || asset.NativeScale <= 0 || asset.NativeScale > 16) throw new FormatException();
                    CrosshairImporter.ValidateModel(asset.ModelJson);
                    if (result.All(item => item.Id != asset.Id)) result.Add(asset);
                }
                catch { failed++; }
            }
            if (failed > 0) warning = failed + " saved crosshair(s) could not be loaded.";
            return result;
        }

        internal CrosshairPreferences LoadPreferences(out string warning)
        {
            warning = "";
            string path = Path.Combine(DirectoryPath, "settings.json");
            if (!File.Exists(path)) return new CrosshairPreferences();
            try
            {
                if (new FileInfo(path).Length > 16384) throw new FormatException();
                return (JsonSerializer.Deserialize<CrosshairPreferences>(File.ReadAllText(path)) ?? new()).Clamped();
            }
            catch { warning = "Crosshair settings could not be read; using defaults."; return new(); }
        }

        internal void SaveAsset(CrosshairAsset asset)
        {
            if (asset.Id.Length != 64 || !asset.Id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid crosshair ID.");
            CrosshairImporter.ValidateModel(asset.ModelJson);
            WriteAtomic(Path.Combine(DirectoryPath, asset.Id + ".crosshair.json"), JsonSerializer.Serialize(asset));
        }

        internal void DeleteAsset(string id)
        {
            if (id == null || id.Length != 64 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Built-in designs cannot be deleted.");
            string path = Path.Combine(DirectoryPath, id + ".crosshair.json");
            if (File.Exists(path)) File.Delete(path);
        }

        // One-time fix for 4.3.17 and earlier: Crosshair X library imports used the
        // stale top-level crosshairSize instead of the active profile's size.
        // Affected copies are rescaled; a copy that becomes identical to another
        // design is removed. Returns the old -> new id map for the selection.
        internal Dictionary<string, string> RepairCrosshairXScale(double staleScale, double profileScale)
        {
            var moved = new Dictionary<string, string>();
            string marker = Path.Combine(DirectoryPath, "scale-repair-1.done");
            if (!Directory.Exists(DirectoryPath) || File.Exists(marker)) return moved;
            if (staleScale != profileScale)
            {
                List<CrosshairAsset> assets = LoadAssets(out _);
                foreach (CrosshairAsset asset in assets.Where(item => item.Source == "Crosshair X library" && item.NativeScale == staleScale).ToList())
                {
                    CrosshairAsset fixedAsset = CrosshairAsset.Create(asset.Name, asset.Source, asset.ModelJson, profileScale);
                    if (fixedAsset.Id != asset.Id)
                    {
                        if (assets.All(item => item.Id != fixedAsset.Id)) { SaveAsset(fixedAsset); assets.Add(fixedAsset); }
                        DeleteAsset(asset.Id);
                        moved[asset.Id] = fixedAsset.Id;
                    }
                }
            }
            WriteAtomic(marker, "Crosshair X scale repaired " + DateTime.Now.ToString("O"));
            return moved;
        }

        internal void SavePreferences(CrosshairPreferences preferences) =>
            WriteAtomic(Path.Combine(DirectoryPath, "settings.json"), JsonSerializer.Serialize(preferences.Clamped()));

        private void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, true);
        }
    }

    internal sealed record LocalCrosshair(string Name, string Path, bool Current = false, double NativeScale = 1)
    {
        public override string ToString() => Name;
    }
}
