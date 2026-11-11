using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Velopack;

namespace AccessibilityZoom
{
    public sealed partial class MainWindow
    {
        private AppUpdateService updates;
        private UpdatePreferences updatePreferences;
        private UpdateInfo availableUpdate;
        private bool updateBusy;
        private bool restoringUpdatePreference;
        private readonly CancellationTokenSource updateCancellation = new();

        internal void InitializeUpdates()
        {
            UpdateVersionText.Text = "Version " + typeof(App).Assembly.GetName().Version?.ToString(3);
            if (previewMode) return;
            try
            {
                updates = new AppUpdateService();
                updatePreferences = UpdatePreferences.Load();
                AutomaticUpdatesToggle.IsOn = updatePreferences.CheckAutomatically;
                AutomaticUpdatesToggle.IsEnabled = updates.Available;
                CheckUpdatesButton.IsEnabled = updates.Available;
                UpdateStatusText.Text = updates.Available
                    ? "Updates are installed only after you confirm. Settings and saved designs are kept."
                    : updates.UnavailableReason;
                if (updates.RepositoryUrl != null)
                {
                    UpdateReleasesLink.NavigateUri = new Uri(updates.RepositoryUrl + "/releases");
                    UpdateReleasesLink.Visibility = Visibility.Visible;
                }
                AutomaticUpdatesToggle.Toggled += (_, __) =>
                {
                    if (restoringUpdatePreference) return;
                    bool previous = updatePreferences.CheckAutomatically;
                    updatePreferences.CheckAutomatically = AutomaticUpdatesToggle.IsOn;
                    try { updatePreferences.Save(); }
                    catch (Exception error)
                    {
                        updatePreferences.CheckAutomatically = previous;
                        restoringUpdatePreference = true;
                        AutomaticUpdatesToggle.IsOn = previous;
                        restoringUpdatePreference = false;
                        UpdateStatusText.Text = "Update preference could not be saved: " + error.Message;
                    }
                };
                CheckUpdatesButton.Click += async (_, __) => await CheckUpdatesAsync(true);
                InstallUpdateButton.Click += async (_, __) => await InstallUpdateAsync();
                UpdateNoticeButton.Click += async (_, __) => await InstallUpdateAsync();
                if (!updates.Available) return;
                _ = CheckUpdatesAfterStartupAsync();
            }
            catch (Exception error) { UpdateStatusText.Text = "Updates unavailable: " + error.Message; }
        }

        private async Task CheckUpdatesAfterStartupAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), updateCancellation.Token);
                if (updatePreferences.CheckAutomatically) await CheckUpdatesAsync(false);
            }
            catch (OperationCanceledException) { }
        }

        internal void OpenUpdates()
        {
            ShowAndActivate();
            _ = CheckUpdatesAsync(true);
        }

        private async Task CheckUpdatesAsync(bool manual)
        {
            if (closed || exiting || updateBusy || updates?.Available != true) return;
            updateBusy = true;
            CheckUpdatesButton.IsEnabled = false;
            try
            {
                if (manual) UpdateStatusText.Text = "Checking GitHub for updates...";
                UpdateInfo result = await updates.Manager.CheckForUpdatesAsync();
                if (closed || exiting) return;
                availableUpdate = result;
                InstallUpdateButton.Visibility = result == null ? Visibility.Collapsed : Visibility.Visible;
                UpdateNotice.IsOpen = result != null;
                if (result != null)
                {
                    string version = result.TargetFullRelease.Version.ToString();
                    UpdateNotice.Message = "Version " + version + " is available. Install when you are ready to restart.";
                    UpdateStatusText.Text = UpdateNotice.Message;
                }
                else UpdateStatusText.Text = "You have the latest published version.";
            }
            catch (Exception error)
            {
                if (!closed && !exiting)
                    UpdateStatusText.Text = "Update check failed; the app will keep working. " + error.Message;
            }
            finally
            {
                updateBusy = false;
                if (!closed && !exiting) CheckUpdatesButton.IsEnabled = true;
            }
        }

        private async Task InstallUpdateAsync()
        {
            if (closed || exiting || updateBusy || availableUpdate == null) return;
            updateBusy = true;
            CheckUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = UpdateNoticeButton.IsEnabled = false;
            UpdateInfo target = availableUpdate;
            try
            {
                await UpdateSafety.InstallAsync(async () =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Install version " + target.TargetFullRelease.Version + "?",
                        Content = "The update will download, save and back up your settings, then restart Accessibility Zoom. " +
                            "Zoom, color effects and the crosshair briefly stop during the restart. " +
                            "Your profiles, shortcuts and imported designs are kept.",
                        PrimaryButtonText = "Update and restart", CloseButtonText = "Later",
                        DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
                    };
                    return await dialog.ShowAsync() == ContentDialogResult.Primary;
                }, async cancellation =>
                {
                    UpdateProgress.Visibility = Visibility.Visible;
                    UpdateStatusText.Text = "Downloading update...";
                    await updates.Manager.DownloadUpdatesAsync(target, progress => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!closed && !exiting) UpdateProgress.Value = progress;
                    }), cancelToken: cancellation);
                }, () =>
                {
                    FlushCrosshairPreferences();
                    if (crosshairSettingsDirty) throw new IOException("Crosshair settings could not be saved.");
                    FlushFilterEdits();
                    PersistSettings(engine.ColorFilter);
                    FlushPendingSettings();
                    if (!string.IsNullOrEmpty(persistenceWarning)) throw new IOException(persistenceWarning);
                    UpdateSafety.BackupSettings(AppInstallation.DataDirectory,
                        Path.Combine(AppInstallation.DataDirectory, "UpdateBackups"));
                }, () =>
                {
                    // Waiting for graceful exit preserves settings and restores
                    // display/audio/cursor state through the existing shutdown.
                    updates.Manager.WaitExitThenApplyUpdates(target.TargetFullRelease, restart: true);
                    ((App)Application.Current).Shutdown();
                }, updateCancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!closed && !exiting) UpdateStatusText.Text = "Update was not installed. " + error.Message;
            }
            finally
            {
                updateBusy = false;
                if (!closed && !exiting)
                {
                    UpdateProgress.Visibility = Visibility.Collapsed;
                    CheckUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = UpdateNoticeButton.IsEnabled = true;
                }
            }
        }

        private void StopUpdates()
        {
            updateCancellation.Cancel();
        }
    }
}
