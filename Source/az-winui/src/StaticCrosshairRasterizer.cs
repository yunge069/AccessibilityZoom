using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using Reticly.Core;

namespace AccessibilityZoom
{
    internal static class StaticCrosshairRasterizer
    {
        internal static Bitmap Render(CrosshairAsset asset)
        {
            CrosshairImporter.ValidateModel(asset.ModelJson);
            if (!double.IsFinite(asset.NativeScale) || asset.NativeScale <= 0 || asset.NativeScale > 16)
                throw new FormatException("Invalid imported crosshair scale.");
            List<object> layers = (List<object>)Json.Parse(asset.ModelJson);
            foreach (var layer in layers.OfType<Dictionary<string, object>>())
                layer.Remove("firingOptions");
            Defaults.NormalizeLayers(layers);
            using var images = Reticly.Render.ImageCache.OpenScope();
            void DecodeImages(object value)
            {
                if (value is Dictionary<string, object> obj)
                {
                    foreach (var item in obj)
                    {
                        if (item.Key == "image" && item.Value is string image && !string.IsNullOrEmpty(image))
                            Reticly.Render.ImageCache.Get(image);
                        else DecodeImages(item.Value);
                    }
                }
                else if (value is List<object> list) foreach (object item in list) DecodeImages(item);
            }
            DecodeImages(layers); // Surface bad image layers instead of silently omitting them.
            int half = Reticly.Render.CrosshairRenderer.ComputeCanvasHalf(layers, asset.NativeScale);
            double scale = asset.NativeScale * (half > 512 ? 512.0 / half : 1);
            using var frame = Reticly.Render.CrosshairRenderer.Render(layers, scale, 0, Math.Min(half, 512));
            if (!HasVisiblePixels(frame.Bitmap)) throw new FormatException("This design has no visible static pixels. Try a different design or export its visible state as a PNG.");
            return (Bitmap)frame.Bitmap.Clone();
        }

        internal static bool HasVisiblePixels(Bitmap bitmap)
        {
            Rectangle rectangle = new(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte[] pixels = new byte[Math.Abs(data.Stride) * data.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) return true;
                return false;
            }
            finally { bitmap.UnlockBits(data); }
        }

        internal static Bitmap Resize(Bitmap original, int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width < original.Width || height < original.Height) return ResizeByCoverage(original, width, height);
            Bitmap result = new(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppPArgb);
            using Graphics graphics = Graphics.FromImage(result);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(original, new Rectangle(0, 0, result.Width, result.Height), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel);
            return result;
        }

        // Average premultiplied coverage instead of picking one source pixel.
        // A one-pixel arm/outline must contribute even when it falls between samples.
        private static Bitmap ResizeByCoverage(Bitmap original, int width, int height)
        {
            Bitmap result = new(width, height, PixelFormat.Format32bppPArgb);
            BitmapData input = original.LockBits(new Rectangle(Point.Empty, original.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            BitmapData output = result.LockBits(new Rectangle(Point.Empty, result.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte[] source = new byte[Math.Abs(input.Stride) * input.Height];
                byte[] destination = new byte[Math.Abs(output.Stride) * output.Height];
                System.Runtime.InteropServices.Marshal.Copy(input.Scan0, source, 0, source.Length);
                double stepX = original.Width / (double)width, stepY = original.Height / (double)height;
                double area = stepX * stepY;
                for (int y = 0; y < height; y++)
                {
                    double top = y * stepY, bottom = (y + 1) * stepY;
                    int firstY = (int)Math.Floor(top), lastY = Math.Min(original.Height - 1, (int)Math.Ceiling(bottom) - 1);
                    for (int x = 0; x < width; x++)
                    {
                        double left = x * stepX, right = (x + 1) * stepX;
                        int firstX = (int)Math.Floor(left), lastX = Math.Min(original.Width - 1, (int)Math.Ceiling(right) - 1);
                        double blue = 0, green = 0, red = 0, alpha = 0;
                        for (int sy = firstY; sy <= lastY; sy++)
                        {
                            double weightY = Math.Min(bottom, sy + 1) - Math.Max(top, sy);
                            for (int sx = firstX; sx <= lastX; sx++)
                            {
                                double weight = weightY * (Math.Min(right, sx + 1) - Math.Max(left, sx));
                                int index = sy * input.Stride + sx * 4;
                                blue += source[index] * weight;
                                green += source[index + 1] * weight;
                                red += source[index + 2] * weight;
                                alpha += source[index + 3] * weight;
                            }
                        }
                        int target = y * output.Stride + x * 4;
                        destination[target] = (byte)Math.Clamp((int)Math.Round(blue / area), 0, 255);
                        destination[target + 1] = (byte)Math.Clamp((int)Math.Round(green / area), 0, 255);
                        destination[target + 2] = (byte)Math.Clamp((int)Math.Round(red / area), 0, 255);
                        destination[target + 3] = (byte)Math.Clamp((int)Math.Round(alpha / area), 0, 255);
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(destination, 0, output.Scan0, destination.Length);
            }
            finally { original.UnlockBits(input); result.UnlockBits(output); }
            return result;
        }
    }
}

namespace Reticly.Render
{
    // Adapter for the MIT renderer: imported images are embedded, bounded and
    // static. It never reads arbitrary local files or makes network requests.
    internal sealed class CachedImage : IDisposable
    {
        private readonly Bitmap image;
        internal bool Failed => image == null;
        internal bool IsAnimated => false;
        internal CachedImage(Bitmap image) { this.image = image; }
        internal Bitmap FrameAt(long ignored) => image;
        public void Dispose() => image?.Dispose();
    }

    internal static class ImageCache
    {
        [ThreadStatic] private static Dictionary<string, CachedImage> cache;

        internal static IDisposable OpenScope()
        {
            if (cache != null) throw new InvalidOperationException("Nested image decoding scope.");
            cache = new Dictionary<string, CachedImage>();
            return new Scope();
        }

        internal static CachedImage Get(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (cache == null) throw new InvalidOperationException("Image decoding requires a render scope.");
            if (cache.TryGetValue(value, out CachedImage existing)) return existing;
            if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Import this crosshair again to download its image layers.");
            int comma = value.IndexOf(',');
            if (comma < 0 || !value.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || value.Length > 12 * 1024 * 1024)
                throw new FormatException("This image layer is not a supported embedded bitmap.");
            byte[] bytes = Convert.FromBase64String(value.Substring(comma + 1));
            byte[] png = AccessibilityZoom.CrosshairImporter.NormalizeImage(bytes, out _, out _);
            using MemoryStream stream = new(png, false);
            using Image decoded = Image.FromStream(stream);
            var result = new CachedImage(new Bitmap(decoded));
            cache.Add(value, result);
            return result;
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
                foreach (CachedImage image in cache.Values) image.Dispose();
                cache = null;
            }
        }
    }
}
