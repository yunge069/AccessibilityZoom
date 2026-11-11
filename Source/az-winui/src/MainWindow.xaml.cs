using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AccessibilityZoom
{
    // Accessibility Zoom control panel (WinUI 3). Behavior is a faithful port
    // of the proven panel logic: the engine is touched only through
    // IZoomController, notifications are batched with Interlocked +
    // DispatcherQueue, slider edits flush on a 33 ms timer and commit on
    // release, settings persist on a 700 ms debounce timer.
    public sealed partial class MainWindow : Window
    {
        private readonly IZoomController engine;
        private readonly ScopeSounds scopeSounds = new ScopeSounds();
        private readonly bool previewMode;
        private readonly bool soundsEnabled;
        private readonly bool reduceTransparency;
        private readonly DispatcherTimer filterUpdateTimer;
        private readonly DispatcherTimer settingsSaveTimer;
        private readonly List<ColorProfile> colorProfiles;
        private readonly Dictionary<string, ColorFilterSettings> drafts =
            new Dictionary<string, ColorFilterSettings>(StringComparer.OrdinalIgnoreCase);
        private ColorProfile selectedColorProfile;

        private int queuedEngineDisplayUpdate;
        private readonly HashSet<Slider> activeSliderGestures = new HashSet<Slider>();
        private bool applyingFilterEdit;
        private bool pendingFilterEdit;
        private bool updatingFilterControls;
        private bool displayInitialized;
        private bool previousActive;
        private bool allowClose;
        private bool exiting;
        private bool closed;
        private bool settingsSaved;
        private AppSettings lastSaved;
        private AppSettings pendingSettings;
        private string engineWarning = "";
        private string persistenceWarning = "";

        private AppWindow appWindow;
        private HotkeyBindings bindings;
        private volatile int quietVolumePercent;
        private PresetPopup presetPopup;
        private readonly Dictionary<HotkeyAction, Button> bindingButtons = new();

        internal event EventHandler ExitRequested;
        internal event Action<HotkeyBindings> BindingsChanged;
        internal int QuietVolumePercent => quietVolumePercent;
        internal bool StartupColorEnabled => StartupColorToggle.IsOn && selectedColorProfile != null;

        internal MainWindow(
            IZoomController engine,
            string initialProfileId,
            bool previewMode = false,
            bool soundsEnabled = false,
            bool reduceTransparency = false,
            HotkeyBindings bindings = null,
            bool startWithColorEnabled = true,
            int quietVolumePercent = SettingsStore.DefaultQuietVolumePercent)
        {
            this.engine = engine;
            this.previewMode = previewMode;
            this.soundsEnabled = soundsEnabled;
            this.reduceTransparency = reduceTransparency;
            this.bindings = bindings ?? HotkeyBindings.Defaults();
            this.quietVolumePercent = quietVolumePercent;
            colorProfiles = previewMode
                ? new List<ColorProfile>()
                : ColorProfileStore.Load();
            selectedColorProfile = colorProfiles.FirstOrDefault(p => ColorProfileStore.IdEquals(p.Id, initialProfileId));
            if (selectedColorProfile != null)
                drafts[selectedColorProfile.Id] = engine.ColorFilter;

            InitializeComponent();
            StartupColorToggle.IsOn = startWithColorEnabled;
            Title = "Accessibility Zoom" + (previewMode ? " \u00B7 Preview" : "");

            // Native Mica, composited by the OS — WinUI renders with real alpha,
            // so the backdrop shows through correctly and text stays crisp.
            // With reduced transparency the content paints solid instead.
            if (reduceTransparency)
            {
                RootGrid.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
            }
            else
            {
                SystemBackdrop = new MicaBackdrop();
            }

            SetupAppWindow();

            filterUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            filterUpdateTimer.Tick += (_, __) =>
            {
                if (closed) { filterUpdateTimer.Stop(); return; }
                // Driver applies can be slow; keep pointer movement free of this
                // work. Pointer release and explicit actions still commit immediately.
                if (engine.FilterEnabled && FilterGestureActive()) return;
                FlushFilterEdits();
            };
            settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            settingsSaveTimer.Tick += (_, __) =>
            {
                if (closed) { settingsSaveTimer.Stop(); return; }
                // Never put disk work inside a slider gesture, including a held thumb.
                if (pendingFilterEdit || FilterGestureActive()) return;
                FlushPendingSettings();
            };

            WireControls();
            engine.StateChanged += EngineStateChanged;
            Closed += (_, __) => DisposePanel();
            ContentRoot.Loaded += (_, __) => EntranceStoryboard.Begin();

            UpdateDisplay(false);
        }

        private void SetupAppWindow()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            appWindow = AppWindow.GetFromWindowId(windowId);
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
            appWindow.Resize(new SizeInt32(680, 800));
            CenterOnPrimaryDisplay(windowId);
            appWindow.Closing += (_, e) =>
            {
                if (!exiting)
                {
                    FlushFilterEdits();
                    PersistSettings(engine.ColorFilter);
                    FlushPendingSettings();
                }
                if (!allowClose && !previewMode)
                {
                    // Hide to the tray instead of exiting.
                    e.Cancel = true;
                    appWindow.Hide();
                }
            };
        }

        private void CenterOnPrimaryDisplay(WindowId windowId)
        {
            try
            {
                DisplayArea area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                RectInt32 work = area.WorkArea;
                int x = work.X + Math.Max(0, (work.Width - 680) / 2);
                int y = work.Y + Math.Max(0, (work.Height - 800) / 2);
                appWindow.Move(new PointInt32(x, y));
            }
            catch
            {
                // System placement is an acceptable fallback.
            }
        }

        internal void ShowAndActivate()
        {
            appWindow?.Show();
            Activate();
        }

        private void WireControls()
        {
            ZoomMinusButton.Click += (_, __) => engine.DecreaseZoom();
            ZoomPlusButton.Click += (_, __) => engine.IncreaseZoom();
            ZoomToggleButton.Click += (_, __) => engine.Toggle();

            BuildBindingControls();
            ResetBindingsButton.Click += (_, __) => ApplyBindings(HotkeyBindings.Defaults());
            QuietVolumeSlider.Value = quietVolumePercent;
            SetText(QuietVolumeText, quietVolumePercent + "%");
            QuietVolumeSlider.ValueChanged += (_, args) =>
            {
                quietVolumePercent = (int)args.NewValue;
                SetText(QuietVolumeText, quietVolumePercent + "%");
                PersistSettings(engine.ColorFilter);
            };

            SoundToggle.IsOn = soundsEnabled;
            InitializeWindowsStartupSetting();
            StartupColorToggle.Toggled += (_, __) =>
            {
                if (displayInitialized) PersistSettings(engine.ColorFilter);
            };
            SoundToggle.Toggled += (_, __) =>
            {
                if (displayInitialized) PersistSettings(engine.ColorFilter);
            };

            ProfileComboBox.ItemsSource = colorProfiles;
            ProfileComboBox.SelectionChanged += (_, __) =>
            {
                if (!updatingFilterControls && ProfileComboBox.SelectedItem is ColorProfile profile)
                    SelectProfile(profile, false);
            };

            ColorToggle.Toggled += (_, __) =>
            {
                if (updatingFilterControls) return;
                bool enabled = ColorToggle.IsOn;
                FlushFilterEdits();
                engine.SetFilterEnabled(enabled);
            };

            WireSlider(GammaSlider);
            WireSlider(BrightnessSlider);
            WireSlider(ContrastSlider);
            WireSlider(SaturationSlider);
            WireSlider(ShadowsSlider);
            WireSlider(HighlightsSlider);

            SaveProfileButton.Click += (_, __) => SaveProfileClick();
            RevertProfileButton.Click += (_, __) => RevertProfileClick();
            NewProfileButton.Click += async (_, __) => await NewProfileClickAsync();
            RenameProfileButton.Click += async (_, __) => await RenameProfileClickAsync();
            DeleteProfileButton.Click += async (_, __) => await DeleteProfileClickAsync();

            AppDetailsButton.Click += async (_, __) => await ShowAppDetailsAsync();
            ResetViewButton.Click += (_, __) => ResetView();
            HideButton.Click += (_, __) => { FlushFilterEdits(); appWindow?.Hide(); };
            ExitButton.Click += (_, __) => RequestExit();
            SetupCrosshairs();
        }

        private void BuildBindingControls()
        {
            foreach (HotkeyAction action in HotkeyBindings.Actions)
            {
                if (action == HotkeyAction.ToggleZoom) continue;
                string label = action == HotkeyAction.HoldZoom ? "Zoom" : HotkeyBindings.Label(action);
                Grid row = new() { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap });
                Button button = new() { Content = BindingText(action), MinWidth = 172 };
                AutomationProperties.SetName(button, "Change " + label + " shortcut");
                Grid.SetColumn(button, 1);
                button.Click += async (_, __) => await EditBindingAsync(action);
                row.Children.Add(button);
                bindingButtons.Add(action, button);
                BindingRows.Children.Add(row);
            }
        }

        private string BindingText(HotkeyAction action)
        {
            if (action != HotkeyAction.HoldZoom) return bindings[action].ToString();
            InputBinding hold = bindings[HotkeyAction.HoldZoom];
            InputBinding toggle = bindings[HotkeyAction.ToggleZoom];
            if (hold.Enabled && toggle.Enabled) return $"{hold} (Hold) / {toggle} (Toggle)";
            if (hold.Enabled) return $"{hold} (Hold)";
            if (toggle.Enabled) return $"{toggle} (Toggle)";
            return "Unassigned";
        }

        private async System.Threading.Tasks.Task EditBindingAsync(HotkeyAction action)
        {
            bool editsZoom = action == HotkeyAction.HoldZoom;
            if (editsZoom && !bindings[HotkeyAction.HoldZoom].Enabled && bindings[HotkeyAction.ToggleZoom].Enabled)
                action = HotkeyAction.ToggleZoom;
            InputBinding current = bindings[action];
            List<int> keys = InputBinding.SelectableKeys.ToList();
            if (!keys.Contains(current.Key)) keys.Add(current.Key);
            ComboBox keyBox = new() { Header = "Key or mouse button", HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = keys.Select(InputBinding.KeyName).ToList(), SelectedIndex = keys.IndexOf(current.Key), MaxDropDownHeight = 280 };
            CheckBox ctrl = new() { Content = "Ctrl", IsChecked = current.Modifiers.HasFlag(InputModifiers.Control) };
            CheckBox alt = new() { Content = "Alt", IsChecked = current.Modifiers.HasFlag(InputModifiers.Alt) };
            CheckBox shift = new() { Content = "Shift", IsChecked = current.Modifiers.HasFlag(InputModifiers.Shift) };
            CheckBox win = new() { Content = "Win", IsChecked = current.Modifiers.HasFlag(InputModifiers.Windows) };
            StackPanel modifiers = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
            modifiers.Children.Add(ctrl); modifiers.Children.Add(alt); modifiers.Children.Add(shift); modifiers.Children.Add(win);
            StackPanel content = new() { Spacing = 12 };
            content.Children.Add(keyBox); content.Children.Add(modifiers);
            ComboBox behaviorBox = null;
            if (editsZoom)
            {
                behaviorBox = new ComboBox
                {
                    Header = "Zoom behavior",
                    ItemsSource = new[] { "Hold", "Toggle" },
                    SelectedIndex = action == HotkeyAction.ToggleZoom ? 1 : 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                content.Children.Add(behaviorBox);
                content.Children.Add(new TextBlock
                {
                    Text = "Hold zooms while you press the shortcut. Toggle zooms in on one press and out on the next.",
                    TextWrapping = TextWrapping.Wrap
                });
                if (bindings[HotkeyAction.HoldZoom].Enabled && bindings[HotkeyAction.ToggleZoom].Enabled)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "Current shortcuts: " + BindingText(HotkeyAction.HoldZoom) +
                            ". Apply saves the single shortcut chosen here.",
                        TextWrapping = TextWrapping.Wrap
                    });
                }
            }
            content.Children.Add(new TextBlock { Text = "An existing assignment will swap with this action. Select Unassigned to disable it.",
                TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "Left/right mouse bindings apply outside this control panel so settings stay clickable.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            ContentDialog dialog = new() { Title = editsZoom ? "Zoom shortcut" : HotkeyBindings.Label(action), Content = content,
                PrimaryButtonText = "Apply", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || keyBox.SelectedIndex < 0) return;
            int key = keys[keyBox.SelectedIndex];
            InputModifiers flags = InputModifiers.None;
            if (key != 0)
            {
                if (ctrl.IsChecked == true) flags |= InputModifiers.Control;
                if (alt.IsChecked == true) flags |= InputModifiers.Alt;
                if (shift.IsChecked == true) flags |= InputModifiers.Shift;
                if (win.IsChecked == true) flags |= InputModifiers.Windows;
            }
            InputBinding binding = new(key, flags);
            HotkeyAction zoomAction = behaviorBox?.SelectedIndex == 1 ? HotkeyAction.ToggleZoom : HotkeyAction.HoldZoom;
            ApplyBindings(editsZoom ? bindings.AssignZoom(action, zoomAction, binding) : bindings.Assign(action, binding));
        }

        private void ApplyBindings(HotkeyBindings next)
        {
            bool clearToggle = bindings[HotkeyAction.ToggleZoom].Enabled && !next[HotkeyAction.ToggleZoom].Enabled;
            bindings = next;
            BindingsChanged?.Invoke(next);
            if (clearToggle) engine.SetActive(false);
            engine.SetZoomBinding(next[HotkeyAction.HoldZoom]);
            foreach (var pair in bindingButtons) pair.Value.Content = BindingText(pair.Key);
            RefreshCrosshairShortcut();
            UpdateDisplay(false);
            FlushPendingSettings();
        }

        private void WireSlider(Slider slider)
        {
            slider.ValueChanged += (_, __) => FilterSliderChanged();
            slider.PointerPressed += (_, __) => activeSliderGestures.Add(slider);
            slider.PointerReleased += (_, __) => EndSliderGesture(slider);
            slider.PointerCaptureLost += (_, __) => EndSliderGesture(slider);
        }

        private void EndSliderGesture(Slider slider)
        {
            if (activeSliderGestures.Remove(slider))
                FlushFilterEdits();
        }

        private void UpdateWarnings()
        {
            string warning = string.Join("  ", new[] { engineWarning, persistenceWarning, windowsStartupWarning }
                .Where(message => !string.IsNullOrWhiteSpace(message)));
            WarningBar.Message = warning;
            WarningBar.IsOpen = warning.Length > 0;
        }

        internal void ShowEngineWarning(string warning)
        {
            engineWarning = warning ?? "";
            if (!closed) UpdateWarnings();
        }

        internal void PrepareForExit()
        {
            if (exiting) return;
            StopUpdates();
            FlushCrosshairPreferences();
            FlushFilterEdits();
            PersistSettings(engine.ColorFilter);
            FlushPendingSettings();
            exiting = true;
            allowClose = true;
        }

        internal void CycleFilterProfile()
        {
            if (colorProfiles.Count == 0) return;
            int current = colorProfiles.IndexOf(selectedColorProfile);
            SelectProfile(colorProfiles[(current + 1) % colorProfiles.Count], true);
            try
            {
                presetPopup ??= new PresetPopup();
                presetPopup.Show(selectedColorProfile.Name, engine.DisplayBounds);
            }
            catch (Exception exception) { ShowEngineWarning("Preset popup unavailable: " + exception.Message); }
        }

        private void SelectProfile(ColorProfile profile, bool enableFilter)
        {
            if (profile == null) return;
            FlushFilterEdits();
            if (selectedColorProfile != null) drafts[selectedColorProfile.Id] = engine.ColorFilter;
            selectedColorProfile = profile;
            updatingFilterControls = true;
            ProfileComboBox.SelectedItem = profile;
            updatingFilterControls = false;
            engine.SetColorFilter(drafts.TryGetValue(profile.Id, out ColorFilterSettings draft) ? draft : profile.Settings);
            if (enableFilter) engine.SetFilterEnabled(true);
            UpdateDisplay(false);
        }

        private void SaveProfileClick()
        {
            FlushFilterEdits();
            if (selectedColorProfile == null) return;
            ColorFilterSettings previous = selectedColorProfile.Settings;
            selectedColorProfile.Settings = engine.ColorFilter;
            if (!SaveProfiles())
                selectedColorProfile.Settings = previous;
            else
                drafts.Remove(selectedColorProfile.Id);
            UpdateDisplay(false);
        }

        private void RevertProfileClick()
        {
            if (selectedColorProfile == null) return;
            filterUpdateTimer.Stop();
            pendingFilterEdit = false;
            drafts.Remove(selectedColorProfile.Id);
            engine.SetColorFilter(selectedColorProfile.Settings);
            UpdateDisplay(false);
        }

        private async System.Threading.Tasks.Task NewProfileClickAsync()
        {
            string name = await PromptForProfileNameAsync("New color profile", "New profile", null);
            if (name == null || closed || exiting) return;
            FlushFilterEdits();
            ColorProfile profile = ColorProfileStore.CreateUserProfile(name, engine.ColorFilter);
            colorProfiles.Add(profile);
            if (!SaveProfiles()) { colorProfiles.Remove(profile); return; }
            if (selectedColorProfile != null) drafts[selectedColorProfile.Id] = engine.ColorFilter;
            selectedColorProfile = profile;
            RefreshProfileItems();
            UpdateDisplay(false);
        }

        private async System.Threading.Tasks.Task RenameProfileClickAsync()
        {
            ColorProfile profile = selectedColorProfile;
            if (profile == null) return;
            string name = await PromptForProfileNameAsync(
                "Rename color profile", profile.Name, profile);
            if (name == null || closed || exiting) return;
            string previousName = profile.Name;
            profile.Name = name;
            if (!SaveProfiles()) profile.Name = previousName;
            RefreshProfileItems();
            UpdateDisplay(false);
        }

        private async System.Threading.Tasks.Task DeleteProfileClickAsync()
        {
            ColorProfile deleted = selectedColorProfile;
            if (deleted == null) return;
            ContentDialog confirm = new ContentDialog
            {
                Title = "Delete color profile",
                Content = "Delete the profile '" + deleted.Name + "'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary || closed || exiting) return;
            FlushFilterEdits();
            int index = colorProfiles.IndexOf(deleted);
            colorProfiles.Remove(deleted);
            if (!SaveProfiles()) { colorProfiles.Insert(index, deleted); return; }
            drafts.Remove(deleted.Id);
            if (ReferenceEquals(selectedColorProfile, deleted)) selectedColorProfile = null;
            RefreshProfileItems();
            UpdateDisplay(false);
        }

        // Returns the validated name, or null when cancelled.
        private async System.Threading.Tasks.Task<string> PromptForProfileNameAsync(
            string title, string initialName, ColorProfile profileBeingRenamed)
        {
            StackPanel panel = new StackPanel { Spacing = 8 };
            TextBox nameBox = new TextBox
            {
                Header = "Profile name",
                Text = initialName ?? string.Empty,
                MaxLength = 60,
            };
            TextBlock errorText = new TextBlock
            {
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                FontSize = 12,
                Visibility = Visibility.Collapsed,
            };
            panel.Children.Add(nameBox);
            panel.Children.Add(errorText);

            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            dialog.Opened += (_, __) =>
            {
                nameBox.Focus(FocusState.Programmatic);
                nameBox.SelectAll();
            };
            string accepted = null;
            dialog.PrimaryButtonClick += (_, e) =>
            {
                string candidate = ColorProfileStore.NormalizeName(nameBox.Text);
                if (candidate.Length == 0)
                {
                    errorText.Text = "Enter a profile name.";
                    errorText.Visibility = Visibility.Visible;
                    e.Cancel = true;
                    return;
                }
                if (colorProfiles.Any(profile =>
                    !ReferenceEquals(profile, profileBeingRenamed) &&
                    string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    errorText.Text = "A profile with that name already exists.";
                    errorText.Visibility = Visibility.Visible;
                    e.Cancel = true;
                    return;
                }
                accepted = candidate;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return null;
            return accepted;
        }

        private bool SaveProfiles()
        {
            if (previewMode) return true;
            if (ColorProfileStore.Save(colorProfiles, out string errorMessage)) return true;
            _ = ShowMessageAsync("The color profiles could not be saved.\n\n" + errorMessage, "Accessibility Zoom");
            return false;
        }

        private async System.Threading.Tasks.Task ShowMessageAsync(string message, string title)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private void RefreshProfileItems()
        {
            updatingFilterControls = true;
            ProfileComboBox.ItemsSource = null;
            ProfileComboBox.ItemsSource = colorProfiles;
            ProfileComboBox.SelectedItem = selectedColorProfile;
            updatingFilterControls = false;
        }

        private void FilterSliderChanged()
        {
            if (updatingFilterControls) return;
            pendingFilterEdit = true;
            UpdateFilterValues(ReadFilterControls());
            // Keep the first scheduled tick: continuous input gets a live update at
            // most every 33 ms instead of waiting until the pointer stops moving.
            if (!filterUpdateTimer.IsEnabled) filterUpdateTimer.Start();
            if (settingsSaveTimer.IsEnabled) { settingsSaveTimer.Stop(); settingsSaveTimer.Start(); }
        }

        private ColorFilterSettings ReadFilterControls()
        {
            ColorFilterSettings settings = engine.ColorFilter;
            settings.Gamma = (float)GammaSlider.Value / 100f;
            settings.Brightness = (float)BrightnessSlider.Value / 100f;
            settings.Contrast = (float)ContrastSlider.Value / 100f;
            settings.Saturation = (float)SaturationSlider.Value / 100f;
            settings.ShadowBoost = (float)ShadowsSlider.Value / 100f;
            settings.HighlightProtection = (float)HighlightsSlider.Value / 100f;
            return settings;
        }

        internal void FlushFilterEdits()
        {
            filterUpdateTimer.Stop();
            if (!pendingFilterEdit || updatingFilterControls || closed) return;
            ColorFilterSettings settings = ReadFilterControls();
            pendingFilterEdit = false;
            if (selectedColorProfile != null) drafts[selectedColorProfile.Id] = settings;
            applyingFilterEdit = true;
            try { engine.SetColorFilter(settings); }
            finally { applyingFilterEdit = false; }
            UpdateDisplay(false);
        }

        private void EngineStateChanged(object sender, EventArgs e)
        {
            if (closed || exiting) return;
            DispatcherQueue queue = DispatcherQueue;
            if (queue.HasThreadAccess)
            {
                if (!applyingFilterEdit) UpdateDisplay(true);
                return;
            }
            // State is read when the queued callback runs. A burst of renderer
            // notifications therefore needs only one UI message.
            if (Interlocked.Exchange(ref queuedEngineDisplayUpdate, 1) != 0) return;
            if (!queue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref queuedEngineDisplayUpdate, 0);
                if (!closed && !exiting) UpdateDisplay(true);
            }))
            {
                Interlocked.Exchange(ref queuedEngineDisplayUpdate, 0);
            }
        }

        private void UpdateDisplay(bool playSound)
        {
            if (closed || exiting) return;
            bool active = engine.Active;
            if (playSound && displayInitialized && previousActive != active && SoundToggle.IsOn == true)
                scopeSounds.Play(active);
            previousActive = active;
            displayInitialized = true;

            ColorFilterSettings settings = engine.ColorFilter;
            UpdateControlPage(active, settings);
            PersistSettings(settings);
            UpdateWarnings();
            crosshairOverlay?.Refresh(true);
        }

        private void UpdateControlPage(bool active, ColorFilterSettings settings)
        {
            float zoom = engine.Zoom;
            SetText(ZoomStatusText, active ? "Zoom is on" : "Normal view");
            SetText(ZoomToggleButton, active ? "Turn zoom off" : "Turn zoom on");
            AutomationProperties.SetName(ZoomToggleButton, active ? "Turn magnification off" : "Turn magnification on");
            SetText(ZoomReadoutText, zoom.ToString("0.00") + "\u00D7");
            ZoomMinusButton.IsEnabled = zoom > 1.25f;
            ZoomPlusButton.IsEnabled = zoom < 8f;
            SetText(ZoomBindingText, "Zoom shortcut: " + BindingText(HotkeyAction.HoldZoom) + " \u00B7 Change in Settings below");

            // Background zoom events cannot replace a not-yet-applied slider edit.
            if (!pendingFilterEdit)
            {
                updatingFilterControls = true;
                SetSliderValue(GammaSlider, settings.Gamma * 100f);
                SetSliderValue(BrightnessSlider, settings.Brightness * 100f);
                SetSliderValue(ContrastSlider, settings.Contrast * 100f);
                SetSliderValue(SaturationSlider, settings.Saturation * 100f);
                SetSliderValue(ShadowsSlider, settings.ShadowBoost * 100f);
                SetSliderValue(HighlightsSlider, settings.HighlightProtection * 100f);
                updatingFilterControls = false;
            }
            updatingFilterControls = true;
            if (!Equals(ProfileComboBox.SelectedItem, selectedColorProfile))
                ProfileComboBox.SelectedItem = selectedColorProfile;
            SetToggle(ColorToggle, engine.FilterEnabled);
            updatingFilterControls = false;
            UpdateFilterValues(pendingFilterEdit ? ReadFilterControls() : settings);
            ProfileComboBox.IsEnabled = colorProfiles.Count > 0;
            bool canManageProfile = selectedColorProfile != null;
            RenameProfileButton.IsEnabled = canManageProfile;
            DeleteProfileButton.IsEnabled = canManageProfile;
            bool filterEnabled = engine.FilterEnabled, filterAvailable = engine.FilterAvailable;
            ColorToggle.IsEnabled = filterAvailable || filterEnabled;
            SetText(FilterStatusText, filterEnabled ? "Color is on" :
                filterAvailable ? "Color is off" : "Color unavailable");
        }

        private void UpdateFilterValues(ColorFilterSettings settings)
        {
            SetText(GammaValueText, settings.Gamma.ToString("0.00"));
            SetText(BrightnessValueText, (settings.Brightness > 0 ? "+" : "") + Percent(settings.Brightness));
            SetText(ContrastValueText, Percent(settings.Contrast));
            SetText(SaturationValueText, Percent(settings.Saturation));
            SetText(ShadowsValueText, Percent(settings.ShadowBoost));
            SetText(HighlightsValueText, Percent(settings.HighlightProtection));
            bool dirty = selectedColorProfile != null && !selectedColorProfile.Settings.NearlyEquals(settings);
            SetText(ProfileStateText, selectedColorProfile == null ? "Use New profile to save these color settings." :
                dirty ? "Edited \u00B7 changes not saved to profile" : "Saved \u00B7 " + selectedColorProfile.Name);
            SaveProfileButton.IsEnabled = dirty;
            RevertProfileButton.IsEnabled = dirty;
        }

        private void PersistSettings(ColorFilterSettings settings)
        {
            if (previewMode || closed || exiting) return;
            AppSettings next = new AppSettings(engine.Zoom, engine.ThumbButton, engine.FilterEnabled,
                settings, selectedColorProfile?.Id ?? "")
            { SoundsEnabled = SoundToggle.IsOn == true, ReduceTransparency = reduceTransparency, Bindings = bindings,
                StartWithColorEnabled = StartupColorToggle.IsOn, FilterMonitorDevice = engine.FilterMonitorDevice,
                QuietVolumePercent = quietVolumePercent };
            if (settingsSaved && SameSettings(lastSaved, next))
            {
                pendingSettings = null;
                settingsSaveTimer.Stop();
                return;
            }
            if (SameSettings(pendingSettings, next)) return;
            pendingSettings = next;
            settingsSaveTimer.Stop();
            settingsSaveTimer.Start();
        }

        private void FlushPendingSettings()
        {
            settingsSaveTimer.Stop();
            if (previewMode || pendingSettings == null) return;
            AppSettings next = pendingSettings;
            pendingSettings = null;
            if (SettingsStore.Save(next.Zoom, next.ThumbButton, next.FilterEnabled, next.ColorFilter,
                next.SelectedProfileId, next.SoundsEnabled, next.ReduceTransparency, out string error, bindings: next.Bindings,
                startWithColorEnabled: next.StartWithColorEnabled, filterMonitorDevice: next.FilterMonitorDevice,
                quietVolumePercent: next.QuietVolumePercent))
            {
                settingsSaved = true;
                lastSaved = next;
                persistenceWarning = "";
            }
            else
            {
                settingsSaved = false;
                persistenceWarning = "Settings could not be saved: " + error;
            }
            if (!closed) UpdateWarnings();
        }

        private static bool SameSettings(AppSettings left, AppSettings right) => left != null && right != null &&
            left.Zoom == right.Zoom && left.ThumbButton == right.ThumbButton && left.FilterEnabled == right.FilterEnabled &&
            left.ColorFilter.NearlyEquals(right.ColorFilter) && left.SelectedProfileId == right.SelectedProfileId &&
            left.SoundsEnabled == right.SoundsEnabled && left.ReduceTransparency == right.ReduceTransparency &&
            left.StartWithColorEnabled == right.StartWithColorEnabled && left.FilterMonitorDevice == right.FilterMonitorDevice &&
            left.QuietVolumePercent == right.QuietVolumePercent && left.Bindings.SameAs(right.Bindings);

        private bool FilterGestureActive() => activeSliderGestures.Count > 0;

        private static void SetText(TextBlock block, string text) { if (block.Text != text) block.Text = text; }
        private static void SetText(Button button, string text)
        {
            if (!string.Equals(button.Content as string, text, StringComparison.Ordinal))
                button.Content = text;
        }
        private static void SetToggle(ToggleSwitch toggle, bool isOn)
        {
            if (toggle.IsOn != isOn) toggle.IsOn = isOn;
        }
        private static void SetSliderValue(Slider slider, float value)
        {
            double next = Math.Clamp(Math.Round(value), slider.Minimum, slider.Maximum);
            if (slider.Value != next) slider.Value = next;
        }

        private static string Percent(float value) => Math.Round(value * 100).ToString("0") + "%";

        internal void ResetView()
        {
            FlushFilterEdits();
            engine.SetActive(false);
            engine.SetFilterEnabled(false);
        }

        private async System.Threading.Tasks.Task ShowAppDetailsAsync()
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "Accessibility Zoom",
                Content = engine.RendererName + "\n\nColor: " + engine.ActiveFilterRenderer +
                    "\n\nAccessibility Zoom 4.3.14\nSettings, profiles, and imported crosshairs are stored locally.",
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private void RequestExit()
        {
            FlushFilterEdits();
            if (previewMode)
            {
                allowClose = true;
                Close();
            }
            else
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void DisposePanel()
        {
            if (closed) return;
            StopUpdates();
            closed = true;
            engine.StateChanged -= EngineStateChanged;
            filterUpdateTimer.Stop();
            settingsSaveTimer.Stop();
            FlushPendingSettings();
            scopeSounds.Dispose();
            DisposeCrosshairs();
            presetPopup?.Dispose();
        }
    }
}
