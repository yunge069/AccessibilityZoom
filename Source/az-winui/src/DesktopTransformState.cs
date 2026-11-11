using System;
using System.Threading;

namespace AccessibilityZoom
{
    internal sealed record DesktopTransform(float Scale, int OffsetX, int OffsetY)
    {
        internal static readonly DesktopTransform Identity = new(1, 0, 0);
        internal System.Drawing.Rectangle ToSourceBounds(System.Drawing.Rectangle destination) =>
            ScopeMath.CalculateOverlaySourceBounds(destination, Scale, new System.Drawing.Point(OffsetX, OffsetY));
    }

    // Only the owning magnification worker publishes verified compositor state.
    // Overlays read an atomic snapshot; they never need a second native query.
    internal static class DesktopTransformState
    {
        private static DesktopTransform current = DesktopTransform.Identity;
        internal static DesktopTransform Current => Volatile.Read(ref current);
        internal static event Action Changed;

        internal static bool Publish(float scale, int offsetX, int offsetY)
        {
            if (!float.IsFinite(scale) || scale < 1 || scale > 4096) return false;
            DesktopTransform next = scale == 1 ? DesktopTransform.Identity : new(scale, offsetX, offsetY);
            if (Current == next) return false;
            Volatile.Write(ref current, next);
            Action changed = Changed;
            if (changed != null)
                foreach (Action listener in changed.GetInvocationList())
                    try { listener(); } catch { /* Overlay notification cannot interrupt zoom. */ }
            return true;
        }
    }
}
