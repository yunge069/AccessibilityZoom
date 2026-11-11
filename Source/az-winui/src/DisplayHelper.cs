using System;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AccessibilityZoom
{
    // WinUI 3 has no System.Windows.Forms, so the two display lookups the zoom
    // engine needs (primary monitor bounds, monitor bounds for a cursor point)
    // are done with the same Win32 monitor APIs Screen wraps. Behavior is
    // identical: primary bounds, and the bounds of the monitor containing the
    // given point (nearest monitor if the point is off-screen).
    internal static class DisplayHelper
    {
        private const uint MONITOR_DEFAULTTONULL = 0;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);

        internal static List<DisplayChoice> GetDisplays()
        {
            List<DisplayChoice> result = new();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, dc, rect, data) =>
            {
                MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                    result.Add(new DisplayChoice(info.DeviceName, Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top,
                        info.Monitor.Right, info.Monitor.Bottom), (info.Flags & 1) != 0));
                return true;
            }, IntPtr.Zero);
            result.Sort((a, b) => a.Primary == b.Primary ? string.CompareOrdinal(a.DeviceName, b.DeviceName) : a.Primary ? -1 : 1);
            return result;
        }

        internal static DisplayChoice FindSavedDisplay(IEnumerable<DisplayChoice> displays, string deviceName)
        {
            foreach (DisplayChoice display in displays)
                if (string.IsNullOrEmpty(deviceName) ? display.Primary :
                    string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return display;
            return null;
        }

        internal static Rectangle GetPrimaryBounds()
        {
            return GetMonitorBounds(new NativePoint { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        }

        internal static Rectangle GetBoundsFromPoint(int x, int y)
        {
            return GetMonitorBounds(new NativePoint { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        }

        private static Rectangle GetMonitorBounds(NativePoint point, uint flags)
        {
            IntPtr monitor = MonitorFromPoint(point, flags);
            if (monitor == IntPtr.Zero)
                return Rectangle.Empty;
            MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return Rectangle.Empty;
            return Rectangle.FromLTRB(
                info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right, info.Monitor.Bottom);
        }
    }

    internal sealed record DisplayChoice(string DeviceName, Rectangle Bounds, bool Primary)
    {
        public override string ToString() => (Primary ? "Primary · " : "") + DeviceName.Replace(@"\\.\", "") +
            " · " + Bounds.Width + " × " + Bounds.Height;
    }
}
