using System.Drawing;
using AccessibilityZoom;

internal static class DesktopTransformTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        DesktopTransformState.Publish(1, 0, 0);
        int notifications = 0;
        void Changed() => Interlocked.Increment(ref notifications);
        DesktopTransformState.Changed += Changed;
        try
        {
            check(!DesktopTransformState.Publish(1, 100, 100) && notifications == 0,
                "Identity offsets must normalize without waking an idle overlay.");
            await Task.Run(() => DesktopTransformState.Publish(2, 480, 270));
            var snapshot = DesktopTransformState.Current;
            check(snapshot == new DesktopTransform(2, 480, 270) && notifications == 1,
                "Verified zoom must reach overlay consumers across threads.");
            check(!DesktopTransformState.Publish(2, 480, 270) && notifications == 1,
                "Unchanged applied zoom must not cause a redraw notification.");
            Rectangle target = new(928, 524, 64, 32);
            Rectangle source = snapshot.ToSourceBounds(target);
            check(source.Width == 32 && source.Height == 16,
                "At 2x, the actual source crosshair must shrink to half width and height.");
            check(source.Width * snapshot.Scale == target.Width && source.Height * snapshot.Scale == target.Height,
                "Compositor magnification must restore the crosshair's original display size.");
            check((source.Left - snapshot.OffsetX) * snapshot.Scale == target.Left &&
                (source.Top - snapshot.OffsetY) * snapshot.Scale == target.Top,
                "Size compensation must retain the crosshair's screen position.");
            check(!DesktopTransformState.Publish(float.NaN, 0, 0) && DesktopTransformState.Current == snapshot,
                "An invalid native scale must not replace the last verified transform.");
            check(!DesktopTransformState.Publish(0, 0, 0) && notifications == 1,
                "A failed native scale read must not silently switch the overlay to 1x.");
            for (int i = 0; i < 500; i++)
            {
                int n = i;
                await Task.Run(() => DesktopTransformState.Publish(n % 2 == 0 ? 4 : 2, n, -n));
                snapshot = DesktopTransformState.Current;
                check(snapshot.OffsetX == n && snapshot.OffsetY == -n && snapshot.Scale == (n % 2 == 0 ? 4 : 2),
                    "Transform scale and offsets must remain a coherent atomic snapshot.");
            }
            check(DesktopTransformState.Publish(1, 0, 0) && DesktopTransformState.Current == DesktopTransform.Identity,
                "Zoom release must restore the overlay's unscaled source size.");
            check(DesktopTransformState.Current.ToSourceBounds(target) == target,
                "At normal view the original crosshair bitmap and placement must be restored.");
        }
        finally
        {
            DesktopTransformState.Changed -= Changed;
            DesktopTransformState.Publish(1, 0, 0);
        }
    }
}
