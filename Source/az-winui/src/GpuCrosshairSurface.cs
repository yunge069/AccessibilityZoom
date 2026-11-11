using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using static Vortice.DirectComposition.DComp;

namespace AccessibilityZoom
{
    // GPU-composited alpha window, not an UpdateLayeredWindow bitmap. Its
    // small swap chain presents only when the crosshair/settings change.
    internal sealed class GpuCrosshairSurface : IDisposable
    {
        private const string ClassName = "AccessibilityZoomGpuCrosshair";
        private static readonly NativeMethods.WndProcDelegate procedure = WindowProcedure;
        private static bool registered;
        private ID3D11Device device;
        private ID3D11DeviceContext context;
        private IDXGIFactory2 factory;
        private IDXGISwapChain1 swapChain;
        private ID3D11Texture2D backBuffer;
        private IDCompositionDevice composition;
        private IDCompositionTarget target;
        private IDCompositionVisual visual;
        private byte[] pixels;
        private Size size;
        private bool dirty;
        private byte previousOpacity;
        internal IntPtr Handle { get; private set; }
        internal bool CaptureExcluded { get; private set; }
        internal int ContentUploads { get; private set; }
        internal Size PixelSize => size;

        internal GpuCrosshairSurface()
        {
            try
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
                Handle = NativeMethods.CreateWindowExW(0x00200000 | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT |
                    NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE, ClassName, "Accessibility Zoom GPU crosshair",
                    NativeMethods.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
                if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                // HTTRANSPARENT alone only forwards within the same UI thread.
                // Layered + transparent lets clicks reach other processes, including games.
                // Keep HWND alpha at 255; the composition surface supplies pixel alpha.
                if (!NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                    out device, out _, out context).CheckError();
                factory = CreateDXGIFactory1<IDXGIFactory2>();
                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                composition = DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
                composition.CreateTargetForHwnd(Handle, true, out target).CheckError();
                composition.CreateVisual(out visual).CheckError();
                target.SetRoot(visual).CheckError();
                composition.Commit().CheckError();
                CaptureExcluded = NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            }
            catch { Dispose(); throw; }
        }

        internal void SetImage(Bitmap bitmap)
        {
            if (swapChain == null)
            {
                SwapChainDescription1 description = new()
                {
                    Width = (uint)bitmap.Width, Height = (uint)bitmap.Height, Format = Format.B8G8R8A8_UNorm,
                    BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = SampleDescription.Default,
                    Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied
                };
                swapChain = factory.CreateSwapChainForComposition(device, description);
                visual.SetContent(swapChain).CheckError();
                composition.Commit().CheckError();
                backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            }
            else if (size != bitmap.Size)
            {
                backBuffer.Dispose();
                backBuffer = null;
                swapChain.ResizeBuffers(2, (uint)bitmap.Width, (uint)bitmap.Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
                backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            }
            size = bitmap.Size;
            pixels = new byte[bitmap.Width * bitmap.Height * 4];
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * bitmap.Width * 4, bitmap.Width * 4);
            }
            finally { bitmap.UnlockBits(data); }
            dirty = true;
        }

        internal void Update(Point position, byte opacity)
        {
            if (pixels == null) return;
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopmost, position.X, position.Y, size.Width, size.Height, NativeMethods.SWP_NOACTIVATE);
            if (!dirty && opacity == previousOpacity) return;
            byte[] upload = pixels;
            if (opacity != 255)
            {
                upload = new byte[pixels.Length];
                for (int i = 0; i < pixels.Length; i++) upload[i] = (byte)((pixels[i] * opacity + 127) / 255);
            }
            GCHandle pinned = GCHandle.Alloc(upload, GCHandleType.Pinned);
            try { context.UpdateSubresource(backBuffer, 0, null, pinned.AddrOfPinnedObject(), (uint)size.Width * 4, 0); }
            finally { pinned.Free(); }
            swapChain.Present(0, PresentFlags.None).CheckError();
            ContentUploads++;
            composition.Commit().CheckError();
            previousOpacity = opacity;
            dirty = false;
        }

        internal void Show() => NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopmost, 0, 0, 0, 0, 0x0053);
        internal void Hide() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
        private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == 0x84) return new IntPtr(-1);
            if (message == 0x21) return new IntPtr(3);
            return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
        }

        public void Dispose()
        {
            if (visual != null) { visual.SetContent(null); composition?.Commit(); }
            backBuffer?.Dispose(); visual?.Dispose(); target?.Dispose(); swapChain?.Dispose(); composition?.Dispose();
            factory?.Dispose(); context?.Dispose(); device?.Dispose();
            backBuffer = null; visual = null; target = null; swapChain = null; composition = null;
            factory = null; context = null; device = null;
            if (Handle != IntPtr.Zero) NativeMethods.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
