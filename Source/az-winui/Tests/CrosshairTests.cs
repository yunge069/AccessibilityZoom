using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AccessibilityZoom;

internal static class CrosshairTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool live, string previewDirectory = null)
    {
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch { rejected = true; }
            check(rejected, message);
        }
        check(HotkeyBindings.Defaults()[HotkeyAction.ToggleCrosshair] == new InputBinding(0x76), "F7 must toggle the crosshair.");
        var router = new HotkeyStateMachine(HotkeyBindings.Defaults());
        router.Process(0x76, true, InputModifiers.None, 10, out var press);
        check(press?.Action == HotkeyAction.ToggleCrosshair, "Crosshair toggle must reach the input router.");
        check(CrosshairImporter.ExtractShareCode("  xe4lbu6zh8  ") == "xe4lbu6zh8", "Plain Crosshair X code must parse.");
        check(CrosshairImporter.ExtractShareCode("https://crosshairx.gg/s/xe4lbu6zh8?shared=1") == "xe4lbu6zh8", "Crosshair X link must parse.");
        check(CrosshairImporter.ExtractShareCode("crosshairx.gg/s/xe4lbu6zh8") == "xe4lbu6zh8", "Scheme-free Crosshair X link must parse.");
        foreach (string text in new[] { "", "no", "https://example.com/s/xe4lbu6zh8", "https://crosshairx.gg.evil.com/s/xe4lbu6zh8", "https://crosshairx.gg/not-a-share" })
            Reject(() => CrosshairImporter.ExtractShareCode(text), "Non-Crosshair X input must be rejected.");
        string model = CrosshairAsset.Default.ModelJson;
        string response = JsonSerializer.Serialize(new { crosshairModel = Convert.ToBase64String(Encoding.UTF8.GetBytes(model)) });
        check(CrosshairImporter.DecodeShareResponse(response) == model, "Share response must decode to layer data.");
        check(CrosshairImporter.DecodeShareResponse("{\"data\":" + response + "}") == model, "Wrapped share response must decode.");
        check(CrosshairImporter.ExtractModel("{\"name\":\"Local\",\"model\":" + model + "}") == model, "Local Crosshair X JSON must extract its model array.");
        foreach (string invalid in new[] { "[]", "{}", "[null]", "[{\"type\":\"script\"}]", "[{\"type\":\"image\",\"image\":\"file:///C:/private.png\"}]", "[{\"line\":{\"length\":1000000}}]" })
            Reject(() => CrosshairImporter.ValidateModel(invalid), "Malformed/unsupported design must be rejected.");
        Reject(() => CrosshairImporter.DecodeShareResponse("{\"message\":\"not found\"}"), "Missing share code must produce a useful error.");
        Reject(() => CrosshairImporter.DecodeShareResponse("{\"crosshairModel\":\"not base64\"}"), "Bad share encoding must be rejected.");

        using (Bitmap defaultFrame = StaticCrosshairRasterizer.Render(CrosshairAsset.Default))
        {
            check(StaticCrosshairRasterizer.HasVisiblePixels(defaultFrame), "Built-in crosshair must render.");
            check(defaultFrame.GetPixel(0, 0).A == 0, "Crosshair background must remain transparent.");
            check(defaultFrame.GetPixel(defaultFrame.Width / 2, defaultFrame.Height / 2).A == 0, "Built-in crosshair gap must stay transparent.");
        }
        // Animation targets must not enlarge or change the static base design.
        string animated = model.Replace("\"position\":{\"x\":0,\"y\":0}", "\"firingOptions\":{\"position\":{\"x\":8000,\"y\":8000}},\"position\":{\"x\":0,\"y\":0}");
        using (Bitmap baseline = StaticCrosshairRasterizer.Render(CrosshairAsset.Default))
        using (Bitmap staticFrame = StaticCrosshairRasterizer.Render(CrosshairAsset.Create("Static", "Test", animated)))
            check(baseline.Size == staticFrame.Size, "Firing animations must not affect static canvas size.");
        foreach (string shape in new[] { "square", "circle", "diamond", "ring" })
        {
            string dot = "[{\"type\":\"model\",\"dot\":{\"diameter\":8,\"opacity\":1,\"color\":\"#FF4400\",\"shape\":\"" + shape + "\",\"thickness\":2},\"outline\":{\"opacity\":0}}]";
            using Bitmap bitmap = StaticCrosshairRasterizer.Render(CrosshairAsset.Create(shape, "Test", dot));
            check(StaticCrosshairRasterizer.HasVisiblePixels(bitmap), shape + " must render as a static dot shape.");
        }
        Reject(() => StaticCrosshairRasterizer.Render(CrosshairAsset.Create("Invisible", "Test", "[{\"type\":\"model\"}]")), "Invisible base designs must report an error.");
        Reject(() => StaticCrosshairRasterizer.Render(CrosshairAsset.Create("Bad image", "Test", "[{\"type\":\"image\",\"image\":\"data:image/png;base64,notpng\"}]")), "Bad image layers must not silently disappear.");

        string directory = Path.Combine(Path.GetTempPath(), "az-crosshair-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "test.png");
            using (Bitmap image = new(5, 5, PixelFormat.Format32bppArgb))
            {
                image.SetPixel(2, 2, Color.FromArgb(255, 0, 255, 0));
                image.SetPixel(1, 2, Color.FromArgb(128, 255, 0, 0));
                image.Save(imagePath, ImageFormat.Png);
            }
            CrosshairAsset imageAsset = CrosshairImporter.ImportImage(imagePath);
            File.Delete(imagePath);
            using (Bitmap image = StaticCrosshairRasterizer.Render(imageAsset))
            {
                check(StaticCrosshairRasterizer.HasVisiblePixels(image), "Image import must survive removal of the original file.");
                check(image.GetPixel(0, 0).A == 0, "Imported PNG transparency must survive rendering.");
                check(ContainsAlpha(image, 128), "Partial alpha must survive PNG import and rendering.");
                using Bitmap scaled = StaticCrosshairRasterizer.Resize(image, 64, 64);
                check(scaled.Size == new Size(64, 64) && scaled.GetPixel(0, 0).A == 0, "Crosshair resizing must preserve transparency.");
            }
            CrosshairStore store = new(Path.Combine(directory, "library"));
            store.SaveAsset(imageAsset);
            store.SaveAsset(imageAsset);
            var preferences = new CrosshairPreferences { SelectedId = imageAsset.Id, Enabled = true, SizePercent = 175,
                OpacityPercent = 65, MonitorDevice = @"\\.\DISPLAY2" };
            store.SavePreferences(preferences);
            var restored = store.LoadAssets(out string warning);
            check(restored.Count == 2 && warning == "", "Importing the same design twice must not duplicate it.");
            check(restored.Single(asset => asset.Id == imageAsset.Id).ModelJson == imageAsset.ModelJson, "Imported image/model data must survive restart.");
            check(store.LoadPreferences(out warning) == preferences && warning == "", "Crosshair selection, enable state, size, opacity, and monitor must persist.");
            File.WriteAllText(Path.Combine(store.DirectoryPath, "broken.crosshair.json"), "not json");
            check(store.LoadAssets(out warning).Count == 2 && warning.Length > 0, "One damaged design must not hide the rest of the library.");
            File.WriteAllText(Path.Combine(store.DirectoryPath, "settings.json"), "bad");
            check(!store.LoadPreferences(out warning).Enabled && warning.Length > 0, "Damaged settings must fall back to a disabled crosshair.");
            var clamped = (preferences with { SizePercent = -50, OpacityPercent = 900 }).Clamped();
            check(clamped.SizePercent == 25 && clamped.OpacityPercent == 100, "Invalid crosshair settings must be clamped.");

            string local = Path.Combine(directory, "CrosshairX");
            Directory.CreateDirectory(Path.Combine(local, "crosshair-store"));
            File.WriteAllText(Path.Combine(local, "crosshair-store", "example.json"), "{\"name\":\"Saved dot\",\"model\":" + imageAsset.ModelJson + "}");
            File.WriteAllText(Path.Combine(local, "crosshair-store", "manifest.json"), "{\"handles\":[]}");
            var candidates = CrosshairImporter.FindLocalDesigns(local);
            check(candidates.Count == 1 && candidates[0].Name == "Saved dot", "Local library discovery must exclude the manifest.");
            CrosshairAsset localAsset = await CrosshairImporter.ImportLocalAsync(candidates[0]);
            using (Bitmap localImage = StaticCrosshairRasterizer.Render(localAsset))
                check(StaticCrosshairRasterizer.HasVisiblePixels(localImage), "Local Crosshair X library import must render.");
            File.WriteAllText(Path.Combine(local, "crosshairx-user-preferences.json"),
                "{\"crosshairSize\":0.72,\"persistentCrosshair\":{\"crosshairImage\":{\"model\":" + imageAsset.ModelJson + "}}}");
            var scaledCandidates = CrosshairImporter.FindLocalDesigns(local);
            check(scaledCandidates.Count == 2 && scaledCandidates.All(item => item.NativeScale == .72), "Current and saved designs must retain Crosshair X's global scale.");
            CrosshairAsset currentAsset = await CrosshairImporter.ImportLocalAsync(scaledCandidates.First(item => item.Current));
            check(currentAsset.NativeScale == .72 && currentAsset.Id != imageAsset.Id, "Imported scale must be retained and distinguish differently sized copies.");
            store.SaveAsset(currentAsset);
            check(store.LoadAssets(out _).Single(item => item.Id == currentAsset.Id).NativeScale == .72, "Imported source scale must survive restart.");
            // One-time repair rescales stale Crosshair X imports and drops duplicates.
            CrosshairAsset fullSize = CrosshairAsset.Create(currentAsset.Name, currentAsset.Source, currentAsset.ModelJson, 1);
            var repaired = store.RepairCrosshairXScale(.72, 1);
            var afterRepair = store.LoadAssets(out _);
            check(repaired.TryGetValue(currentAsset.Id, out string repairedId) && repairedId == fullSize.Id &&
                afterRepair.All(item => item.Id != currentAsset.Id) && afterRepair.Single(item => item.Id == fullSize.Id).NativeScale == 1,
                "Stale 72% Crosshair X imports must be rescaled to the profile size.");
            store.SaveAsset(currentAsset);
            check(store.RepairCrosshairXScale(.72, 1).Count == 0 && store.LoadAssets(out _).Any(item => item.Id == currentAsset.Id),
                "The scale repair must run only once.");
            store.DeleteAsset(currentAsset.Id);
            check(store.LoadAssets(out _).All(item => item.Id != currentAsset.Id), "Deleted crosshairs must stay deleted after restart.");
            Reject(() => store.DeleteAsset(CrosshairAsset.Default.Id), "The built-in crosshair must not be deletable.");
            // The active profile's size wins over the stale top-level value, as in Crosshair X.
            Directory.CreateDirectory(Path.Combine(local, "profiles"));
            File.WriteAllText(Path.Combine(local, "crosshairx-user-preferences.json"),
                "{\"crosshairSize\":0.72,\"currentProfileId\":\"default\",\"persistentCrosshair\":{\"crosshairImage\":{\"model\":" + imageAsset.ModelJson + "}}}");
            File.WriteAllText(Path.Combine(local, "profiles", "default.json"), "{\"id\":\"default\",\"crosshairSize\":1}");
            check(CrosshairImporter.FindLocalDesigns(local).All(item => item.NativeScale == 1), "The active Crosshair X profile size must replace the legacy top-level size.");
            File.WriteAllText(Path.Combine(local, "profiles", "default.json"), "{\"id\":\"default\",\"crosshairSize\":1.5}");
            check(CrosshairImporter.FindLocalDesigns(local).All(item => item.NativeScale == 1.5), "A non-default profile size must be retained.");
            File.WriteAllText(Path.Combine(local, "crosshairx-user-preferences.json"),
                "{\"crosshairSize\":0.72,\"currentProfileId\":\"..\\\\evil\",\"persistentCrosshair\":{\"crosshairImage\":{\"model\":" + imageAsset.ModelJson + "}}}");
            check(CrosshairImporter.FindLocalDesigns(local).All(item => item.NativeScale == .72), "Unsafe profile ids must be ignored.");

            // Exercise the real Win32 alpha-surface path while the window remains hidden.
            using (CrosshairSurface surface = new())
            using (Bitmap pixels = StaticCrosshairRasterizer.Render(imageAsset))
            {
                surface.SetImage(pixels);
                surface.Update(new Point(10, 10), 180);
                check(!NativeMethods.IsWindowVisible(surface.Handle), "Native crosshair surface must remain hidden until explicitly enabled.");
                surface.SetImage(pixels); // Replacing the DIB must release its prior GDI resources.
                surface.Update(new Point(-100, 10), 255);
                check(!NativeMethods.IsWindowVisible(surface.Handle), "Moving a hidden surface must not show or activate it.");
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                uint before = GetGuiResources(process.Handle, 0);
                var updateWatch = System.Diagnostics.Stopwatch.StartNew();
                for (int replacement = 0; replacement < 50; replacement++)
                {
                    surface.SetImage(pixels);
                    surface.Update(new Point(-100 + replacement, 10), (byte)(100 + replacement));
                }
                Console.WriteLine("Hidden crosshair bitmap replacement/update: " +
                    (updateWatch.Elapsed.TotalMilliseconds * 1000 / 50).ToString("F1") + " microseconds average (not a game FPS measurement).");
                check(GetGuiResources(process.Handle, 0) <= before + 1, "Replacing/moving the overlay must not leak GDI handles.");
            }
            using (GpuCrosshairSurface surface = new())
            using (Bitmap pixels = StaticCrosshairRasterizer.Render(imageAsset))
            {
                surface.SetImage(pixels);
                surface.Update(new Point(20, 20), 255);
                check(!NativeMethods.IsWindowVisible(surface.Handle), "GPU crosshair self-test must keep its window hidden.");
                int styles = NativeMethods.GetWindowLong(surface.Handle, -20);
                check((styles & (NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT)) ==
                    (NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT),
                    "GPU crosshair must pass mouse input through to other processes.");
                check(surface.PixelSize == pixels.Size && surface.ContentUploads == 1,
                    "GPU overlay must upload the original crosshair pixels once.");
                for (int i = 0; i < 50; i++) surface.Update(new Point(20 + i, 20), 255);
                check(surface.ContentUploads == 1, "Moving/maintaining the GPU overlay must not present or upload frames.");
                DesktopTransformState.Publish(2.5f, 480, 270);
                surface.Update(new Point(20, 20), 255);
                check(surface.PixelSize == pixels.Size && surface.ContentUploads == 1,
                    "Zoom changes must not downsample or replace the GPU crosshair image.");
                DesktopTransformState.Publish(1, 0, 0);
                surface.Update(new Point(20, 20), 128);
                check(surface.ContentUploads == 2, "Changing opacity must update the alpha texture.");
                using var bigger = StaticCrosshairRasterizer.Resize(pixels, 64, 64);
                surface.SetImage(bigger);
                surface.Update(new Point(-100, 0), 255);
                check(surface.PixelSize == new Size(64, 64), "GPU surface must resize for crosshair size edits.");
                Console.WriteLine("GPU crosshair: hidden alpha presentation passed; capture exclusion=" + surface.CaptureExcluded + "; no uploads during 50 idle/move updates.");
            }
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("az-crosshair-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a test directory outside its expected temp location.");
            Directory.Delete(resolved, true);
        }

        foreach (Rectangle screen in new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(-2560, -200, 2560, 1440), new Rectangle(1920, 0, 3840, 2160) })
        foreach (int percent in new[] { 25, 100, 175, 400 })
        foreach (float zoom in new[] { 1f, 1.25f, 2f, 4f, 8f })
        {
            Rectangle destination = ScopeMath.CalculateCrosshairDestination(screen, new Size(48, 32), percent);
            check(Math.Abs(destination.Left + destination.Width / 2.0 - (screen.Left + screen.Width / 2.0)) <= .5 &&
                Math.Abs(destination.Top + destination.Height / 2.0 - (screen.Top + screen.Height / 2.0)) <= .5, "Crosshair must center on each monitor at every size.");
            Point offset = ScopeMath.CalculateFixedCenterOffset(screen, zoom);
            Rectangle source = ScopeMath.CalculateOverlaySourceBounds(destination, zoom, offset);
            check(Math.Abs((source.X - offset.X) * zoom - destination.X) <= zoom / 2,
                "Crosshair must compensate for the applied fullscreen transform.");
        }

        if (live)
        {
            if (previewDirectory != null) Directory.CreateDirectory(previewDirectory);
            CrosshairAsset shared = await CrosshairImporter.ImportShareAsync("xe4lbu6zh8");
            using Bitmap sharedFrame = StaticCrosshairRasterizer.Render(shared);
            check(StaticCrosshairRasterizer.HasVisiblePixels(sharedFrame), "Public Crosshair X share code must download, decode, and render.");
            if (previewDirectory != null) sharedFrame.Save(Path.Combine(previewDirectory, "public-share.png"), ImageFormat.Png);
            var designs = CrosshairImporter.FindLocalDesigns();
            int index = 0;
            using Bitmap sheet = new(960, (int)Math.Ceiling((designs.Count + 1) / 6.0) * 150, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(sheet);
            graphics.Clear(Color.FromArgb(35, 40, 50));
            using Font font = new("Segoe UI", 9);
            void AddPreview(Bitmap frame, string label)
            {
                int x = index % 6 * 160, y = index / 6 * 150;
                graphics.FillRectangle(Brushes.DimGray, x + 5, y + 5, 150, 115);
                double scale = Math.Min(3, Math.Min(130.0 / frame.Width, 100.0 / frame.Height));
                int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
                int height = Math.Max(1, (int)Math.Round(frame.Height * scale));
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(frame, new Rectangle(x + 80 - width / 2, y + 62 - height / 2, width, height));
                graphics.DrawString(label, font, Brushes.White, x + 8, y + 126);
                index++;
            }
            AddPreview(sharedFrame, "Public share code");
            foreach (var design in designs)
            {
                CrosshairAsset asset = await CrosshairImporter.ImportLocalAsync(design);
                using Bitmap frame = StaticCrosshairRasterizer.Render(asset);
                check(StaticCrosshairRasterizer.HasVisiblePixels(frame), "Existing Crosshair X design must import and render: " + design.Name);
                if (previewDirectory != null) frame.Save(Path.Combine(previewDirectory, "local-" + index.ToString("00") + ".png"), ImageFormat.Png);
                AddPreview(frame, design.Current ? "Current crosshair" : "Saved design " + index.ToString("00"));
            }
            if (previewDirectory != null) sheet.Save(Path.Combine(previewDirectory, "render-check.png"), ImageFormat.Png);
            Console.WriteLine("Live compatibility checks: downloaded a public share code and rendered " + designs.Count + " local designs (including the current crosshair).");
        }
    }

    private static bool ContainsAlpha(Bitmap bitmap, byte alpha)
    {
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
            if (Math.Abs(bitmap.GetPixel(x, y).A - alpha) <= 1) return true;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
