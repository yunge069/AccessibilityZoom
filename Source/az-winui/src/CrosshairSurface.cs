using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AccessibilityZoom
{
    // A small per-pixel-alpha surface; no desktop capture, input handling or render loop.
    internal sealed class CrosshairSurface : IDisposable
    {
        private const string ClassName = "AccessibilityZoomCrosshair";
        private static readonly NativeMethods.WndProcDelegate procedure = WindowProcedure;
        private static bool registered;
        private IntPtr memoryDc, bitmapHandle, originalObject;
        private Size imageSize;
        internal IntPtr Handle { get; private set; }

        internal CrosshairSurface()
        {
            if (!registered)
            {
                NativeMethods.WndClassEx windowClass = new()
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(), lpfnWndProc = procedure,
                    hInstance = NativeMethods.GetModuleHandleW(null), lpszClassName = ClassName
                };
                if (NativeMethods.RegisterClassExW(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                registered = true;
            }
            Handle = NativeMethods.CreateWindowExW(0x00080000 | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT |
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE, ClassName, "Accessibility Zoom crosshair",
                NativeMethods.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }

        internal void SetImage(Bitmap bitmap)
        {
            ReleaseImage();
            memoryDc = CreateCompatibleDC(IntPtr.Zero);
            if (memoryDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            BitmapInfo info = new()
            {
                Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, BitCount = 32,
                SizeImage = (uint)(bitmap.Width * bitmap.Height * 4)
            };
            bitmapHandle = CreateDIBSection(memoryDc, ref info, 0, out IntPtr pixels, IntPtr.Zero, 0);
            if (bitmapHandle == IntPtr.Zero) { ReleaseImage(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            originalObject = SelectObject(memoryDc, bitmapHandle);
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte[] row = new byte[bitmap.Width * 4];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(pixels, y * row.Length), row.Length);
                }
                imageSize = bitmap.Size;
            }
            finally { bitmap.UnlockBits(data); }
        }

        internal void Update(Point position, byte opacity)
        {
            if (memoryDc == IntPtr.Zero) return;
            NativeMethods.Point destination = new() { X = position.X, Y = position.Y };
            NativeMethods.Point source = new();
            NativeSize size = new() { Width = imageSize.Width, Height = imageSize.Height };
            BlendFunction blend = new() { SourceConstantAlpha = opacity, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The crosshair surface could not be updated.");
        }

        internal void Show() => NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopmost, 0, 0, 0, 0, 0x0053);
        internal void Hide() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);

        private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == 0x84) return new IntPtr(-1); // HTTRANSPARENT
            if (message == 0x21) return new IntPtr(3); // MA_NOACTIVATE
            return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
        }

        private void ReleaseImage()
        {
            if (originalObject != IntPtr.Zero) SelectObject(memoryDc, originalObject);
            if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            memoryDc = bitmapHandle = originalObject = IntPtr.Zero;
        }

        public void Dispose()
        {
            ReleaseImage();
            if (Handle != IntPtr.Zero) NativeMethods.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { internal int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction
        { internal byte Operation, Flags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, SizeImage;
            internal int XPelsPerMeter, YPelsPerMeter;
            internal uint ColorsUsed, ColorsImportant, Color;
        }
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screenDc,
            ref NativeMethods.Point destination, ref NativeSize size, IntPtr sourceDc, ref NativeMethods.Point source, uint colorKey,
            ref BlendFunction blend, uint flags);
    }
}
