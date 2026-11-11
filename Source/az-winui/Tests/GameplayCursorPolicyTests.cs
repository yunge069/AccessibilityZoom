using System.Drawing;
using AccessibilityZoom;

internal static class GameplayCursorPolicyTests
{
    internal static void Run(Action<bool, string> check)
    {
        var policy = new GameplayCursorPolicy();
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var center = new Point(960, 540);
        var game = new IntPtr(123);
        check(!policy.ShouldHide(game, false, monitor, monitor, center, monitor, 0), "Gameplay suppression must wait for a stable centered pointer.");
        check(policy.ShouldHide(game, false, monitor, monitor, center, monitor, 350), "Stable fullscreen centered pointer must be suppressed.");
        check(!policy.ShouldHide(game, false, monitor, monitor, new Point(1000, 540), monitor, 400), "Menu pointer movement must restore cursor immediately.");
        check(!policy.ShouldHide(game, false, monitor, monitor, center, monitor, 450), "Returning to center must restart detection.");
        check(policy.ShouldHide(game, false, monitor, monitor, center, monitor, 800), "Gameplay may resume after pointer settles.");
        check(!policy.ShouldHide(new IntPtr(456), false, monitor, monitor, center, monitor, 850), "Alt-tab must immediately restore cursor even to another fullscreen window.");
        check(!policy.ShouldHide(game, true, monitor, monitor, center, monitor, 900), "Desktop and own settings must always retain cursor.");
        check(!policy.ShouldHide(game, false, new Rectangle(20, 20, 1000, 800), monitor, center, monitor, 950), "Ordinary desktop windows must retain cursor.");
        check(!policy.ShouldHide(game, false, monitor, new Rectangle(-1920, 0, 3840, 1080), center, monitor, 1000), "Unconfined multi-monitor pointer must remain available.");
        check(!policy.ShouldHide(IntPtr.Zero, false, monitor, monitor, center, monitor, 1050), "Missing focus must restore cursor.");
        var left = new Rectangle(-1920, 0, 1920, 1080);
        check(!policy.ShouldHide(game, false, left, left, new Point(-960, 540), left, 1100), "Secondary monitor needs stable detection too.");
        check(policy.ShouldHide(game, false, left, left, new Point(-960, 540), left, 1450), "Gameplay detection must support negative monitor coordinates.");
    }
}
