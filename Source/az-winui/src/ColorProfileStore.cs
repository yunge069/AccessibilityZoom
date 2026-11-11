using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AccessibilityZoom
{
    internal sealed class ColorProfile
    {
        internal ColorProfile(
            string id,
            string name,
            ColorFilterSettings settings)
        {
            Id = id;
            Name = name;
            Settings = settings.Clamped();
        }

        internal string Id { get; private set; }
        internal string Name { get; set; }
        internal ColorFilterSettings Settings;

        public override string ToString()
        {
            return Name;
        }
    }

    internal static class ColorProfileStore
    {
        private const int CurrentVersion = 2;

        internal static List<ColorProfile> Load()
        {
            try
            {
                string path = GetProfilesPath();
                if (File.Exists(path))
                {
                    return DeserializeProfiles(File.ReadAllText(path));
                }
            }
            catch
            {
                // The color controls also work without saved profiles.
            }
            return new List<ColorProfile>();
        }

        internal static bool Save(
            IEnumerable<ColorProfile> profiles,
            out string errorMessage)
        {
            try
            {
                string path = GetProfilesPath();
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);

                string json = SerializeProfiles(profiles);
                string temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, path, true);
                errorMessage = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                return false;
            }
        }

        internal static ColorProfile CreateUserProfile(
            string name,
            ColorFilterSettings settings)
        {
            return new ColorProfile(
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                NormalizeName(name),
                settings);
        }

        internal static string NormalizeName(string name)
        {
            string normalized = (name ?? string.Empty).Trim();
            if (normalized.Length > 60)
            {
                normalized = normalized.Substring(0, 60).TrimEnd();
            }
            return normalized;
        }

        internal static bool IdEquals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool RunSelfTest()
        {
            ColorFilterSettings settings = ColorFilterSettings.Visibility;
            settings.RadeonImageSharpening = true;
            settings.RadeonImageSharpeningSharpness = 75;
            ColorProfile legacyProfile = new ColorProfile("visibility", "Visibility", settings);
            var legacyDocument = new ProfileDocument
            {
                Version = 1,
                Profiles = new List<ProfileRecord> { ToRecord(legacyProfile) }
            };
            List<ColorProfile> profiles = DeserializeProfiles(JsonSerializer.Serialize(legacyDocument));
            if (profiles.Count != 1 || !profiles[0].Settings.NearlyEquals(ColorFilterSettings.Visibility))
                return false;

            profiles[0].Name = "Renamed profile";
            profiles[0].Settings = settings;
            List<ColorProfile> roundTrip = DeserializeProfiles(SerializeProfiles(profiles));
            if (roundTrip.Count != 1 || roundTrip[0].Id != legacyProfile.Id ||
                roundTrip[0].Name != profiles[0].Name || !roundTrip[0].Settings.NearlyEquals(settings))
                return false;

            profiles.Clear();
            if (DeserializeProfiles(SerializeProfiles(profiles)).Count != 0) return false;

            ColorProfile custom = CreateUserProfile("  Example  ", settings);
            profiles.Add(custom);
            roundTrip = DeserializeProfiles(SerializeProfiles(profiles));
            if (roundTrip.Count != 1 || roundTrip[0].Id != custom.Id ||
                roundTrip[0].Name != "Example" || !roundTrip[0].Settings.NearlyEquals(settings))
                return false;

            profiles.AddRange(Enumerable.Range(1, 100).Select(index => CreateUserProfile($"Profile {index}", settings)));
            return DeserializeProfiles(SerializeProfiles(profiles)).Count == profiles.Count;
        }

        private static string SerializeProfiles(IEnumerable<ColorProfile> profiles)
        {
            ProfileDocument document = new ProfileDocument
            {
                Version = CurrentVersion,
                Profiles = profiles.Select(ToRecord).ToList()
            };
            return JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions { WriteIndented = true });
        }

        private static List<ColorProfile> DeserializeProfiles(string json)
        {
            ProfileDocument document = JsonSerializer.Deserialize<ProfileDocument>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            List<ColorProfile> loaded = new List<ColorProfile>();
            if (document?.Profiles == null) return loaded;

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProfileRecord record in document.Profiles)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Name))
                {
                    continue;
                }

                string id = NormalizeId(record.Id);
                if (!seenIds.Add(id))
                {
                    continue;
                }

                string name = NormalizeName(record.Name);
                ColorFilterSettings settings = new ColorFilterSettings
                {
                    Gamma = record.Gamma,
                    Brightness = record.Brightness,
                    Contrast = record.Contrast,
                    Saturation = record.Saturation,
                    ShadowBoost = record.ShadowBoost,
                    HighlightProtection = record.HighlightProtection,
                    RedGain = record.RedGain,
                    GreenGain = record.GreenGain,
                    BlueGain = record.BlueGain,
                    RadeonImageSharpening = record.RadeonImageSharpening,
                    RadeonImageSharpeningSharpness =
                        record.RadeonImageSharpeningSharpness
                };
                if (document.Version < CurrentVersion && HadLegacySharpening(id))
                {
                    // Migrate built-in profiles written by older releases. The
                    // visibility presets do not own Radeon Image Sharpening.
                    settings.RadeonImageSharpening = false;
                    settings.RadeonImageSharpeningSharpness = 0;
                }
                loaded.Add(new ColorProfile(id, name, settings));
            }
            return loaded;
        }

        private static ProfileRecord ToRecord(ColorProfile profile)
        {
            ColorFilterSettings settings = profile.Settings.Clamped();
            return new ProfileRecord
            {
                Id = profile.Id,
                Name = profile.Name,
                Gamma = settings.Gamma,
                Brightness = settings.Brightness,
                Contrast = settings.Contrast,
                Saturation = settings.Saturation,
                ShadowBoost = settings.ShadowBoost,
                HighlightProtection = settings.HighlightProtection,
                RedGain = settings.RedGain,
                GreenGain = settings.GreenGain,
                BlueGain = settings.BlueGain,
                RadeonImageSharpening = settings.RadeonImageSharpening,
                RadeonImageSharpeningSharpness =
                    settings.RadeonImageSharpeningSharpness
            };
        }

        internal static bool HadLegacySharpening(string id)
        {
            return IdEquals(id, "visibility") ||
                IdEquals(id, "night") ||
                IdEquals(id, "gaming");
        }

        private static string NormalizeId(string id)
        {
            string value = (id ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 80 ||
                value.Any(character =>
                    !char.IsLetterOrDigit(character) &&
                    character != '-' && character != '_' && character != '.'))
            {
                return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            }
            return value;
        }

        private static string GetProfilesPath()
        {
            string folder = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(folder, "AccessibilityZoom", "profiles.json");
        }

        private sealed class ProfileDocument
        {
            public int Version { get; set; }
            public List<ProfileRecord> Profiles { get; set; }
        }

        private sealed class ProfileRecord
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public float Gamma { get; set; }
            public float Brightness { get; set; }
            public float Contrast { get; set; }
            public float Saturation { get; set; }
            public float ShadowBoost { get; set; }
            public float HighlightProtection { get; set; }
            public float RedGain { get; set; }
            public float GreenGain { get; set; }
            public float BlueGain { get; set; }
            public bool RadeonImageSharpening { get; set; }
            public int RadeonImageSharpeningSharpness { get; set; }
        }
    }
}
