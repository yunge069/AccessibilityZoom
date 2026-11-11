using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AccessibilityZoom
{
    // A transient, click-through window. Showing it never changes keyboard focus.
    internal sealed class PresetPopup : IDisposable
    {
        private readonly Window window = new();
        private readonly TextBlock name = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1800) };
        private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly ScaleTransform contentScale = new();
        private readonly AppWindow appWindow;
        private readonly IntPtr handle;
        private Rectangle display;
        private Rectangle previousBounds;

        internal PresetPopup()
        {
            StackPanel content = new() { Spacing = 3 };
            content.Children.Add(new TextBlock { Text = "Monitor preset", FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 175, 185, 200)) });
            content.Children.Add(name);
            Border card = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 32, 40)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 72, 84, 102)),
                BorderThickness = new Thickness(1), Padding = new Thickness(18, 12, 18, 12), Child = content,
                Width = 280, Height = 82, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, RenderTransform = contentScale };
            Grid root = new() { Background = card.Background };
            root.Children.Add(card);
            window.Content = root;
            handle = WindowNative.GetWindowHandle(window);
            appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }
            long style = GetWindowLongPtrW(handle, -20).ToInt64();
            SetWindowLongPtrW(handle, -20, new IntPtr(style | NativeMethods.WS_EX_NOACTIVATE |
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            hideTimer.Tick += (_, __) => { hideTimer.Stop(); positionTimer.Stop(); appWindow.Hide(); };
            positionTimer.Tick += (_, __) => UpdatePlacement();
        }

        internal void Show(string presetName, Rectangle display)
        {
            name.Text = presetName;
            if (display.IsEmpty) display = DisplayHelper.GetPrimaryBounds();
            this.display = display;
            // Move first so GetDpiForWindow reflects the destination monitor.
            appWindow.Move(new PointInt32(display.Right - 280, display.Top + 20));
            previousBounds = Rectangle.Empty;
            UpdatePlacement();
            NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, 0, 0, 0, 0, 0x0013); // no move/size/activate
            appWindow.Show(false);
            positionTimer.Start();
            hideTimer.Stop();
            hideTimer.Start();
        }

        private void UpdatePlacement()
        {
            double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
            int margin = (int)Math.Round(20 * scale);
            int width = Math.Min(display.Width, (int)Math.Round(280 * scale));
            int height = (int)Math.Round(82 * scale);
            Rectangle destination = new(display.Right - width - margin, display.Top + margin, width, height);
            // Only the short-lived popup samples zoom; the zoom worker stays independent.
            DesktopTransform transform = NativeMethods.MagGetFullscreenTransform(out float zoom, out int x, out int y)
                ? new DesktopTransform(zoom, x, y) : DesktopTransform.Identity;
            Rectangle source = transform.ToSourceBounds(destination);
            contentScale.ScaleX = contentScale.ScaleY = 1.0 / transform.Scale;
            if (source != previousBounds)
            {
                appWindow.MoveAndResize(new RectInt32(source.X, source.Y, source.Width, source.Height));
                previousBounds = source;
            }
        }

        public void Dispose() { hideTimer.Stop(); positionTimer.Stop(); window.Close(); }

        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    }
}
