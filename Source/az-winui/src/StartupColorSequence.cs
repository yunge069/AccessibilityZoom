using System;
using System.Threading;
using System.Threading.Tasks;

namespace AccessibilityZoom
{
    // A bounded startup operation, with no persistent timer or polling loop.
    internal static class StartupColorSequence
    {
        internal static async Task RunAsync(Func<bool> requested, Func<bool> alreadyEnabled,
            Func<(string Layout, bool TargetPresent)> snapshot, Func<bool> apply,
            Action<string> warning, Action<string> trace, CancellationToken cancellationToken,
            Func<CancellationToken, Task> wait = null)
        {
            wait ??= token => Task.Delay(750, token);
            string previousLayout = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                await wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!requested()) { trace("Startup preset disabled by preference."); return; }
                if (alreadyEnabled()) { trace("Preset already enabled."); return; }
                var current = snapshot();
                trace("Layout " + (attempt + 1) + ": target=" + current.TargetPresent + "; " + current.Layout);
                if (current.TargetPresent && !string.IsNullOrEmpty(current.Layout) && current.Layout == previousLayout)
                {
                    bool applied = apply();
                    trace("Startup preset applied=" + applied);
                    if (!applied) warning("The startup monitor preset could not be enabled. Check display color availability.");
                    return;
                }
                previousLayout = current.Layout;
            }
            warning("The startup monitor preset was left off because its saved monitor is missing or the display layout is still changing.");
            trace("No stable target found during startup.");
        }
    }
}
