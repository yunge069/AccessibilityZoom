using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace AccessibilityZoom
{
    // Serial audio work keeps COM/disk calls out of both the input hook and UI.
    internal sealed class AppVolumeShortcut : IDisposable
    {
        private readonly BlockingCollection<(int Pid, int Held, int QuietVolumePercent)> pending = new();
        private readonly Thread worker;
        internal event Action<string> Completed;

        internal AppVolumeShortcut()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "Accessibility Zoom app volume" };
            worker.Start();
        }

        internal void Enqueue(int held, int quietVolumePercent)
        {
            // Snapshot the foreground app before any asynchronous/UI dispatch.
            if (FocusedAppAudio.TryGetForegroundProcessId(out int pid) && !pending.IsAddingCompleted)
                pending.Add((pid, held, quietVolumePercent));
        }

        private void Run()
        {
            foreach (var request in pending.GetConsumingEnumerable())
            {
                string detail;
                try
                {
                    if (request.Held >= GlobalHotkeyService.QuietHoldMilliseconds)
                        FocusedAppAudio.ToggleQuietMode(request.Pid, request.QuietVolumePercent, out detail);
                    else
                        FocusedAppAudio.ToggleMuteApp(request.Pid, out detail);
                }
                catch (Exception exception) { detail = "Audio shortcut failed: " + exception.Message; }
                try
                {
                    string path = Path.Combine(Path.GetTempPath(), "az-f10-log.txt");
                    string[] lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
                    File.WriteAllLines(path, lines.TakeLast(19).Append(DateTime.Now.ToString("HH:mm:ss") +
                        " hold=" + request.Held + "ms " + detail));
                }
                catch { }
                try { Completed?.Invoke(detail); } catch { }
            }
        }

        public void Dispose()
        {
            pending.CompleteAdding();
            if (worker.Join(1000)) pending.Dispose();
        }
    }
}
