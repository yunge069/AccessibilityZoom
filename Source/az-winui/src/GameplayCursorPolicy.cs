using System;
using System.Drawing;

namespace AccessibilityZoom
{
    // Conservative gameplay heuristic: another fullscreen window, confined pointer,
    // and a stable pointer at the reticle. Any focus change or free movement restores it.
    internal sealed class GameplayCursorPolicy
    {
        private IntPtr previousWindow;
        private long centeredSince = -1;

        internal bool ShouldHide(IntPtr window, bool ownOrShell, Rectangle foreground,
            Rectangle clip, Point pointer, Rectangle display, long now)
        {
            bool candidate = window != IntPtr.Zero && !ownOrShell && !display.IsEmpty &&
                foreground.Contains(display) && !clip.IsEmpty && display.Contains(clip) &&
                Math.Abs(pointer.X - (display.Left + display.Width / 2)) <= 2 &&
                Math.Abs(pointer.Y - (display.Top + display.Height / 2)) <= 2;
            if (!candidate || window != previousWindow) centeredSince = -1;
            previousWindow = window;
            if (!candidate) return false;
            if (centeredSince < 0) centeredSince = now;
            return now - centeredSince >= 350;
        }

        internal bool Sample(Rectangle display)
        {
            IntPtr window = NativeMethods.GetForegroundWindow();
            bool gotWindow = NativeMethods.GetWindowRect(window, out var foreground);
            bool gotClip = NativeMethods.GetClipCursor(out var clip);
            bool gotPointer = NativeMethods.GetCursorPos(out var pointer);
            if (!gotWindow || !gotClip || !gotPointer)
                return ShouldHide(IntPtr.Zero, true, default, default, default, display, Environment.TickCount64);
            return ShouldHide(window, NativeMethods.IsOwnWindowForeground() ||
                window == NativeMethods.GetShellWindow() || window == NativeMethods.GetDesktopWindow(),
                foreground.ToRectangle(), clip.ToRectangle(), new Point(pointer.X, pointer.Y), display, Environment.TickCount64);
        }
    }
}
