using AccessibilityZoom;

internal static class CrosshairCursorTests
{
    internal static void Run(Action<bool, string> check)
    {
        int starts = 0, stops = 0;
        var visibility = new List<bool>();
        var cursor = new CrosshairCursor(() => { starts++; return true; },
            visible => { visibility.Add(visible); return true; }, () => { stops++; return true; });
        cursor.SetHidden(false);
        check(starts == 0 && visibility.Count == 0, "Disabled crosshair must leave cursor alone.");
        cursor.SetHidden(true);
        cursor.SetHidden(true);
        check(starts == 1 && visibility.SequenceEqual(new[] { false }), "Repeated refresh must hide once without visibility counter drift.");
        cursor.SetHidden(false);
        check(visibility.SequenceEqual(new[] { false, true }), "Disabling crosshair must restore cursor.");
        cursor.SetHidden(true);
        cursor.Dispose();
        cursor.Dispose();
        cursor.SetHidden(true);
        check(starts == 1 && stops == 1 && visibility.SequenceEqual(new[] { false, true, false, true }),
            "Exit must restore cursor before releasing magnification and be idempotent.");

        bool failHide = true;
        visibility.Clear();
        using var retry = new CrosshairCursor(() => true, visible =>
        {
            visibility.Add(visible);
            return visible || !failHide;
        }, () => true);
        bool threw = false;
        try { retry.SetHidden(true); } catch (System.ComponentModel.Win32Exception) { threw = true; }
        check(threw, "Failed cursor suppression must report failure.");
        retry.SetHidden(false);
        failHide = false;
        retry.SetHidden(true);
        retry.SetHidden(false);
        check(visibility.SequenceEqual(new[] { false, false, true }), "Failed suppression must allow retry and restoration.");

        using var unavailable = new CrosshairCursor(() => false,
            _ => throw new Exception("Visibility called after initialization failed."), () => true);
        threw = false;
        try { unavailable.SetHidden(true); } catch (System.ComponentModel.Win32Exception) { threw = true; }
        check(threw, "Initialization failure must leave cursor untouched and report failure.");
    }
}
