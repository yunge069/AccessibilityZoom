using System;

namespace AccessibilityZoom
{
    internal static class QuietVolumePolicy
    {
        internal static float GetBaseline(float level, bool muted, float muteRestoreLevel)
        {
            float baseline = Math.Clamp(float.IsFinite(level) ? level : 0, 0, 1);
            if (muted && baseline == 0 && muteRestoreLevel > .02f)
                return Math.Clamp(muteRestoreLevel, 0, 1);
            return baseline;
        }

        internal static float Reduce(float baseline, int quietVolumePercent) =>
            baseline * (Math.Clamp(quietVolumePercent, 0, 100) / 100f);
    }
}
