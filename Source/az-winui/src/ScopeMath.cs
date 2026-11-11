using System;
using System.Drawing;

namespace AccessibilityZoom
{
    internal static class ScopeMath
    {
        internal static Point CalculateOffset(Point cursor, Rectangle virtualScreen, float zoom)
        {
            if (zoom <= 1.0f)
            {
                return Point.Empty;
            }

            double factor = zoom;
            double destinationCenterX = virtualScreen.Left + (virtualScreen.Width / 2.0);
            double destinationCenterY = virtualScreen.Top + (virtualScreen.Height / 2.0);

            double wantedX = cursor.X - (destinationCenterX / factor);
            double wantedY = cursor.Y - (destinationCenterY / factor);

            // The full-screen transform is relative to the primary screen's origin.
            // These bounds keep the sampled source rectangle inside the virtual desktop.
            double minimumX = virtualScreen.Left - (virtualScreen.Left / factor);
            double minimumY = virtualScreen.Top - (virtualScreen.Top / factor);
            double right = virtualScreen.Right;
            double bottom = virtualScreen.Bottom;
            double maximumX = right - (right / factor);
            double maximumY = bottom - (bottom / factor);

            int x = RoundAndClamp(wantedX, minimumX, maximumX);
            int y = RoundAndClamp(wantedY, minimumY, maximumY);
            return new Point(x, y);
        }

        internal static Point CalculateFixedCenterOffset(Rectangle display, float zoom)
        {
            if (zoom <= 1.0f)
            {
                return Point.Empty;
            }

            double centerX = display.Left + (display.Width / 2.0);
            double centerY = display.Top + (display.Height / 2.0);
            double scale = 1.0 - (1.0 / zoom);
            return new Point(
                (int)Math.Round(centerX * scale, MidpointRounding.AwayFromZero),
                (int)Math.Round(centerY * scale, MidpointRounding.AwayFromZero));
        }

        private static int RoundAndClamp(double value, double minimum, double maximum)
        {
            double clamped = Math.Max(minimum, Math.Min(maximum, value));
            return (int)Math.Round(clamped, MidpointRounding.AwayFromZero);
        }

        // Place an overlay in desktop source coordinates so Windows' full-screen
        // transform still draws it at the requested physical screen position.
        internal static Rectangle CalculateOverlaySourceBounds(Rectangle destination, float zoom, Point offset)
        {
            if (!float.IsFinite(zoom) || zoom < 1) zoom = 1;
            if (zoom == 1) return destination;
            return new Rectangle(
                (int)Math.Round(offset.X + destination.X / (double)zoom),
                (int)Math.Round(offset.Y + destination.Y / (double)zoom),
                Math.Max(1, (int)Math.Ceiling(destination.Width / (double)zoom)),
                Math.Max(1, (int)Math.Ceiling(destination.Height / (double)zoom)));
        }

        internal static Rectangle CalculateCrosshairDestination(Rectangle display, Size bitmap, int sizePercent)
        {
            double scale = Math.Clamp(sizePercent, 25, 400) / 100.0;
            scale = Math.Min(scale, 2048.0 / Math.Max(1, Math.Max(bitmap.Width, bitmap.Height)));
            int width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
            int height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
            return new Rectangle(display.Left + (display.Width - width) / 2,
                display.Top + (display.Height - height) / 2, width, height);
        }

        internal static bool RunSelfTest()
        {
            Rectangle oneScreen = new Rectangle(0, 0, 1920, 1080);
            if (CalculateOffset(new Point(960, 540), oneScreen, 2.0f) != new Point(480, 270))
            {
                return false;
            }

            if (CalculateOffset(new Point(0, 0), oneScreen, 2.0f) != Point.Empty)
            {
                return false;
            }

            if (CalculateOffset(new Point(1919, 1079), oneScreen, 2.0f) != new Point(960, 540))
            {
                return false;
            }

            Rectangle dualScreen = new Rectangle(-1920, 0, 3840, 1080);
            if (CalculateOffset(new Point(-960, 540), dualScreen, 2.0f) != new Point(-960, 270))
            {
                return false;
            }

            if (CalculateOffset(new Point(960, 540), dualScreen, 2.0f) != new Point(960, 270))
            {
                return false;
            }

            if (CalculateOffset(new Point(123, 456), dualScreen, 1.0f) != Point.Empty)
            {
                return false;
            }

            if (CalculateFixedCenterOffset(oneScreen, 2.0f) != new Point(480, 270))
            {
                return false;
            }

            Rectangle rightScreen = new Rectangle(1920, 0, 2560, 1440);
            if (CalculateFixedCenterOffset(rightScreen, 2.0f) != new Point(1600, 360))
            {
                return false;
            }

            if (CalculateFixedCenterOffset(oneScreen, 1.0f) != Point.Empty)
            {
                return false;
            }

            return ColorProfileStore.RunSelfTest() &&
                ColorFilterSettings.General.DetectPreset() == 0 &&
                ColorFilterSettings.Visibility.DetectPreset() == 1 &&
                ColorFilterSettings.Night.DetectPreset() == 2 &&
                ColorFilterSettings.Gaming.DetectPreset() == 3 &&
                ColorFilterSettings.Visibility.NearlyEquals(new ColorFilterSettings
                {
                    Gamma = 1.25f,
                    Brightness = 0.00f,
                    Contrast = 1.10f,
                    Saturation = 1.25f,
                    ShadowBoost = 0.12f,
                    HighlightProtection = 0.15f,
                    RedGain = 1.00f,
                    GreenGain = 1.00f,
                    BlueGain = 1.00f,
                    RadeonImageSharpening = false,
                    RadeonImageSharpeningSharpness = 0
                }) &&
                ColorFilterSettings.Night.NearlyEquals(new ColorFilterSettings
                {
                    Gamma = 2.50f,
                    Brightness = 0.30f,
                    Contrast = 1.50f,
                    Saturation = 2.00f,
                    ShadowBoost = 0.00f,
                    HighlightProtection = 0.00f,
                    RedGain = 1.02f,
                    GreenGain = 1.00f,
                    BlueGain = 0.98f,
                    RadeonImageSharpening = false,
                    RadeonImageSharpeningSharpness = 0
                }) &&
                ColorFilterSettings.Gaming.NearlyEquals(new ColorFilterSettings
                {
                    Gamma = 0.90f,
                    Brightness = 0.00f,
                    Contrast = 1.37f,
                    Saturation = 2.00f,
                    ShadowBoost = 0.00f,
                    HighlightProtection = 0.00f,
                    RedGain = 1.02f,
                    GreenGain = 1.00f,
                    BlueGain = 0.98f,
                    RadeonImageSharpening = false,
                    RadeonImageSharpeningSharpness = 0
                });
        }
    }
}
