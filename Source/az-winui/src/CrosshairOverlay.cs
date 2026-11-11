using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;


using Microsoft.UI.Xaml;

namespace AccessibilityZoom
{
    internal sealed class CrosshairOverlay : IDisposable
    {
        private readonly GpuCrosshairSurface surface = new();
        private readonly CrosshairCursor cursor = new();
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

        private readonly GameplayCursorPolicy cursorPolicy = new();
        private readonly DispatcherTimer cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private Bitmap original;
        private Bitmap lastConfiguredSource;
        private readonly Dictionary<Size, Bitmap> scaledCache = new();
        private CrosshairPreferences preferences = new();
        private Rectangle previousSource;
        private Rectangle display;
        private long lastMonitorCheck;
        private bool forceImage;
        private bool disposed;

        internal event Action<string> Failed;

        internal CrosshairOverlay()
        {
            timer.Tick += (_, __) => Refresh(); // Monitor changes and topmost maintenance only.
            cursorTimer.Tick += (_, __) => UpdateCursor();

        }

        internal void Configure(Bitmap bitmap, CrosshairPreferences settings)
        {
            if (disposed) return;
            if (!ReferenceEquals(bitmap, lastConfiguredSource))
            {
                ClearScaledCache();
                Bitmap next = (Bitmap)bitmap.Clone();
                original?.Dispose();
                original = next;
                lastConfiguredSource = bitmap;
            }
            if (preferences.MonitorDevice != settings.MonitorDevice) display = Rectangle.Empty;
            preferences = settings.Clamped();
            forceImage = true;
            lastMonitorCheck = 0;
            if (preferences.Enabled) { Refresh(); if (preferences.Enabled) { timer.Start(); cursorTimer.Start(); } }
            else { timer.Stop(); cursorTimer.Stop(); surface.Hide(); cursor.SetHidden(false); }
        }

        internal void Refresh(bool raise = false)
        {
            if (disposed || original == null || !preferences.Enabled) return;
            try
            {
                bool refreshMonitor = Environment.TickCount64 - lastMonitorCheck >= 1000 || display.IsEmpty;
                if (refreshMonitor)
                {
                    var monitors = DisplayHelper.GetDisplays();
                    display = (monitors.FirstOrDefault(item => item.DeviceName == preferences.MonitorDevice) ??
                        monitors.FirstOrDefault(item => item.Primary))?.Bounds ?? DisplayHelper.GetPrimaryBounds();
                    lastMonitorCheck = Environment.TickCount64;
                }
                if (display.IsEmpty) { surface.Hide(); cursor.SetHidden(false); return; }
                Rectangle destination = ScopeMath.CalculateCrosshairDestination(display, original.Size, preferences.SizePercent);
                Rectangle source = destination; // Preserve original pixels through the GPU overlay path.
                if (forceImage || source.Size != previousSource.Size)
                {
                    Bitmap scaled = GetScaledBitmap(source.Size);
                    surface.SetImage(scaled);
                }
                if (forceImage || source != previousSource)
                    surface.Update(source.Location, (byte)Math.Round(preferences.OpacityPercent * 2.55));
                if (forceImage || source != previousSource || refreshMonitor || raise) surface.Show();
                UpdateCursor();
                previousSource = source;
                forceImage = false;
            }
            catch (Exception exception)
            {
                timer.Stop(); cursorTimer.Stop();
                surface.Hide();
                cursor.SetHidden(false);
                preferences = preferences with { Enabled = false };
                Failed?.Invoke(exception.Message);
            }
        }

        private void UpdateCursor()
        {
            if (disposed) return;
            try { cursor.SetHidden(preferences.Enabled && !display.IsEmpty && cursorPolicy.Sample(display)); }
            catch (Exception exception)
            {
                cursorTimer.Stop();
                cursor.SetHidden(false);
                Failed?.Invoke(exception.Message);
            }
        }

        private Bitmap GetScaledBitmap(Size size)
        {
            if (size == original.Size) return original;
            if (scaledCache.TryGetValue(size, out Bitmap cached)) return cached;
            while (scaledCache.Count > 0 && (scaledCache.Count >= 4 ||
                scaledCache.Keys.Sum(item => (long)item.Width * item.Height) + (long)size.Width * size.Height > 4_194_304))
            {
                Size oldest = scaledCache.Keys.First();
                scaledCache[oldest].Dispose();
                scaledCache.Remove(oldest);
            }
            Bitmap bitmap = StaticCrosshairRasterizer.Resize(original, size.Width, size.Height);
            scaledCache.Add(size, bitmap);
            return bitmap;
        }

        private void ClearScaledCache()
        {
            foreach (Bitmap bitmap in scaledCache.Values) bitmap.Dispose();
            scaledCache.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            try { cursor.Dispose(); }
            finally
            {
                ClearScaledCache();
                timer.Stop(); cursorTimer.Stop(); surface.Dispose(); original?.Dispose();
            }
        }
    }
}
