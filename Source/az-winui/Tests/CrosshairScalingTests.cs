using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using AccessibilityZoom;

internal static class CrosshairScalingTests
{
    internal static void Run(Action<bool, string> check, string previewDirectory)
    {
        using Bitmap thin = new(32, 32, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(thin))
        {
            graphics.Clear(Color.Transparent);
            graphics.FillRectangle(Brushes.Lime, 15, 9, 1, 14);
            graphics.FillRectangle(Brushes.Lime, 9, 15, 14, 1);
        }
        double coverage = AlphaSum(thin);
        foreach (float zoom in new[] { 1.25f, 2f, 2.5f, 4f, 8f })
        {
            int side = (int)Math.Ceiling(32 / zoom);
            using Bitmap scaled = StaticCrosshairRasterizer.Resize(thin, side, side);
            double expected = coverage * side * side / (32 * 32);
            check(Math.Abs(AlphaSum(scaled) - expected) <= side * side * .5,
                "Thin crosshair stroke coverage must survive counter-scaling at " + zoom + "x.");
            check(StaticCrosshairRasterizer.HasVisiblePixels(scaled), "Thin crosshair must not vanish at " + zoom + "x.");
            check(scaled.GetPixel(0, 0).A == 0, "Coverage scaling must keep the background transparent.");
        }
        using Bitmap premultiplied = new(8, 8, PixelFormat.Format32bppPArgb);
        premultiplied.SetPixel(3, 3, Color.FromArgb(128, 255, 20, 0));
        using Bitmap half = StaticCrosshairRasterizer.Resize(premultiplied, 4, 4);
        check(Math.Abs(AlphaSum(half) - 32) <= 1, "Premultiplied alpha must be averaged with color coverage.");
        if (previewDirectory == null) return;
        Directory.CreateDirectory(previewDirectory);
        var store = new CrosshairStore();
        var preferences = store.LoadPreferences(out _);
        var assets = store.LoadAssets(out _);
        CrosshairAsset selected = assets.FirstOrDefault(asset => asset.Id == preferences.SelectedId) ?? CrosshairAsset.Default;
        using Bitmap original = StaticCrosshairRasterizer.Render(selected);
        int width = (int)Math.Ceiling(original.Width / 2.5), height = (int)Math.Ceiling(original.Height / 2.5);
        using Bitmap old = new(width, height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(old))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(original, new Rectangle(0, 0, width, height), new Rectangle(0, 0, original.Width, original.Height), GraphicsUnit.Pixel);
        }
        using Bitmap improved = StaticCrosshairRasterizer.Resize(original, width, height);
        using Bitmap sheet = new(780, 300, PixelFormat.Format32bppArgb);
        using Graphics canvas = Graphics.FromImage(sheet);
        canvas.Clear(Color.FromArgb(45, 50, 60));
        using Font font = new("Segoe UI", 11);
        void Draw(Bitmap image, int column, string title)
        {
            canvas.InterpolationMode = InterpolationMode.NearestNeighbor;
            canvas.PixelOffsetMode = PixelOffsetMode.Half;
            canvas.DrawImage(image, new Rectangle(column * 260 + 25, 30, 210, 210), new Rectangle(Point.Empty, image.Size), GraphicsUnit.Pixel);
            canvas.DrawString(title, font, Brushes.White, column * 260 + 15, 260);
        }
        Draw(original, 0, "Original design (enlarged view)");
        Draw(old, 1, "Old 2.5x source: lost strokes");
        Draw(improved, 2, "New 2.5x source: coverage");
        sheet.Save(Path.Combine(previewDirectory, "fixed-size-comparison.png"), ImageFormat.Png);
    }

    private static double AlphaSum(Bitmap bitmap)
    {
        double sum = 0;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++) sum += bitmap.GetPixel(x, y).A;
        return sum;
    }
}
