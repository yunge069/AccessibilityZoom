using System;
using System.Globalization;
using System.IO;

namespace AccessibilityZoom
{
    internal static class SettingsStore
    {
        private const float DefaultZoom = 2.0f;
        private const int CurrentVersion = 2;
        internal const int DefaultQuietVolumePercent = 30;

        internal static AppSettings Load(string settingsPath = null)
        {
            AppSettings settings = new AppSettings(
                DefaultZoom,
                1,
                false,
                ColorFilterSettings.Neutral,
                string.Empty);
            try
            {
                string path = settingsPath ?? GetSettingsPath();
                if (!File.Exists(path))
                {
                    return settings;
                }

                int version = 1;
                string[] lines = File.ReadAllLines(path);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    int separator = line.IndexOf('=');
                    if (separator < 0)
                    {
                        // Compatibility with the earliest file format, which
                        // stored only a raw zoom value.
                        float legacyZoom;
                        if (TryFloat(line, out legacyZoom)) settings.Zoom = legacyZoom;
                        continue;
                    }

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    if (key.StartsWith("hotkey.", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse(key.Substring(7), true, out HotkeyAction action) &&
                            Enum.IsDefined(action) && InputBinding.TryParse(value, out InputBinding binding))
                            settings.Bindings = settings.Bindings.Assign(action, binding);
                        continue;
                    }
                    float number;
                    if (key.Equals("zoom", StringComparison.OrdinalIgnoreCase) &&
                        TryFloat(value, out number)) settings.Zoom = number;
                    else if (key.Equals("button", StringComparison.OrdinalIgnoreCase))
                    {
                        int button;
                        if (int.TryParse(value, out button) && (button == 1 || button == 2))
                            settings.ThumbButton = button;
                    }
                    else if (key.Equals("filterEnabled", StringComparison.OrdinalIgnoreCase))
                        settings.FilterEnabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("startWithColorEnabled", StringComparison.OrdinalIgnoreCase))
                        settings.StartWithColorEnabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("filterMonitorDevice", StringComparison.OrdinalIgnoreCase))
                        settings.FilterMonitorDevice = value;
                    else if (key.Equals("profileId", StringComparison.OrdinalIgnoreCase))
                        settings.SelectedProfileId = value;
                    else if (key.Equals("version", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int storedVersion))
                        version = storedVersion;
                    else if (key.Equals("soundsEnabled", StringComparison.OrdinalIgnoreCase))
                        settings.SoundsEnabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("reduceTransparency", StringComparison.OrdinalIgnoreCase))
                        settings.ReduceTransparency = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("quietVolumePercent", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quietVolumePercent))
                        settings.QuietVolumePercent = Math.Clamp(quietVolumePercent, 0, 100);
                    else if (key.Equals("gamma", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.Gamma = number;
                    else if (key.Equals("brightness", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.Brightness = number;
                    else if (key.Equals("contrast", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.Contrast = number;
                    else if (key.Equals("saturation", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.Saturation = number;
                    else if (key.Equals("shadows", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.ShadowBoost = number;
                    else if (key.Equals("highlightProtection", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.HighlightProtection = number;
                    else if (key.Equals("red", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.RedGain = number;
                    else if (key.Equals("green", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.GreenGain = number;
                    else if (key.Equals("blue", StringComparison.OrdinalIgnoreCase) && TryFloat(value, out number))
                        settings.ColorFilter.BlueGain = number;
                    else if (key.Equals("imageSharpening", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.ColorFilter.RadeonImageSharpening =
                            value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (key.Equals("imageSharpness", StringComparison.OrdinalIgnoreCase))
                    {
                        int sharpness;
                        if (int.TryParse(
                            value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out sharpness))
                        {
                            settings.ColorFilter.RadeonImageSharpeningSharpness = sharpness;
                        }
                    }
                }

                if (version < CurrentVersion && ColorProfileStore.HadLegacySharpening(settings.SelectedProfileId))
                {
                    // Upgrade the active built-in preset from releases that
                    // stored sharpening as part of a visibility preset.
                    settings.ColorFilter.RadeonImageSharpening = false;
                    settings.ColorFilter.RadeonImageSharpeningSharpness = 0;
                }

                settings.ColorFilter = settings.ColorFilter.Clamped();
            }
            catch
            {
                // A read-only profile should not prevent the accessibility tool from running.
            }

            settings.Zoom = Math.Clamp(float.IsFinite(settings.Zoom) ? settings.Zoom : DefaultZoom, 1.25f, 8.0f);
            settings.ColorFilter = settings.ColorFilter.Clamped();
            return settings;
        }

        internal static bool Save(
            float zoom,
            int thumbButton,
            bool filterEnabled,
            ColorFilterSettings colorFilter,
            string selectedProfileId,
            bool soundsEnabled,
            bool reduceTransparency,
            out string errorMessage,
            string settingsPath = null,
            HotkeyBindings bindings = null,
            bool startWithColorEnabled = true,
            string filterMonitorDevice = "",
            int quietVolumePercent = DefaultQuietVolumePercent)
        {
            try
            {
                string path = settingsPath ?? GetSettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ColorFilterSettings filter = colorFilter.Clamped();
                string contents =
                    "version=" + CurrentVersion.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    "zoom=" + F(zoom) + Environment.NewLine +
                    "button=" + thumbButton.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    "filterEnabled=" + (filterEnabled ? "1" : "0") + Environment.NewLine +
                    "startWithColorEnabled=" + (startWithColorEnabled ? "1" : "0") + Environment.NewLine +
                    "filterMonitorDevice=" + (filterMonitorDevice ?? "").Replace("\r", "").Replace("\n", "") + Environment.NewLine +
                    "profileId=" + (selectedProfileId ?? string.Empty) + Environment.NewLine +
                    "soundsEnabled=" + (soundsEnabled ? "1" : "0") + Environment.NewLine +
                    "reduceTransparency=" + (reduceTransparency ? "1" : "0") + Environment.NewLine +
                    "quietVolumePercent=" + Math.Clamp(quietVolumePercent, 0, 100).ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    "gamma=" + F(filter.Gamma) + Environment.NewLine +
                    "brightness=" + F(filter.Brightness) + Environment.NewLine +
                    "contrast=" + F(filter.Contrast) + Environment.NewLine +
                    "saturation=" + F(filter.Saturation) + Environment.NewLine +
                    "shadows=" + F(filter.ShadowBoost) + Environment.NewLine +
                    "highlightProtection=" + F(filter.HighlightProtection) + Environment.NewLine +
                    "red=" + F(filter.RedGain) + Environment.NewLine +
                    "green=" + F(filter.GreenGain) + Environment.NewLine +
                    "blue=" + F(filter.BlueGain) + Environment.NewLine +
                    "imageSharpening=" +
                        (filter.RadeonImageSharpening ? "1" : "0") + Environment.NewLine +
                    "imageSharpness=" +
                        filter.RadeonImageSharpeningSharpness.ToString(
                            CultureInfo.InvariantCulture) + Environment.NewLine;
                HotkeyBindings shortcuts = bindings ?? HotkeyBindings.Defaults();
                foreach (HotkeyAction action in HotkeyBindings.Actions)
                    contents += "hotkey." + action + "=" + shortcuts[action].Serialize() + Environment.NewLine;
                string temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, contents);
                File.Move(temporaryPath, path, true);
                errorMessage = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                // Persistence is optional; magnification and the filter remain available.
                errorMessage = exception.Message;
                return false;
            }
        }

        private static bool TryFloat(string value, out float result)
        {
            return float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
        }

        private static string F(float value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static string GetSettingsPath()
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(folder, "AccessibilityZoom", "zoom.txt");
        }
    }

    internal sealed class AppSettings
    {
        internal AppSettings(
            float zoom,
            int thumbButton,
            bool filterEnabled,
            ColorFilterSettings colorFilter,
            string selectedProfileId)
        {
            Zoom = zoom;
            ThumbButton = thumbButton;
            FilterEnabled = filterEnabled;
            ColorFilter = colorFilter;
            SelectedProfileId = selectedProfileId;
        }

        internal float Zoom { get; set; }
        internal int ThumbButton { get; set; }
        internal bool FilterEnabled { get; set; }
        internal bool StartWithColorEnabled { get; set; } = true;
        internal string FilterMonitorDevice { get; set; } = "";
        internal ColorFilterSettings ColorFilter;
        internal string SelectedProfileId { get; set; }
        internal bool SoundsEnabled { get; set; }
        internal bool ReduceTransparency { get; set; }
        internal int QuietVolumePercent { get; set; } = SettingsStore.DefaultQuietVolumePercent;
        internal HotkeyBindings Bindings { get; set; } = HotkeyBindings.Defaults();
    }
}
