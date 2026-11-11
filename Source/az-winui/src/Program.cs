using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace AccessibilityZoom
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Installer hooks must finish before XAML, single-instance routing,
            // hotkeys, settings migrations or display changes are initialized.
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnAfterInstallFastCallback(_ => AppInstallation.RefreshStartup())
                .OnAfterUpdateFastCallback(_ => AppInstallation.RefreshStartup())
                .OnBeforeUninstallFastCallback(_ => AppInstallation.RemoveStartup())
                .Run();

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
        }
    }
}
