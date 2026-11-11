using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AccessibilityZoom
{
    public sealed partial class MainWindow
    {
        private readonly CrosshairStore crosshairStore = new();
        private readonly CancellationTokenSource crosshairLifetime = new();
        private List<CrosshairAsset> crosshairAssets;
        private CrosshairPreferences crosshairPreferences;
        private System.Drawing.Bitmap crosshairBitmap;
        private CrosshairOverlay crosshairOverlay;
        private DispatcherTimer crosshairSaveTimer;
        private bool updatingCrosshairControls, crosshairBusy, crosshairSettingsDirty;

        private void SetupCrosshairs()
        {
            string assetsWarning = "", preferencesWarning = "", repairNote = "";
            Dictionary<string, string> repaired = new();
            if (!previewMode && CrosshairImporter.TryReadCrosshairXScales(out double staleScale, out double profileScale))
            {
                try { repaired = crosshairStore.RepairCrosshairXScale(staleScale, profileScale); }
                catch (Exception exception) { repairNote = "Old Crosshair X imports could not be resized: " + exception.Message; }
                if (repaired.Count > 0) repairNote = repaired.Count + " Crosshair X import(s) resized to match Crosshair X.";
            }
            crosshairAssets = previewMode ? new() { CrosshairAsset.Default } : crosshairStore.LoadAssets(out assetsWarning);
            crosshairPreferences = previewMode ? new() : crosshairStore.LoadPreferences(out preferencesWarning);
            if (crosshairPreferences.SelectedId != null && repaired.TryGetValue(crosshairPreferences.SelectedId, out string newId))
            {
                crosshairPreferences = crosshairPreferences with { SelectedId = newId };
                crosshairSettingsDirty = true;
            }
            if (repairNote.Length > 0) assetsWarning = string.Join(" ", new[] { repairNote, assetsWarning }.Where(text => text.Length > 0));
            CrosshairAsset selected = crosshairAssets.FirstOrDefault(asset => asset.Id == crosshairPreferences.SelectedId);
            if (selected == null)
            {
                selected = crosshairAssets[0];
                crosshairPreferences = crosshairPreferences with { SelectedId = selected.Id, Enabled = false };
                assetsWarning = "The saved crosshair was missing. Choose a design to enable it again.";
            }
            try { crosshairBitmap = StaticCrosshairRasterizer.Render(selected); }
            catch (Exception exception)
            {
                selected = crosshairAssets[0];
                crosshairBitmap = StaticCrosshairRasterizer.Render(selected);
                crosshairPreferences = crosshairPreferences with { SelectedId = selected.Id, Enabled = false };
                assetsWarning = "The selected design could not be loaded: " + exception.Message;
            }
            crosshairSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            crosshairSaveTimer.Tick += (_, __) => FlushCrosshairPreferences();

            CrosshairAssetBox.SelectionChanged += async (_, __) =>
            {
                if (!updatingCrosshairControls && !crosshairBusy && CrosshairAssetBox.SelectedItem is CrosshairAsset asset)
                    await RunCrosshairOperationAsync(async () => await SelectCrosshairAsync(asset, false));
            };
            CrosshairToggle.Toggled += (_, __) =>
            {
                if (updatingCrosshairControls) return;
                crosshairPreferences = crosshairPreferences with { Enabled = CrosshairToggle.IsOn };
                ApplyCrosshairPreferences();
            };
            CrosshairMonitorBox.SelectionChanged += (_, __) =>
            {
                if (updatingCrosshairControls || CrosshairMonitorBox.SelectedItem is not DisplayChoice display) return;
                crosshairPreferences = crosshairPreferences with { MonitorDevice = display.DeviceName };
                ApplyCrosshairPreferences();
            };
            CrosshairMonitorBox.DropDownOpened += (_, __) => RefreshCrosshairMonitors();
            CrosshairSizeSlider.ValueChanged += (_, __) =>
            {
                if (updatingCrosshairControls) return;
                crosshairPreferences = crosshairPreferences with { SizePercent = (int)Math.Round(CrosshairSizeSlider.Value) };
                ApplyCrosshairPreferences();
            };
            CrosshairOpacitySlider.ValueChanged += (_, __) =>
            {
                if (updatingCrosshairControls) return;
                crosshairPreferences = crosshairPreferences with { OpacityPercent = (int)Math.Round(CrosshairOpacitySlider.Value) };
                ApplyCrosshairPreferences();
            };
            ImportCrosshairImageButton.Click += async (_, __) => await PickCrosshairImageAsync();
            ImportCrosshairCodeButton.Click += async (_, __) => await PromptCrosshairCodeAsync();
            ImportCrosshairLibraryButton.Click += async (_, __) => await PickCrosshairLibraryAsync();
            DeleteCrosshairButton.Click += async (_, __) => await DeleteSelectedCrosshairAsync();
            RefreshCrosshairControls();
            FlushCrosshairPreferences();
            RefreshCrosshairMonitors();
            ConfigureCrosshairOverlay();
            _ = UpdateCrosshairPreviewAsync(CrosshairPreview, crosshairBitmap);
            string warning = string.Join(" ", new[] { assetsWarning, preferencesWarning }.Where(text => text.Length > 0));
            SetText(CrosshairStatusText, warning.Length > 0 ? warning :
                "Static crosshair · centered on the selected monitor · imports stay available offline");
        }

        internal void ToggleCrosshair()
        {
            if (closed || crosshairPreferences == null) return;
            crosshairPreferences = crosshairPreferences with { Enabled = !crosshairPreferences.Enabled };
            ApplyCrosshairPreferences();
        }

        private void ApplyCrosshairPreferences()
        {
            crosshairPreferences = crosshairPreferences.Clamped();
            ConfigureCrosshairOverlay();
            RefreshCrosshairControls();
            crosshairSettingsDirty = true;
            crosshairSaveTimer.Stop();
            crosshairSaveTimer.Start();
        }

        private void ConfigureCrosshairOverlay()
        {
            if (previewMode || crosshairBitmap == null) return;
            try
            {
                if (crosshairOverlay == null && crosshairPreferences.Enabled)
                {
                    crosshairOverlay = new CrosshairOverlay();
                    crosshairOverlay.Failed += message =>
                    {
                        crosshairPreferences = crosshairPreferences with { Enabled = false };
                        SetText(CrosshairStatusText, "Crosshair hidden: " + message);
                        RefreshCrosshairControls();
                        crosshairSettingsDirty = true;
                        crosshairSaveTimer.Start();
                    };
                }
                crosshairOverlay?.Configure(crosshairBitmap, crosshairPreferences);
            }
            catch (Exception exception)
            {
                crosshairPreferences = crosshairPreferences with { Enabled = false };
                SetText(CrosshairStatusText, "Crosshair could not be shown: " + exception.Message);
            }
        }

        private void RefreshCrosshairControls()
        {
            updatingCrosshairControls = true;
            try
            {
                CrosshairAsset selected = crosshairAssets.FirstOrDefault(asset => asset.Id == crosshairPreferences.SelectedId) ?? crosshairAssets[0];
                CrosshairAssetBox.ItemsSource = null;
                CrosshairAssetBox.ItemsSource = crosshairAssets;
                CrosshairAssetBox.SelectedItem = selected;
                DeleteCrosshairButton.IsEnabled = !crosshairBusy && selected.Id != CrosshairAsset.Default.Id;
                CrosshairToggle.IsOn = crosshairPreferences.Enabled;
                CrosshairSizeSlider.Value = crosshairPreferences.SizePercent;
                CrosshairOpacitySlider.Value = crosshairPreferences.OpacityPercent;
                SetText(CrosshairSizeText, crosshairPreferences.SizePercent + "%");
                SetText(CrosshairOpacityText, crosshairPreferences.OpacityPercent + "%");
                RefreshCrosshairShortcut();
            }
            finally { updatingCrosshairControls = false; }
        }

        private void RefreshCrosshairShortcut() => SetText(CrosshairShortcutText,
            "Toggle: " + bindings[HotkeyAction.ToggleCrosshair] + " · Change in Settings below");

        private void RefreshCrosshairMonitors()
        {
            bool wasUpdating = updatingCrosshairControls;
            updatingCrosshairControls = true;
            try
            {
                var displays = DisplayHelper.GetDisplays();
                CrosshairMonitorBox.ItemsSource = displays;
                CrosshairMonitorBox.SelectedItem = displays.FirstOrDefault(item => item.DeviceName == crosshairPreferences.MonitorDevice)
                    ?? displays.FirstOrDefault(item => item.Primary);
            }
            finally { updatingCrosshairControls = wasUpdating; }
        }

        private void FlushCrosshairPreferences()
        {
            crosshairSaveTimer?.Stop();
            if (previewMode || !crosshairSettingsDirty || crosshairPreferences == null) return;
            try { crosshairStore.SavePreferences(crosshairPreferences); crosshairSettingsDirty = false; }
            catch (Exception exception) { if (!closed) SetText(CrosshairStatusText, "Crosshair settings could not be saved: " + exception.Message); }
        }

        private async Task PickCrosshairImageAsync()
        {
            await RunCrosshairOperationAsync(async () =>
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(appWindow.Id);
                foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }) picker.FileTypeFilter.Add(extension);
                var file = await picker.PickSingleFileAsync();
                if (file == null || closed) return;
                CrosshairAsset asset = await Task.Run(() => CrosshairImporter.ImportImage(file.Path), crosshairLifetime.Token);
                await AddCrosshairAsync(asset);
            });
        }

        private async Task PromptCrosshairCodeAsync()
        {
            await RunCrosshairOperationAsync(async () =>
            {
                TextBox input = new() { Header = "Crosshair X code or share link", PlaceholderText = "crosshairx.gg/s/…", MinWidth = 320 };
                StackPanel content = new() { Spacing = 12 };
                content.Children.Add(input);
                content.Children.Add(new TextBlock { Text = "The design downloads once and is saved for offline use. Static appearance is imported.", TextWrapping = TextWrapping.Wrap });
                ContentDialog dialog = new() { Title = "Import Crosshair X code", Content = content, PrimaryButtonText = "Import",
                    CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot, IsPrimaryButtonEnabled = false };
                input.TextChanged += (_, __) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
                SetText(CrosshairStatusText, "Downloading crosshair…");
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(crosshairLifetime.Token);
                cancellation.CancelAfter(TimeSpan.FromSeconds(60));
                string code = input.Text;
                CrosshairAsset asset = await Task.Run(() => CrosshairImporter.ImportShareAsync(code, cancellation.Token), cancellation.Token);
                await AddCrosshairAsync(asset);
            });
        }

        private async Task PickCrosshairLibraryAsync()
        {
            await RunCrosshairOperationAsync(async () =>
            {
                var designs = await Task.Run(() => CrosshairImporter.FindLocalDesigns(), crosshairLifetime.Token);
                if (designs.Count == 0) throw new FileNotFoundException("No saved Crosshair X designs were found. Use Load image or Paste code / link instead.");
                ListView list = new() { ItemsSource = designs, SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 240, MinWidth = 320 };
                list.SelectedItems.Add(designs[0]);
                Image preview = new() { Height = 96, Width = 120, Stretch = Stretch.Uniform };
                TextBlock previewStatus = new() { Text = "Select one or more designs to import.", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                Button selectAll = new() { Content = "Select all" };
                selectAll.Click += (_, __) => list.SelectAll();
                StackPanel content = new() { Spacing = 10 };
                content.Children.Add(selectAll); content.Children.Add(list);
                content.Children.Add(new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 41, 46, 57)), Child = preview, Padding = new Thickness(12) });
                content.Children.Add(previewStatus);
                ContentDialog dialog = new() { Title = "Saved Crosshair X designs", Content = content, PrimaryButtonText = "Import selected",
                    CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
                using var previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(crosshairLifetime.Token);
                int previewVersion = 0;
                async Task PreviewAsync(LocalCrosshair design)
                {
                    int version = ++previewVersion;
                    try
                    {
                        var asset = await Task.Run(() => CrosshairImporter.ImportLocalAsync(design, previewCancellation.Token), previewCancellation.Token);
                        using var bitmap = await Task.Run(() => StaticCrosshairRasterizer.Render(asset), previewCancellation.Token);
                        if (version != previewVersion || previewCancellation.IsCancellationRequested || closed) return;
                        await UpdateCrosshairPreviewAsync(preview, bitmap);
                        previewStatus.Text = design.Name;
                    }
                    catch (Exception exception)
                    { if (version == previewVersion && !previewCancellation.IsCancellationRequested && !closed) previewStatus.Text = "Preview unavailable: " + exception.Message; }
                }
                list.SelectionChanged += async (_, e) =>
                {
                    dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
                    if (e.AddedItems.LastOrDefault() is LocalCrosshair design) await PreviewAsync(design);
                };
                _ = PreviewAsync(designs[0]);
                ContentDialogResult result = await dialog.ShowAsync();
                previewCancellation.Cancel();
                previewVersion++;
                if (result != ContentDialogResult.Primary || closed) return;
                var selected = list.SelectedItems.Cast<LocalCrosshair>().ToList();
                List<string> failures = new();
                CrosshairAsset last = null;
                int imported = 0;
                foreach (LocalCrosshair design in selected)
                {
                    crosshairLifetime.Token.ThrowIfCancellationRequested();
                    SetText(CrosshairStatusText, "Importing " + (imported + failures.Count + 1) + " of " + selected.Count + "…");
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(crosshairLifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(45));
                        CrosshairAsset asset = await Task.Run(() => CrosshairImporter.ImportLocalAsync(design, timeout.Token), timeout.Token);
                        await ValidateAndSaveCrosshairAsync(asset);
                        last = crosshairAssets.First(item => item.Id == asset.Id);
                        imported++;
                    }
                    catch (OperationCanceledException) when (crosshairLifetime.IsCancellationRequested) { throw; }
                    catch (Exception exception) { failures.Add(design.Name + ": " + exception.Message); }
                }
                if (last != null) await SelectCrosshairAsync(last, true);
                SetText(CrosshairStatusText, "Imported " + imported + " design(s)." +
                    (failures.Count == 0 ? " Saved locally for offline use." : " " + failures.Count + " skipped. " + failures[0]));
            });
        }

        private async Task DeleteSelectedCrosshairAsync()
        {
            await RunCrosshairOperationAsync(async () =>
            {
                if (CrosshairAssetBox.SelectedItem is not CrosshairAsset asset || asset.Id == CrosshairAsset.Default.Id) return;
                ContentDialog dialog = new()
                {
                    Title = "Delete crosshair?",
                    Content = "\"" + asset.Name + "\" will be removed from this app. Crosshair X files stay untouched.",
                    PrimaryButtonText = "Delete", CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
                if (!previewMode) crosshairStore.DeleteAsset(asset.Id);
                crosshairAssets.RemoveAll(item => item.Id == asset.Id);
                if (crosshairPreferences.SelectedId == asset.Id)
                    await SelectCrosshairAsync(crosshairAssets[0], false);
                else RefreshCrosshairControls();
                SetText(CrosshairStatusText, "Deleted " + asset.Name + ".");
            });
        }

        private async Task ValidateAndSaveCrosshairAsync(CrosshairAsset asset)
        {
            bool exists = crosshairAssets.Any(item => item.Id == asset.Id);
            if (!exists && crosshairAssets.Count >= 101) throw new InvalidOperationException("The crosshair library is full (100 imported designs).");
            await Task.Run(() =>
            {
                using var bitmap = StaticCrosshairRasterizer.Render(asset);
                if (!previewMode && !exists) crosshairStore.SaveAsset(asset);
            }, crosshairLifetime.Token);
            crosshairLifetime.Token.ThrowIfCancellationRequested();
            if (!exists) crosshairAssets.Add(asset);
        }

        private async Task AddCrosshairAsync(CrosshairAsset asset)
        {
            await ValidateAndSaveCrosshairAsync(asset);
            await SelectCrosshairAsync(crosshairAssets.First(item => item.Id == asset.Id), true);
            SetText(CrosshairStatusText, "Loaded " + asset.Name + " · saved locally for offline use");
        }

        private async Task SelectCrosshairAsync(CrosshairAsset asset, bool enable)
        {
            System.Drawing.Bitmap bitmap = await Task.Run(() => StaticCrosshairRasterizer.Render(asset), crosshairLifetime.Token);
            if (closed) { bitmap.Dispose(); return; }
            System.Drawing.Bitmap previous = crosshairBitmap;
            crosshairBitmap = bitmap;
            crosshairPreferences = crosshairPreferences with { SelectedId = asset.Id, Enabled = enable || crosshairPreferences.Enabled };
            ApplyCrosshairPreferences();
            previous?.Dispose();
            await UpdateCrosshairPreviewAsync(CrosshairPreview, bitmap);
            FlushCrosshairPreferences();
        }

        private async Task RunCrosshairOperationAsync(Func<Task> action)
        {
            if (crosshairBusy || closed) return;
            crosshairBusy = true;
            SetCrosshairImportControls(false);
            try { await action(); }
            catch (OperationCanceledException) { if (!closed) SetText(CrosshairStatusText, "Crosshair import timed out. Try again."); }
            catch (Exception exception)
            {
                if (!closed) { SetText(CrosshairStatusText, "Crosshair import failed: " + exception.Message); RefreshCrosshairControls(); }
            }
            finally
            {
                crosshairBusy = false;
                if (!closed) SetCrosshairImportControls(true);
            }
        }

        private void SetCrosshairImportControls(bool enabled)
        {
            CrosshairAssetBox.IsEnabled = enabled;
            ImportCrosshairImageButton.IsEnabled = enabled;
            ImportCrosshairCodeButton.IsEnabled = enabled;
            ImportCrosshairLibraryButton.IsEnabled = enabled;
            DeleteCrosshairButton.IsEnabled = enabled && CrosshairAssetBox.SelectedItem is CrosshairAsset selected &&
                selected.Id != CrosshairAsset.Default.Id;
        }

        private static async Task UpdateCrosshairPreviewAsync(Image preview, System.Drawing.Bitmap bitmap)
        {
            try
            {
                using MemoryStream png = new();
                bitmap.Save(png, ImageFormat.Png);
                using InMemoryRandomAccessStream stream = new();
                using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(png.ToArray());
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                BitmapImage source = new();
                await source.SetSourceAsync(stream);
                preview.Source = source;
            }
            catch { /* Preview failure must not hide an otherwise valid crosshair. */ }
        }

        private void DisposeCrosshairs()
        {
            crosshairLifetime.Cancel();
            FlushCrosshairPreferences();
            crosshairOverlay?.Dispose();
            crosshairBitmap?.Dispose();
            crosshairLifetime.Dispose();
        }
    }
}
