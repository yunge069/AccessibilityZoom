using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AccessibilityZoom
{
    internal static class CrosshairImporter
    {
        internal const int MaximumJsonBytes = 12 * 1024 * 1024;
        internal const int MaximumImageBytes = 8 * 1024 * 1024;
        private const string ShareEndpoint = "https://8yy0fp6ycd.execute-api.us-east-1.amazonaws.com/Prod/fetchsharedcrosshair?linkId=";
        private static readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20) };
        private static string CrosshairXDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrosshairX");

        internal static string ExtractShareCode(string text)
        {
            string value = (text ?? "").Trim();
            if (Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out Uri link) &&
                (link.Host.Equals("crosshairx.gg", StringComparison.OrdinalIgnoreCase) || link.Host.Equals("www.crosshairx.gg", StringComparison.OrdinalIgnoreCase) ||
                 link.Host.Equals("crosshairx.com", StringComparison.OrdinalIgnoreCase)))
            {
                Match match = Regex.Match(link.AbsolutePath, @"^/s/([A-Za-z0-9_-]{6,32})/?$");
                if (!match.Success) throw new FormatException("Use a Crosshair X share link containing /s/ and its code.");
                return match.Groups[1].Value;
            }
            if (!Regex.IsMatch(value, @"^[A-Za-z0-9_-]{6,32}$"))
                throw new FormatException("Paste a Crosshair X share code or a crosshairx.gg/s/ link.");
            return value;
        }

        internal static string DecodeShareResponse(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object) root = data;
            if (!root.TryGetProperty("crosshairModel", out JsonElement encoded) || encoded.ValueKind != JsonValueKind.String)
                throw new FormatException("That share code was not found or contains no crosshair design.");
            string model = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.GetString()));
            return ExtractModel(model);
        }

        internal static async Task<CrosshairAsset> ImportShareAsync(string input, CancellationToken cancellation = default)
        {
            string code = ExtractShareCode(input);
            byte[] response = await DownloadAsync(new Uri(ShareEndpoint + Uri.EscapeDataString(code)), MaximumJsonBytes * 2, cancellation);
            string model = DecodeShareResponse(Encoding.UTF8.GetString(response));
            model = await EmbedRemoteImagesAsync(model, cancellation);
            return CrosshairAsset.Create("Crosshair X · " + code, "Crosshair X code " + code, model);
        }

        internal static string ExtractModel(string json)
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new FormatException("This crosshair file is too large.");
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("model", out JsonElement model)) root = model;
                else if (root.TryGetProperty("layers", out JsonElement layers)) root = layers;
                else if (root.TryGetProperty("crosshairModel", out _)) return DecodeShareResponse(json);
                else if (root.TryGetProperty("type", out _) || root.TryGetProperty("line", out _))
                { string single = "[" + root.GetRawText() + "]"; ValidateModel(single); return single; }
            }
            string result = root.GetRawText();
            ValidateModel(result);
            return result;
        }

        internal static void ValidateModel(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new FormatException("Crosshair design is missing or too large.");
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root.GetArrayLength() > 128)
                throw new FormatException("A crosshair must contain between 1 and 128 layers.");
            foreach (JsonElement layer in root.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid crosshair layer.");
                string type = layer.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : "model";
                if (type is not ("model" or "image" or "draw" or "shape" or "text"))
                    throw new FormatException("Unsupported crosshair layer: " + type + ". Export it as a PNG instead.");
                ValidateValues(layer);
            }
        }

        private static void ValidateValues(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (property.NameEquals("firingOptions")) continue; // Static base design only.
                    ValidateValues(property.Value);
                    if (property.NameEquals("blur") && property.Value.ValueKind == JsonValueKind.Number && property.Value.GetDouble() > 16)
                        throw new FormatException("This design's blur is too large. Export it as a PNG instead.");
                    if (property.NameEquals("image") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        string image = property.Value.GetString();
                        if (!string.IsNullOrEmpty(image) && !image.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
                            !(Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme == "https"))
                            throw new FormatException("Image layers must contain embedded images or HTTPS image links.");
                    }
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) ValidateValues(item);
            else if (node.ValueKind == JsonValueKind.Number && (!node.TryGetDouble(out double number) || !double.IsFinite(number) || Math.Abs(number) > 8192))
                throw new FormatException("This crosshair has values outside the supported range.");
        }

        internal static CrosshairAsset ImportImage(string path)
        {
            if (new FileInfo(path).Length > MaximumImageBytes) throw new FormatException("Choose an image smaller than 8 MB.");
            byte[] png = NormalizeImage(File.ReadAllBytes(path), out int width, out int height);
            // Keep small reticles at their natural pixel size; fit large art inside 256 px.
            double scale = Math.Min(1, 256.0 / Math.Max(width, height));
            var layer = new
            {
                type = "image", name = "Drawing", image = "data:image/png;base64," + Convert.ToBase64String(png),
                width, height, opacity = 1, rotation = 0, position = new { x = 0, y = 0 }, layer = new { size = scale }
            };
            return CrosshairAsset.Create(Path.GetFileNameWithoutExtension(path), "Image import", JsonSerializer.Serialize(new[] { layer }));
        }

        internal static byte[] NormalizeImage(byte[] data, out int width, out int height)
        {
            if (data.Length > MaximumImageBytes) throw new FormatException("Image is larger than 8 MB.");
            using MemoryStream stream = new(data, false);
            using Image image = Image.FromStream(stream, false, true);
            width = image.Width; height = image.Height;
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 16_000_000)
                throw new FormatException("Images must be at most 4096 pixels per side and 16 million pixels in total.");
            using Bitmap bitmap = new(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.DrawImage(image, new Rectangle(0, 0, width, height));
            using MemoryStream png = new();
            bitmap.Save(png, ImageFormat.Png); // GIFs use their static first frame.
            return png.ToArray();
        }

        internal static List<LocalCrosshair> FindLocalDesigns(string directory = null)
        {
            string root = directory ?? CrosshairXDirectory;
            List<LocalCrosshair> result = new();
            string preferences = Path.Combine(root, "crosshairx-user-preferences.json");
            double nativeScale = 1;
            if (File.Exists(preferences))
            {
                try
                {
                    if (new FileInfo(preferences).Length <= MaximumJsonBytes)
                    {
                        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(preferences), new JsonDocumentOptions { MaxDepth = 32 });
                        // Crosshair X draws with the ACTIVE PROFILE's crosshairSize
                        // (profiles\<currentProfileId>.json). The top-level value is a
                        // stale pre-profile copy and is only a fallback.
                        if (TryReadScale(saved.RootElement, out double scale)) nativeScale = scale;
                        if (TryReadProfileScale(root, saved.RootElement, out double profileScale)) nativeScale = profileScale;
                        if (saved.RootElement.TryGetProperty("persistentCrosshair", out var persistent) &&
                            persistent.TryGetProperty("crosshairImage", out var image) && image.TryGetProperty("model", out _))
                            result.Add(new LocalCrosshair("Current Crosshair X crosshair", preferences, true, nativeScale));
                    }
                }
                catch { /* Saved files remain importable if the current-design cache is absent. */ }
            }
            string store = Path.Combine(root, "crosshair-store");
            if (!Directory.Exists(store)) return result;
            int index = 0;
            foreach (string file in Directory.EnumerateFiles(store, "*.json").Where(p => Path.GetFileName(p) != "manifest.json")
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
            {
                index++;
                try
                {
                    if (new FileInfo(file).Length > MaximumJsonBytes) continue;
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 32 });
                    if (!document.RootElement.TryGetProperty("model", out _)) continue;
                    string name = document.RootElement.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                    result.Add(new LocalCrosshair(string.IsNullOrWhiteSpace(name) || name == "Untitled" ? "Saved crosshair " + index.ToString("00") : name, file, false, nativeScale));
                }
                catch { /* Report individual import errors after selection; leave other designs available. */ }
            }
            return result;
        }

        // Stale top-level size and the active profile's size, if both are readable.
        internal static bool TryReadCrosshairXScales(out double staleScale, out double profileScale, string directory = null)
        {
            staleScale = profileScale = 1;
            string root = directory ?? CrosshairXDirectory;
            string preferences = Path.Combine(root, "crosshairx-user-preferences.json");
            try
            {
                if (!File.Exists(preferences) || new FileInfo(preferences).Length > MaximumJsonBytes) return false;
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(preferences), new JsonDocumentOptions { MaxDepth = 32 });
                return TryReadScale(saved.RootElement, out staleScale) && TryReadProfileScale(root, saved.RootElement, out profileScale);
            }
            catch { return false; }
        }

        private static bool TryReadScale(JsonElement element, out double scale)
        {
            scale = 1;
            return element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("crosshairSize", out var size) && size.ValueKind == JsonValueKind.Number &&
                size.TryGetDouble(out scale) && double.IsFinite(scale) && scale > 0 && scale <= 16;
        }

        private static bool TryReadProfileScale(string root, JsonElement preferences, out double scale)
        {
            scale = 1;
            string id = preferences.TryGetProperty("currentProfileId", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : "default";
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                id.Contains("..")) return false;
            string file = Path.Combine(root, "profiles", id + ".json");
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > MaximumJsonBytes) return false;
                using JsonDocument profile = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 32 });
                return TryReadScale(profile.RootElement, out scale);
            }
            catch { return false; }
        }

        internal static async Task<CrosshairAsset> ImportLocalAsync(LocalCrosshair design, CancellationToken cancellation = default)
        {
            if (new FileInfo(design.Path).Length > MaximumJsonBytes) throw new FormatException("This crosshair file is too large.");
            string text = await File.ReadAllTextAsync(design.Path, cancellation);
            if (design.Current)
            {
                using JsonDocument preferences = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                text = preferences.RootElement.GetProperty("persistentCrosshair").GetProperty("crosshairImage").GetProperty("model").GetRawText();
            }
            string model = await EmbedRemoteImagesAsync(ExtractModel(text), cancellation);
            return CrosshairAsset.Create(design.Name, "Crosshair X library", model, design.NativeScale);
        }

        private static async Task<string> EmbedRemoteImagesAsync(string model, CancellationToken cancellation)
        {
            JsonNode node = JsonNode.Parse(model);
            async Task Visit(JsonNode value)
            {
                if (value is JsonObject obj)
                {
                    foreach (var item in obj.ToList())
                    {
                        if (item.Key == "firingOptions") { obj.Remove(item.Key); continue; }
                        if (item.Key == "image" && item.Value is JsonValue scalar && scalar.TryGetValue<string>(out string image) &&
                            Uri.TryCreate(image, UriKind.Absolute, out Uri uri) && uri.Scheme == "https")
                        {
                            byte[] bytes = await DownloadAsync(uri, MaximumImageBytes, cancellation);
                            byte[] png = NormalizeImage(bytes, out _, out _);
                            obj[item.Key] = "data:image/png;base64," + Convert.ToBase64String(png);
                        }
                        else if (item.Value != null) await Visit(item.Value);
                    }
                }
                else if (value is JsonArray array) foreach (JsonNode item in array) if (item != null) await Visit(item);
            }
            await Visit(node);
            string result = node.ToJsonString();
            ValidateModel(result);
            return result;
        }

        private static async Task<byte[]> DownloadAsync(Uri uri, int maximumBytes, CancellationToken cancellation, int redirects = 0)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.IsLoopback)
                throw new FormatException("Only public HTTPS crosshair resources can be imported.");
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellation);
            if (addresses.Length == 0 || addresses.Any(IsPrivate)) throw new FormatException("Crosshair resources must use a public internet address.");
            using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location != null && redirects < 3)
                return await DownloadAsync(new Uri(uri, response.Headers.Location), maximumBytes, cancellation, redirects + 1);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new FormatException("That Crosshair X code or image was not found.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes) throw new FormatException("Crosshair download is too large.");
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
            using MemoryStream data = new();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellation)) > 0)
            {
                if (data.Length + read > maximumBytes) throw new FormatException("Crosshair download is too large.");
                data.Write(buffer, 0, read);
            }
            return data.ToArray();
        }

        private static bool IsPrivate(IPAddress address)
        {
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length == 16) return (bytes[0] & 0xFE) == 0xFC;
            return bytes[0] is 0 or 10 or 127 or >= 224 || bytes[0] == 169 && bytes[1] == 254 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
        }
    }
}
