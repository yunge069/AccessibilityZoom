using System;
using System.Runtime.InteropServices;

namespace AccessibilityZoom
{
    internal static class NativeMethods
    {
        internal const uint WS_POPUP = 0x80000000;
        internal const uint WS_EX_TOPMOST = 0x00000008;
        internal const uint WS_EX_LAYERED = 0x00080000;
        internal const uint WS_EX_TRANSPARENT = 0x00000020;
        internal const uint WS_EX_TOOLWINDOW = 0x00000080;
        internal const uint WS_EX_NOACTIVATE = 0x08000000;
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        internal const int SW_HIDE = 0;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const uint SWP_NOACTIVATE = 0x0010;

        internal static readonly IntPtr HwndTopmost = new IntPtr(-1);

        internal const int VK_XBUTTON1 = 0x05;
        internal const int VK_XBUTTON2 = 0x06;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WndClassEx
        {
            internal uint cbSize;
            internal uint style;
            internal WndProcDelegate lpfnWndProc;
            internal int cbClsExtra;
            internal int cbWndExtra;
            internal IntPtr hInstance;
            internal IntPtr hIcon;
            internal IntPtr hCursor;
            internal IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] internal string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] internal string lpszClassName;
            internal IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WndClassEx lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MagInitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MagUninitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MagShowSystemCursor([MarshalAs(UnmanagedType.Bool)] bool showCursor);

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MagSetFullscreenTransform(
            float magnificationLevel,
            int xOffset,
            int yOffset);

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MagGetFullscreenTransform(
            out float magnificationLevel,
            out int xOffset,
            out int yOffset);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal System.Drawing.Rectangle ToRectangle() =>
                System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClipCursor(out Rect rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rectangle);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        internal static bool IsOwnWindowForeground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
            return processId == Environment.ProcessId;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        internal static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle, IntPtr updateRegion, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowDisplayAffinity(
            IntPtr window,
            uint affinity);

        [DllImport("winmm.dll")]
        internal static extern uint timeBeginPeriod(uint periodMilliseconds);

        [DllImport("winmm.dll")]
        internal static extern uint timeEndPeriod(uint periodMilliseconds);

    }
}
