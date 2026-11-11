using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;

namespace AccessibilityZoom
{
    // Application host: single instance, CLI surface, tray icon, and the
    // shutdown sequence. The control panel itself is MainWindow; the zoom
    // engine is only ever touched through IZoomController.
    public partial class App : Application
    {
        private const string SingleInstanceKey = "AccessibilityZoom.Main";

        private IZoomController engine;
        private MainWindow panel;
        private TaskbarIcon trayIcon;
        private GlobalHotkeyService hotkeys;
        private AppVolumeShortcut volumeShortcut;
        private bool shuttingDown;
        private readonly CancellationTokenSource startupCancellation = new CancellationTokenSource();

        public App()
        {
            InitializeComponent();
            UnhandledException += (_, e) =>
            {
                // Never leave the user's display zoomed after a crash.
                EmergencyReset();
                e.Handled = true;
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "az-unhandled-error.txt"), e.Exception.ToString()); } catch { }
                try { Exit(); } catch { Environment.Exit(1); }
            };
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            string[] cli = Environment.GetCommandLineArgs().Skip(1).ToArray();

            // Headless self-tests run before single-instance registration,
            // mirroring the old Program.cs (they never return on this path).
            RunHeadlessSelfTest(cli);

            bool preview = cli.Any(a =>
                a.Equals("--ui-preview", StringComparison.OrdinalIgnoreCase));

            if (!preview)
            {
                AppInstance instance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
                if (!instance.IsCurrent)
                {
                    AppActivationArguments activation =
                        AppInstance.GetCurrent().GetActivatedEventArgs();
                    await instance.RedirectActivationToAsync(activation);
                    Exit();
                    return;
                }
                instance.Activated += (_, __) => panel?.ShowAndActivate();
            }

            try
            {
                HotkeyBindings bindings = HotkeyBindings.Defaults();
                bool startColor = false;
                if (preview)
                {
                    engine = new PreviewZoomController();
                    panel = new MainWindow(engine, string.Empty, previewMode: true);
                }
                else
                {
                    AppSettings settings = SettingsStore.Load();
                    bindings = settings.Bindings;
                    engine = new MagnifierEngine(
                        settings.Zoom,
                        settings.ThumbButton,
                        settings.ColorFilter,
                        false, // Apply startup color after the desktop layout settles.
                        bindings[HotkeyAction.HoldZoom],
                        settings.FilterMonitorDevice);
                    panel = new MainWindow(
                        engine,
                        settings.SelectedProfileId,
                        previewMode: false,
                        soundsEnabled: settings.SoundsEnabled,
                        reduceTransparency: settings.ReduceTransparency,
                        bindings: bindings,
                        startWithColorEnabled: settings.StartWithColorEnabled,
                        quietVolumePercent: settings.QuietVolumePercent);
                    if (engine is MagnifierEngine nativeEngine)
                        nativeEngine.Error += (_, error) => panel?.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!shuttingDown) panel?.ShowEngineWarning(error.Message);
                        });
                    startColor = panel.StartupColorEnabled;
                }

                panel.ExitRequested += (_, __) => Shutdown();
                if (preview)
                    panel.Closed += (_, __) => { if (!shuttingDown) Shutdown(); };
                panel.Activate();

                if (!preview)
                {
                    CreateTrayIcon(bindings);
                    StartHotkeys(bindings);
                    panel.BindingsChanged += next =>
                    {
                        hotkeys?.SetBindings(next);
                        UpdateTrayShortcuts(next);
                    };
                    panel.WhenReady(() =>
                    {
                        panel.InitializeUpdates();
                        if (startColor) QueueStartupColor();
                        if (cli.Any(a => a.Equals("--start-minimized", StringComparison.OrdinalIgnoreCase))) panel.HideToTray();
                    });
                }
            }
            catch (Exception exception)
            {
                EmergencyReset();
                Debug.WriteLine("Accessibility Zoom could not start: " + exception);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "az-startup-error.txt"), exception.ToString()); } catch { }
                Exit();
            }
        }

        private async void QueueStartupColor()
        {
            // Start only after the control window is loaded. The async wait uses
            // the UI dispatcher and does not depend on a pre-activation XAML timer.
            string logPath = Path.Combine(Path.GetTempPath(), "az-startup-color.txt");
            void Trace(string message)
            {
                try { File.AppendAllText(logPath, DateTime.Now.ToString("O") + " " + message + Environment.NewLine); } catch { }
            }
            try
            {
                try { File.WriteAllText(logPath, "Accessibility Zoom startup preset" + Environment.NewLine); } catch { }
                Trace("Window ready; requested=" + panel.StartupColorEnabled + "; monitor=" + engine.FilterMonitorDevice);
                await StartupColorSequence.RunAsync(() => !shuttingDown && panel.StartupColorEnabled,
                    () => engine.FilterEnabled, () =>
                    {
                        var displays = DisplayHelper.GetDisplays();
                        return (string.Join(";", displays.Select(display => display.DeviceName + ":" + display.Bounds)),
                            DisplayHelper.FindSavedDisplay(displays, engine.FilterMonitorDevice) != null);
                    }, () => engine is MagnifierEngine magnifier && magnifier.EnableStartupFilter(),
                    message => panel.ShowEngineWarning(message), Trace, startupCancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Trace(exception.ToString());
                if (!shuttingDown) panel.ShowEngineWarning("Startup preset could not be applied: " + exception.Message);
            }
        }

        // Tray items that show a shortcut: label, matching hotkey action.
        private readonly System.Collections.Generic.List<(MenuFlyoutItem Item, string Text, HotkeyAction Action)>
            shortcutMenuItems = new();

        private void CreateTrayIcon(HotkeyBindings bindings)
        {
            trayIcon = new TaskbarIcon
            {
                IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
                ToolTipText = "Accessibility Zoom",
            };

            MenuFlyout menu = new MenuFlyout();
            AddMenuItem(menu, "Open Accessibility Zoom", () => panel?.ShowAndActivate());
            menu.Items.Add(new MenuFlyoutSeparator());
            AddShortcutMenuItem(menu, "Display color on / off", HotkeyAction.ToggleColor,
                () => { panel?.FlushFilterEdits(); engine?.ToggleFilter(); });
            AddShortcutMenuItem(menu, "Cycle saved color profiles", HotkeyAction.CycleProfiles,
                () => panel?.CycleFilterProfile());
            AddShortcutMenuItem(menu, "Crosshair on / off", HotkeyAction.ToggleCrosshair,
                () => panel?.ToggleCrosshair());
            menu.Items.Add(new MenuFlyoutSeparator());
            AddMenuItem(menu, "Check for updates", () => panel?.OpenUpdates());
            AddShortcutMenuItem(menu, "Exit Accessibility Zoom", HotkeyAction.Exit, () => Shutdown());
            UpdateTrayShortcuts(bindings);

            trayIcon.ContextFlyout = menu;
            trayIcon.DoubleClickCommand = new RelayCommand(() => panel?.ShowAndActivate());
            trayIcon.ForceCreate();
        }

        private void AddShortcutMenuItem(MenuFlyout menu, string text, HotkeyAction action, Action run)
        {
            MenuFlyoutItem item = AddMenuItem(menu, text, run);
            shortcutMenuItems.Add((item, text, action));
        }

        // The tray menu is a native Win32 menu: text after a tab is drawn
        // right-aligned, like normal Windows shortcut hints.
        private void UpdateTrayShortcuts(HotkeyBindings bindings)
        {
            if (bindings == null) return;
            foreach (var entry in shortcutMenuItems)
            {
                InputBinding binding = bindings[entry.Action];
                entry.Item.Text = binding.Enabled ? entry.Text + "\t" + binding : entry.Text;
            }
        }

        private static MenuFlyoutItem AddMenuItem(MenuFlyout menu, string text, Action action)
        {
            // H.NotifyIcon shows the tray menu as a native Win32 popup
            // (PopupMenu mode). That mode only runs item.Command; it never
            // raises the XAML Click event.
            // The native menu runs the command on the tray icon's own message
            // thread, so hop back to the UI thread before touching the panel
            // or the engine.
            Microsoft.UI.Dispatching.DispatcherQueue ui =
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            MenuFlyoutItem item = new MenuFlyoutItem
            {
                Text = text,
                Command = new RelayCommand(() =>
                {
                    if (ui == null || ui.HasThreadAccess) action();
                    else ui.TryEnqueue(() => action());
                }),
            };
            menu.Items.Add(item);
            return item;
        }

        private void StartHotkeys(HotkeyBindings bindings)
        {
            string statusPath = Path.Combine(Path.GetTempPath(), "az-f10-status.txt");
            try
            {
                volumeShortcut = new AppVolumeShortcut();
                volumeShortcut.Completed += ShowVolumeBalloon;
                hotkeys = new GlobalHotkeyService(bindings);
                hotkeys.Pressed += HandleHotkey;
                File.WriteAllText(statusPath, "shortcuts installed " + DateTime.Now.ToString("o") +
                    "\r\nexe " + Environment.ProcessPath);
            }
            catch (Exception exception)
            {
                hotkeys?.Dispose();
                hotkeys = null;
                volumeShortcut?.Dispose();
                volumeShortcut = null;
                panel?.ShowEngineWarning("Global shortcuts unavailable: " + exception.Message);
                try { File.WriteAllText(statusPath, "hook FAILED: " + exception.Message); } catch { }
            }
        }

        private void HandleHotkey(HotkeyPress press)
        {
            if (shuttingDown) return;
            if (press.Action == HotkeyAction.AppVolume)
            {
                volumeShortcut?.Enqueue(press.HeldMilliseconds, panel.QuietVolumePercent);
                return;
            }
            panel?.DispatcherQueue.TryEnqueue(() =>
            {
                if (shuttingDown) return;
                switch (press.Action)
                {
                    case HotkeyAction.ToggleZoom: engine.Toggle(); break;
                    case HotkeyAction.ToggleColor: panel.FlushFilterEdits(); engine.ToggleFilter(); break;
                    case HotkeyAction.CycleProfiles: panel.CycleFilterProfile(); break;
                    case HotkeyAction.IncreaseZoom: engine.IncreaseZoom(); break;
                    case HotkeyAction.DecreaseZoom: engine.DecreaseZoom(); break;
                    case HotkeyAction.ResetView: panel.ResetView(); break;
                    case HotkeyAction.ToggleCrosshair: panel.ToggleCrosshair(); break;
                    case HotkeyAction.Exit: Shutdown(); break;
                }
            });
        }

        // One small tray-tip update per F10 press so a tap gives instant
        // visible feedback on hover (kills the tap-again uncertainty).
        // Runs on the UI thread. Uses only the existing ToolTipText API.
        private void ShowVolumeBalloon(string detail)
        {
            try
            {
                TaskbarIcon icon = trayIcon;
                if (icon == null)
                    return;
                string tip = "Accessibility Zoom — " + detail;
                if (tip.Length > 127)
                    tip = tip.Substring(0, 127);
                try
                {
                    if (!icon.DispatcherQueue.TryEnqueue(() =>
                    {
                        try { icon.ToolTipText = tip; }
                        catch { }
                    }))
                        icon.ToolTipText = tip;
                }
                catch
                {
                    icon.ToolTipText = tip;
                }
            }
            catch
            {
                // UI feedback must never break the hotkey.
            }
        }

        private sealed class RelayCommand : System.Windows.Input.ICommand
        {
            private readonly Action execute;
            public RelayCommand(Action execute) => this.execute = execute;
            public event EventHandler CanExecuteChanged;
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => execute();
        }

        internal void Shutdown()
        {
            if (shuttingDown)
                return;
            shuttingDown = true;
            startupCancellation.Cancel();

            try
            {
                // Commit the last slider movement before reset notifications
                // refresh the UI.
                panel?.PrepareForExit();
                engine?.SetActive(false);
                engine?.SetFilterEnabled(false);
            }
            catch
            {
                // Shutdown must not throw.
            }

            if (trayIcon != null)
            {
                trayIcon.Dispose();
                trayIcon = null;
            }
            hotkeys?.Dispose();
            hotkeys = null;
            if (volumeShortcut != null)
            {
                volumeShortcut.Completed -= ShowVolumeBalloon;
                volumeShortcut.Dispose();
                volumeShortcut = null;
            }
            if (engine is IDisposable disposable)
            {
                disposable.Dispose();
                engine = null;
            }
            panel?.Close();
            panel = null;
            Exit();
        }

        internal static void EmergencyReset()
        {
            // Best-effort recovery for an unexpected main-thread failure while
            // the direct compositor transform is active.
            try
            {
                if (NativeMethods.MagInitialize())
                {
                    NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0);
                    NativeMethods.MagUninitialize();
                }
            }
            catch
            {
                // There is nothing else safe to do during emergency teardown.
            }
        }

        // --self-test, --gpu-self-test, --direct-self-test, --amd-self-test,
        // --amd-roundtrip-self-test. These never return: they terminate the
        // process with the legacy exit codes.
        private static void RunHeadlessSelfTest(string[] cli)
        {
            bool Has(string name) =>
                cli.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (Has("--self-test"))
                Environment.Exit(ScopeMath.RunSelfTest() ? 0 : 1);

            if (Has("--crosshair-self-test"))
            {
                try
                {
                    using var bitmap = StaticCrosshairRasterizer.Render(CrosshairAsset.Default);
                    using GpuCrosshairSurface surface = new();
                    surface.SetImage(bitmap);
                    surface.Update(System.Drawing.Point.Empty, 255);
                    Environment.Exit(StaticCrosshairRasterizer.HasVisiblePixels(bitmap) &&
                        !NativeMethods.IsWindowVisible(surface.Handle) ? 0 : 13);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(exception);
                    Environment.Exit(13);
                }
            }

            if (Has("--gpu-self-test"))
            {
                try
                {
                    using (GpuOverlay overlay =
                        new GpuOverlay(DisplayHelper.GetPrimaryBounds()))
                    {
                        // Initialization exercises desktop duplication,
                        // shaders, and the swap chain while the overlay
                        // remains hidden. Let the render thread acquire
                        // several cropped desktop frames.
                        Thread.Sleep(250);
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(exception.ToString());
                    Environment.Exit(3);
                }
                Environment.Exit(0);
            }

            if (Has("--direct-self-test"))
                Environment.Exit(RunDirectSelfTest());

            if (Has("--amd-self-test") || Has("--amd-roundtrip-self-test"))
                Environment.Exit(RunAmdSelfTest(Has("--amd-roundtrip-self-test")));
        }

        private static int RunDirectSelfTest()
        {
            try
            {
                using (DirectFullscreenZoom zoom =
                    new DirectFullscreenZoom(DisplayHelper.GetPrimaryBounds()))
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    if (!zoom.Show(
                        DisplayHelper.GetPrimaryBounds(),
                        2.0f,
                        ColorFilterSettings.Neutral,
                        out int errorCode))
                    {
                        Console.Error.WriteLine("Direct zoom request failed: " + errorCode);
                        return 4;
                    }

                    if (!zoom.WaitForAppliedState(true, 2000))
                    {
                        Console.Error.WriteLine("Direct zoom did not reach 2x within 2 seconds.");
                        return 5;
                    }
                    long activeMicroseconds =
                        stopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;

                    stopwatch.Restart();
                    zoom.Hide();
                    if (!zoom.WaitForAppliedState(false, 2000))
                    {
                        Console.Error.WriteLine("Direct zoom did not return to 1x within 2 seconds.");
                        return 6;
                    }
                    long resetMicroseconds =
                        stopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;

                    Console.WriteLine(
                        "Direct compositor self-test passed: on=" + activeMicroseconds +
                        " us, off=" + resetMicroseconds + " us.");
                }

                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 7;
            }
            finally
            {
                // Redundant safety reset for failed or interrupted test paths.
                if (NativeMethods.MagInitialize())
                {
                    NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0);
                    NativeMethods.MagUninitialize();
                }
            }
        }

        private static int RunAmdSelfTest(bool writeRoundTrip)
        {
            // Refuse when the app is already running, like the old mutex check.
            AppInstance instance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (!instance.IsCurrent)
            {
                Console.Error.WriteLine(
                    "AMD self-test refused because Accessibility Zoom is already running.");
                return 12;
            }

            try
            {
                using (AmdDriverColorController controller =
                    new AmdDriverColorController())
                {
                    Console.WriteLine(controller.Status);
                    if (!controller.Available)
                        return 8;

                    if (!writeRoundTrip)
                    {
                        Console.WriteLine(
                            "AMD ADLX read-only self-test passed for " +
                            controller.DisplayCount + " display(s).");
                        return 0;
                    }

                    bool applied = false;
                    try
                    {
                        applied = controller.Apply(
                            ColorFilterSettings.Night,
                            DisplayHelper.GetPrimaryBounds());
                        if (!applied)
                        {
                            Console.Error.WriteLine(controller.Status);
                            return 9;
                        }

                        Thread.Sleep(350);
                        if (!controller.Apply(
                            ColorFilterSettings.General,
                            DisplayHelper.GetPrimaryBounds()))
                        {
                            Console.Error.WriteLine(controller.Status);
                            return 13;
                        }

                        Thread.Sleep(200);
                        if (!controller.Apply(
                            ColorFilterSettings.Gaming,
                            DisplayHelper.GetPrimaryBounds()))
                        {
                            Console.Error.WriteLine(controller.Status);
                            return 14;
                        }

                        Thread.Sleep(350);
                        if (!controller.Restore())
                        {
                            Console.Error.WriteLine(controller.Status);
                            return 10;
                        }

                        applied = false;
                        Console.WriteLine(
                            "AMD ADLX apply/restore round-trip self-test passed.");
                        return 0;
                    }
                    finally
                    {
                        if (applied)
                            controller.Restore();
                    }
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 11;
            }
        }
    }
}
