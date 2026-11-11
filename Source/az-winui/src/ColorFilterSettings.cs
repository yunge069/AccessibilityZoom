using System;

namespace AccessibilityZoom
{
    internal struct ColorFilterSettings
    {
        internal float Gamma;
        internal float Brightness;
        internal float Contrast;
        internal float Saturation;
        internal float ShadowBoost;
        internal float HighlightProtection;
        internal float RedGain;
        internal float GreenGain;
        internal float BlueGain;
        internal bool RadeonImageSharpening;
        internal int RadeonImageSharpeningSharpness;

        internal static ColorFilterSettings Neutral
        {
            get
            {
                return new ColorFilterSettings
                {
                    Gamma = 1.0f,
                    Brightness = 0.0f,
                    Contrast = 1.0f,
                    Saturation = 1.0f,
                    ShadowBoost = 0.0f,
                    HighlightProtection = 0.0f,
                    RedGain = 1.0f,
                    GreenGain = 1.0f,
                    BlueGain = 1.0f,
                    RadeonImageSharpening = false,
                    RadeonImageSharpeningSharpness = 0
                };
            }
        }

        internal static ColorFilterSettings General
        {
            get { return Neutral; }
        }

        internal static ColorFilterSettings Night
        {
            get
            {
                return new ColorFilterSettings
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
                };
            }
        }

        internal static ColorFilterSettings Gaming
        {
            get
            {
                return new ColorFilterSettings
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
                };
            }
        }

        internal static ColorFilterSettings Visibility
        {
            get
            {
                return new ColorFilterSettings
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
                };
            }
        }

        internal ColorFilterSettings Clamped()
        {
            ColorFilterSettings value = this;
            value.Gamma = Clamp(value.Gamma, 0.50f, 2.50f, 1.0f);
            value.Brightness = Clamp(value.Brightness, -0.20f, 0.30f, 0.0f);
            value.Contrast = Clamp(value.Contrast, 0.50f, 1.50f, 1.0f);
            value.Saturation = Clamp(value.Saturation, 0.0f, 2.0f, 1.0f);
            value.ShadowBoost = Clamp(value.ShadowBoost, 0.0f, 0.50f, 0.0f);
            value.HighlightProtection = Clamp(
                value.HighlightProtection,
                0.0f,
                0.50f,
                0.0f);
            value.RedGain = Clamp(value.RedGain, 0.50f, 1.50f, 1.0f);
            value.GreenGain = Clamp(value.GreenGain, 0.50f, 1.50f, 1.0f);
            value.BlueGain = Clamp(value.BlueGain, 0.50f, 1.50f, 1.0f);
            value.RadeonImageSharpeningSharpness = Math.Max(
                0,
                Math.Min(100, value.RadeonImageSharpeningSharpness));
            return value;
        }

        internal bool NearlyEquals(ColorFilterSettings other)
        {
            return Near(Gamma, other.Gamma) &&
                Near(Brightness, other.Brightness) &&
                Near(Contrast, other.Contrast) &&
                Near(Saturation, other.Saturation) &&
                Near(ShadowBoost, other.ShadowBoost) &&
                Near(HighlightProtection, other.HighlightProtection) &&
                Near(RedGain, other.RedGain) &&
                Near(GreenGain, other.GreenGain) &&
                Near(BlueGain, other.BlueGain) &&
                RadeonImageSharpening == other.RadeonImageSharpening &&
                RadeonImageSharpeningSharpness ==
                    other.RadeonImageSharpeningSharpness;
        }

        internal int DetectPreset()
        {
            if (NearlyEquals(General)) return 0;
            if (NearlyEquals(Visibility)) return 1;
            if (NearlyEquals(Night)) return 2;
            if (NearlyEquals(Gaming)) return 3;
            return 4;
        }

        private static bool Near(float left, float right)
        {
            return Math.Abs(left - right) < 0.001f;
        }

        private static float Clamp(
            float value,
            float minimum,
            float maximum,
            float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return fallback;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
