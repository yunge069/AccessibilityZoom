using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AccessibilityZoom
{
    // Per-application volume control behind the F10 hotkey. Everything here is
    // plain Win32 + Core Audio (MMDevice/session APIs) and has no dependency on
    // WinUI, the zoom engine, or any window: the low-level hook and the test
    // harness both drive these methods directly.
    internal static class FocusedAppAudio
    {
        private const uint CLSCTX_ALL = 23;
        private const uint DEVICE_STATE_ACTIVE = 0x00000001;

        [ThreadStatic]
        private static bool comInitialized;

        // Short F10 press: mute/unmute every audio session of the foreground
        // app *including its child processes* (browsers and launchers play
        // audio from helper processes, not the window-owning PID).
        internal static bool ToggleMuteForegroundApp(out string detail)
        {
            if (!TryGetForegroundProcessId(out int pid))
            {
                detail = "No foreground app.";
                return false;
            }
            return ToggleMuteApp(pid, out detail);
        }

        internal static bool ToggleMuteApp(int pid, out string detail)
        {
            return ToggleMuteTree(GetProcessTree(pid), pid, out detail);
        }

        private static bool ToggleMuteTree(List<int> tree, int rootPid, out string detail)
        {
            // Make one decision for all sessions, including helper processes.
            // A mixed state becomes fully muted; another tap unmutes all of them.
            int found = 0;
            bool anyUnmuted = false;
            foreach (int target in tree)
                EnumerateProcessSessions(target, (volume, _) =>
                {
                    if (!IsLiveSession(volume)) return;
                    volume.GetMute(out bool muted);
                    found++;
                    anyUnmuted |= !muted;
                }, out _);
            if (found == 0)
            {
                detail = "No audio sessions for PID " + rootPid + ".";
                return false;
            }
            bool mute = anyUnmuted;
            int changed = 0;
            string lastError = "";
            foreach (int target in tree)
            {
                string exe = GetExeName(target);
                EnumerateProcessSessions(target, (volume, _) =>
                {
                    if (!IsLiveSession(volume)) return;
                    try
                    {
                        volume.GetMasterVolume(out float level);
                        volume.GetMute(out bool wasMuted);
                        Guid context = Guid.Empty;
                        if (mute && (!wasMuted || level > .02f)) RememberVolume(exe, level, true);
                        // Older releases zeroed the volume as well as muting.
                        if (!mute && wasMuted && level == 0)
                        {
                            float restore = GetRememberedVolume(exe, true);
                            if (restore > .02f) volume.SetMasterVolume(restore, ref context);
                        }
                        volume.SetMute(mute, ref context);
                        volume.GetMute(out bool actual);
                        if (actual != mute) throw new InvalidOperationException("Windows did not retain the requested mute state.");
                        changed++;
                    }
                    catch (Exception exception) { lastError = exception.Message; }
                }, out string error);
                if (!string.IsNullOrEmpty(error)) lastError = error;
            }
            detail = (mute ? "Muted " : "Unmuted ") + changed + "/" + found +
                " session(s) of PID " + rootPid + ".";
            if (!string.IsNullOrEmpty(lastError)) detail += " " + lastError;
            return changed == found;
        }

        private static bool IsLiveSession(ISimpleAudioVolume volume)
        {
            try
            {
                if (volume is IAudioSessionControl control)
                {
                    control.GetState(out int state);
                    return state != 2; // Inactive sessions still belong in the Windows mixer.
                }
                return true;
            }
            catch { return false; }
        }

        // Plain-language audibility note: acting on silent sessions changes
        // nothing audible, and that used to look exactly like a broken key.
        private static string PeakSuffix(List<int> tree)
        {
            try
            {
                if (GetTreePeak(tree) < 0.02f)
                    return " — nothing currently audible.";
            }
            catch
            {
            }
            return string.Empty;
        }

        private static string QuietPeakNote(int pid)
        {
            try
            {
                return PeakSuffix(GetProcessTree(pid));
            }
            catch
            {
                return string.Empty;
            }
        }

        // Every active render endpoint (speakers, headset, HDMI, ...), not
        // just the two defaults: per-app routing in Windows sound settings
        // can put an app's sessions on any of them. Caller owns + releases.
        // Falls back to the two default endpoints if enumeration fails.
        private static List<IMMDevice> GetRenderEndpoints()
        {
            List<IMMDevice> endpoints = new List<IMMDevice>();
            try
            {
                EnsureCom();
                IMMDeviceEnumerator enumerator =
                    (IMMDeviceEnumerator)new MMDeviceEnumerator();
                try
                {
                    bool enumerated = false;
                    try
                    {
                        enumerator.EnumAudioEndpoints(
                            EDataFlow.Render, DEVICE_STATE_ACTIVE, out IMMDeviceCollection collection);
                        try
                        {
                            collection.GetCount(out uint count);
                            for (uint i = 0; i < count && endpoints.Count < 16; i++)
                            {
                                try
                                {
                                    collection.Item(i, out IMMDevice device);
                                    if (device != null)
                                        endpoints.Add(device);
                                }
                                catch
                                {
                                }
                            }
                            enumerated = true;
                        }
                        finally
                        {
                            try { Marshal.ReleaseComObject(collection); } catch { }
                        }
                    }
                    catch
                    {
                    }
                    if (!enumerated)
                    {
                        foreach (ERole role in new ERole[] { ERole.Multimedia, ERole.Console })
                        {
                            try
                            {
                                enumerator.GetDefaultAudioEndpoint(
                                    EDataFlow.Render, role, out IMMDevice device);
                                if (device != null)
                                    endpoints.Add(device);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                finally
                {
                    try { Marshal.ReleaseComObject(enumerator); } catch { }
                }
            }
            catch
            {
            }
            return endpoints;
        }

        // Single-walk peak snapshot over a process tree: which of these PIDs
        // is actually producing sound right now. Read-only, one enumeration.
        internal static float GetTreePeak(List<int> tree)
        {
            float best = 0.0f;
            try
            {
                EnsureCom();
                List<IMMDevice> endpoints = GetRenderEndpoints();
                try
                {
                    foreach (IMMDevice device in endpoints)
                    {
                        IAudioSessionManager2 manager = null;
                        IAudioSessionEnumerator sessionEnum = null;
                        try
                        {
                            Guid iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                            device.Activate(
                                ref iid, CLSCTX_ALL, IntPtr.Zero, out object managerObject);
                            manager = (IAudioSessionManager2)managerObject;
                            manager.GetSessionEnumerator(out sessionEnum);
                            sessionEnum.GetCount(out int sessionCount);
                            for (int i = 0; i < sessionCount; i++)
                            {
                                IAudioSessionControl control = null;
                                try
                                {
                                    sessionEnum.GetSession(i, out control);
                                    if (!(control is IAudioSessionControl2 control2))
                                        continue;
                                    control2.GetProcessId(out uint sessionPid);
                                    if (!tree.Contains(unchecked((int)sessionPid)))
                                        continue;
                                    if (control is IAudioMeterInformation meter)
                                    {
                                        try
                                        {
                                            meter.GetPeakValue(out float peak);
                                            if (peak > best)
                                                best = peak;
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                                finally
                                {
                                    if (control != null)
                                        Marshal.ReleaseComObject(control);
                                }
                            }
                        }
                        finally
                        {
                            if (sessionEnum != null)
                                Marshal.ReleaseComObject(sessionEnum);
                            if (manager != null)
                                Marshal.ReleaseComObject(manager);
                        }
                    }
                    foreach (IMMDevice endpoint in endpoints)
                    {
                        try { Marshal.ReleaseComObject(endpoint); } catch { }
                    }
                }
                catch
                {
                }
            }
            catch
            {
            }
            return best;
        }

        // Read-only loudness snapshot for diagnostics ("was anything audible
        // when the key was pressed?"). Never changes any audio state.
        internal static float GetPeakLevel(int pid)
        {
            float best = 0.0f;
            try
            {
                EnsureCom();
                List<IMMDevice> endpoints = GetRenderEndpoints();
                try
                {
                    foreach (IMMDevice device in endpoints)
                    {
                        IAudioSessionManager2 manager = null;
                        IAudioSessionEnumerator sessionEnum = null;
                        try
                        {
                            Guid iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                            device.Activate(
                                ref iid, CLSCTX_ALL, IntPtr.Zero, out object managerObject);
                            manager = (IAudioSessionManager2)managerObject;
                            manager.GetSessionEnumerator(out sessionEnum);
                            sessionEnum.GetCount(out int sessionCount);
                            for (int i = 0; i < sessionCount; i++)
                            {
                                IAudioSessionControl control = null;
                                try
                                {
                                    sessionEnum.GetSession(i, out control);
                                    if (!(control is IAudioSessionControl2 control2))
                                        continue;
                                    control2.GetProcessId(out uint sessionPid);
                                    if (unchecked((int)sessionPid) != pid)
                                        continue;
                                    if (control is IAudioMeterInformation meter)
                                    {
                                        try
                                        {
                                            meter.GetPeakValue(out float peak);
                                            if (peak > best)
                                                best = peak;
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                                finally
                                {
                                    if (control != null)
                                        Marshal.ReleaseComObject(control);
                                }
                            }
                        }
                        finally
                        {
                            if (sessionEnum != null)
                                Marshal.ReleaseComObject(sessionEnum);
                            if (manager != null)
                                Marshal.ReleaseComObject(manager);
                        }
                    }
                    foreach (IMMDevice endpoint in endpoints)
                    {
                        try { Marshal.ReleaseComObject(endpoint); } catch { }
                    }
                }
                catch
                {
                }
            }
            catch
            {
            }
            return best;
        }

        // Unmute and set the foreground app to an explicit mixer level.
        internal static bool SetForegroundAppVolume(float level, out string detail)
        {
            if (!TryGetForegroundProcessId(out int pid))
            {
                detail = "No foreground app.";
                return false;
            }
            int totalSessions = 0;
            int pidsWithAudio = 0;
            foreach (int target in GetProcessTree(pid))
            {
                QueryProcessAudio(target, out int sessions, out _, out _);
                if (sessions == 0)
                    continue;
                SetProcessVolume(target, level, out _);
                totalSessions += sessions;
                pidsWithAudio++;
            }
            if (pidsWithAudio == 0)
            {
                detail = "No audio sessions for PID " + pid + ".";
                return false;
            }
            detail = "Set " + totalSessions + " session(s) in " +
                pidsWithAudio + " process(es) of PID " + pid + " to " +
                Math.Round(Math.Max(0.0f, Math.Min(1.0f, level)) * 100) + "%.";
            return true;
        }

        internal static bool ToggleMuteProcess(int pid, out string detail)
        {
            return ToggleMuteTree(new List<int> { pid }, pid, out detail);
        }

        internal static bool SetProcessVolume(int pid, float level, out string detail)
        {
            float clamped = Math.Max(0.0f, Math.Min(1.0f, level));
            return WithProcessVolumes(
                pid,
                (volume, firstMute) =>
                {
                    Guid context = Guid.Empty;
                    volume.SetMasterVolume(clamped, ref context);
                    UnmuteAndVerifyLevel(volume, clamped, ref context);
                },
                (sessions, muted) => "Set " + sessions + " session(s) of PID " + pid +
                    " to " + Math.Round(clamped * 100) + "%.",
                out detail);
        }

        // Live sessions each have a saved level. The app-wide disk record still
        // provides a restore level if all sessions or Accessibility Zoom restart.
        private sealed record QuietRestore(Dictionary<string, float> Pending, float FallbackLevel, bool PartiallyRestored, bool Persisted);
        private static readonly Dictionary<string, QuietRestore> liveQuietRestores = new(StringComparer.OrdinalIgnoreCase);

        internal static bool ToggleQuietMode(int pid, int quietVolumePercent, out string detail)
        {
            try
            {
                bool success = false;
                bool changed = false;
                string message = string.Empty;
                WithStore(() => success = ToggleQuietModeLocked(pid, Math.Clamp(quietVolumePercent, 0, 100), out message, out changed));
                detail = changed ? message + QuietPeakNote(pid) : message;
                return success;
            }
            catch (Exception exception) { detail = exception.Message; return false; }
        }

        private static bool ToggleQuietModeLocked(int pid, int percent, out string detail, out bool changed)
        {
            changed = false;
            bool storeLoaded = LoadVolumeStoreLocked();
            string exe = GetExeName(pid);
            if (!volumeStore.TryGetValue(exe, out VolumeMemory memory)) memory = new VolumeMemory();
            liveQuietRestores.TryGetValue(exe, out QuietRestore live);
            if (storeLoaded && !memory.Quiet && live?.Persisted == true)
            {
                // Another copy of the app may already have restored this level.
                liveQuietRestores.Remove(exe);
                live = null;
            }
            bool restoring = live != null ? live.Pending.Count > 0 : memory.Quiet;
            if (!restoring && percent == 100)
            {
                detail = "Quiet volume is set to 100%.";
                return true;
            }

            List<int> tree = GetProcessTree(pid);
            Dictionary<string, float> levels = SnapshotQuietLevels(tree, restoring);
            if (levels.Count == 0)
            {
                detail = "No audio sessions for PID " + pid + ".";
                return false;
            }

            Dictionary<string, float> targets;
            if (restoring)
            {
                targets = live?.Pending.Where(entry => levels.ContainsKey(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value) ?? new Dictionary<string, float>();
                if (targets.Count == 0 && live?.PartiallyRestored != true)
                    targets = levels.ToDictionary(entry => entry.Key, _ => live?.FallbackLevel ?? memory.QuietSaved);
            }
            else
                targets = levels.ToDictionary(entry => entry.Key, entry => QuietVolumePolicy.Reduce(entry.Value, percent));

            var written = new HashSet<string>();
            HashSet<string> verified = ApplyQuietLevels(tree, targets, written);
            changed = verified.Count > 0;
            bool complete = verified.Count == targets.Count;
            if (restoring && complete)
            {
                memory.Quiet = false;
                volumeStore[exe] = memory;
                if (SaveVolumeStoreLocked())
                    liveQuietRestores.Remove(exe);
                else
                    liveQuietRestores[exe] = new(new Dictionary<string, float>(), 0, false, false);
            }
            else if (restoring)
            {
                // Preserve pending sessions and skip verified restores on the next hold.
                foreach (string id in verified) targets.Remove(id);
                liveQuietRestores[exe] = new(targets, live?.FallbackLevel ?? memory.QuietSaved,
                    verified.Count > 0 || live?.PartiallyRestored == true, live?.Persisted ?? memory.Quiet);
            }
            else if (written.Count > 0)
            {
                // A level write may succeed even if unmute or verification fails.
                var pending = written.ToDictionary(id => id, id => levels[id]);
                memory.QuietSaved = pending.First().Value;
                memory.Quiet = true;
                volumeStore[exe] = memory;
                liveQuietRestores[exe] = new(pending, memory.QuietSaved, false, SaveVolumeStoreLocked());
            }

            detail = (restoring ? "Restored volume for " : "Set quiet volume to " + percent + "% for ") +
                verified.Count + " session(s) of PID " + pid + ".";
            if (!complete) detail += " Some audio sessions could not be updated.";
            return complete;
        }

        private static Dictionary<string, float> SnapshotQuietLevels(List<int> tree, bool restoring)
        {
            // Read every baseline before writing any volume because Windows
            // may share the volume control across multiple sessions.
            var levels = new Dictionary<string, float>();
            foreach (int target in tree)
            {
                float muteRestore = !restoring && volumeStore.TryGetValue(GetExeName(target), out VolumeMemory memory)
                    ? memory.MuteRemembered : 0;
                EnumerateProcessSessions(target, (volume, _) =>
                {
                    if (!IsLiveSession(volume)) return;
                    try
                    {
                        ((IAudioSessionControl2)volume).GetSessionInstanceIdentifier(out string id);
                        if (string.IsNullOrEmpty(id)) return;
                        float baseline = 0;
                        if (!restoring)
                        {
                            volume.GetMasterVolume(out float level);
                            volume.GetMute(out bool muted);
                            baseline = QuietVolumePolicy.GetBaseline(level, muted, muteRestore);
                        }
                        levels.TryAdd(id, baseline);
                    }
                    catch { }
                }, out _);
            }
            return levels;
        }

        private static HashSet<string> ApplyQuietLevels(List<int> tree, Dictionary<string, float> targets, HashSet<string> written)
        {
            var verified = new HashSet<string>();
            if (targets.Count == 0) return verified;
            foreach (int target in tree)
                EnumerateProcessSessions(target, (volume, _) =>
                {
                    if (!IsLiveSession(volume)) return;
                    try
                    {
                        ((IAudioSessionControl2)volume).GetSessionInstanceIdentifier(out string id);
                        if (string.IsNullOrEmpty(id) || !targets.TryGetValue(id, out float level) || written.Contains(id)) return;
                        Guid context = Guid.Empty;
                        volume.SetMasterVolume(level, ref context);
                        written.Add(id);
                        UnmuteAndVerifyLevel(volume, level, ref context);
                        verified.Add(id);
                    }
                    catch { }
                }, out _);
            return verified;
        }

        private static void UnmuteAndVerifyLevel(ISimpleAudioVolume volume, float level, ref Guid context)
        {
            volume.SetMute(false, ref context);
            volume.GetMasterVolume(out float actualLevel);
            volume.GetMute(out bool actualMute);
            if (actualMute || Math.Abs(actualLevel - level) > .005f)
                throw new InvalidOperationException("Windows did not retain the requested audio level.");
        }

        internal static bool ToggleQuietModeForegroundApp(int quietVolumePercent, out string detail)
        {
            if (!TryGetForegroundProcessId(out int pid))
            {
                detail = "No foreground app.";
                return false;
            }
            return ToggleQuietMode(pid, quietVolumePercent, out detail);
        }

        private static string GetExeName(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    string name = process.ProcessName;
                    if (!string.IsNullOrEmpty(name))
                        return name.ToLowerInvariant();
                }
            }
            catch
            {
            }
            return "pid" + pid;
        }

        // Mute and quiet keep SEPARATE remembered levels: a mute in between
        // must never clobber the level a quiet hold will restore.
        private static void RememberVolume(string exe, float volume, bool forMute)
        {
            WithStore(() =>
            {
                LoadVolumeStoreLocked();
                if (!volumeStore.TryGetValue(exe, out VolumeMemory memory))
                {
                    memory = new VolumeMemory();
                    volumeStore[exe] = memory;
                }
                if (forMute)
                    memory.MuteRemembered = volume;
                else
                    memory.QuietSaved = volume;
                SaveVolumeStoreLocked();
            });
        }

        private static float GetRememberedVolume(string exe, bool forMute)
        {
            float remembered = 0.0f;
            WithStore(() =>
            {
                LoadVolumeStoreLocked();
                if (volumeStore.TryGetValue(exe, out VolumeMemory memory))
                    remembered = forMute ? memory.MuteRemembered : memory.QuietSaved;
            });
            return remembered;
        }

        private sealed class VolumeMemory
        {
            internal float MuteRemembered;
            internal float QuietSaved;
            internal bool Quiet;
        }

        private static readonly object volumeLock = new object();
        private static readonly Dictionary<string, VolumeMemory> volumeStore =
            new Dictionary<string, VolumeMemory>(StringComparer.OrdinalIgnoreCase);

        // Shared across every copy of the app on this machine (normal and
        // elevated instances, restarts): %PROGRAMDATA% is visible to all
        // users. Falls back per-user, then temp. Write-probed so a
        // non-writable location is never selected.
        internal static string VolumeStorePathOverride { get; set; }

        private static string GetVolumeStorePath()
        {
            if (VolumeStorePathOverride != null) return VolumeStorePathOverride;
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AccessibilityZoom");
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, "f10-volumes.txt");
                using (FileStream stream = new FileStream(
                    probe, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                }
                return probe;
            }
            catch
            {
            }
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AccessibilityZoom");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "f10-volumes.txt");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "f10-volumes.txt");
            }
        }

        // Cross-process gate so two live copies (e.g. normal + elevated, or
        // an old and a new build) cannot split-brain the store. Every access
        // re-reads the file. Best effort: without the mutex we still work,
        // just without the guarantee.
        private static void WithStore(Action work)
        {
            Mutex mutex = null;
            bool taken = false;
            try
            {
                try { mutex = new Mutex(false, @"Local\AccessibilityZoom.VolumeStore"); }
                catch { mutex = null; }
                if (mutex != null)
                {
                    try { taken = mutex.WaitOne(1000); }
                    catch (AbandonedMutexException) { taken = true; }
                    catch { taken = false; }
                }
                lock (volumeLock)
                {
                    work();
                }
            }
            finally
            {
                if (taken)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                if (mutex != null)
                {
                    try { mutex.Dispose(); } catch { }
                }
            }
        }

        // Lines look like: spotify|0.5|0.5|1
        // (exe|muteRemembered|quietSaved|quietFlag). Old 3-field lines
        // (exe|remembered|quietFlag) migrate into both slots.
        private static bool LoadVolumeStoreLocked()
        {
            volumeStore.Clear();
            try
            {
                string path = GetVolumeStorePath();
                if (!File.Exists(path))
                    return false;
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string[] parts = rawLine.Split('|');
                    if (parts.Length != 3 && parts.Length != 4)
                        continue;
                    string exe = parts[0].Trim().ToLowerInvariant();
                    if (exe.Length == 0 || exe.Length > 64 || volumeStore.Count >= 256)
                        continue;
                    if (!float.TryParse(
                        parts[1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float first))
                        continue;
                    float second = first;
                    string quietPart = parts[2];
                    if (parts.Length == 4)
                    {
                        if (!float.TryParse(
                            parts[2],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out second))
                            continue;
                        quietPart = parts[3];
                    }
                    volumeStore[exe] = new VolumeMemory
                    {
                        MuteRemembered = Math.Max(0.0f, Math.Min(1.0f, first)),
                        QuietSaved = Math.Max(0.0f, Math.Min(1.0f, second)),
                        Quiet = quietPart.Trim() == "1",
                    };
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SaveVolumeStoreLocked()
        {
            try
            {
                StringBuilder builder = new StringBuilder();
                foreach (KeyValuePair<string, VolumeMemory> entry in volumeStore)
                {
                    builder.Append(entry.Key).Append('|')
                        .Append(entry.Value.MuteRemembered.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                        .Append('|')
                        .Append(entry.Value.QuietSaved.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                        .Append('|').Append(entry.Value.Quiet ? "1" : "0")
                        .AppendLine();
                }
                File.WriteAllText(GetVolumeStorePath(), builder.ToString());
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Foreground window PID plus all of its descendants (root first),
        // capped for safety. Browsers and launchers play audio from helper
        // processes, so muting only the window-owning PID misses the sound.
        internal static List<int> GetProcessTree(int rootPid)
        {
            List<int> tree = new List<int> { rootPid };
            try
            {
                Dictionary<int, List<int>> children = new Dictionary<int, List<int>>();
                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                    return tree;
                try
                {
                    ProcessEntry entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                    if (!Process32FirstW(snapshot, ref entry))
                        return tree;
                    do
                    {
                        int child = unchecked((int)entry.ProcessId);
                        int parent = unchecked((int)entry.ParentProcessId);
                        if (child == 0 || child == parent)
                            continue;
                        if (!children.TryGetValue(parent, out List<int> list))
                        {
                            list = new List<int>();
                            children[parent] = list;
                        }
                        if (list.Count < 256)
                            list.Add(child);
                    }
                    while (Process32NextW(snapshot, ref entry));
                }
                finally
                {
                    CloseHandle(snapshot);
                }
                Queue<int> queue = new Queue<int>();
                queue.Enqueue(rootPid);
                while (queue.Count > 0 && tree.Count < 128)
                {
                    int current = queue.Dequeue();
                    if (!children.TryGetValue(current, out List<int> list))
                        continue;
                    foreach (int child in list)
                    {
                        if (tree.Contains(child))
                            continue;
                        tree.Add(child);
                        queue.Enqueue(child);
                    }
                }
            }
            catch
            {
                // Fall back to the root PID alone.
            }
            return tree;
        }

        // Test/verification surface: mute + volume of the first matching session.
        // Diagnose returns a human-readable enumeration trace for debugging.
        internal static string Diagnose(int pid)
        {
            try
            {
                EnsureCom();
                StringBuilder trace = new StringBuilder();
                int seen = 0;
                if (!EnumerateProcessSessions(
                    pid,
                    (volume, isFirst) => { seen++; },
                    out string error,
                    trace))
                {
                    return "FAIL pid=" + pid + " " + error + " trace=" + trace.ToString();
                }
                return "OK pid=" + pid + " matched=" + seen + " trace=" + trace.ToString();
            }
            catch (Exception exception)
            {
                return "FAIL pid=" + pid + " " + exception.ToString();
            }
        }

        internal static bool QueryProcessAudio(
            int pid, out int sessions, out bool muted, out float volume)
        {
            sessions = 0;
            muted = false;
            volume = 1.0f;
            try
            {
                EnsureCom();
                int seen = 0;
                bool localMuted = false;
                float localVolume = 1.0f;
                if (!EnumerateProcessSessions(
                    pid,
                    (simpleVolume, isFirst) =>
                    {
                        // Inactive sessions can resume; only expired sessions are stale.
                        bool active = true;
                        try
                        {
                            if (simpleVolume is IAudioSessionControl control)
                            {
                                control.GetState(out int state);
                                active = state != 2;
                            }
                        }
                        catch
                        {
                            active = false;
                        }
                        if (!active)
                            return;
                        if (seen == 0)
                        {
                            try { simpleVolume.GetMute(out localMuted); } catch { }
                            try { simpleVolume.GetMasterVolume(out localVolume); } catch { }
                        }
                        seen++;
                    },
                    out _))
                {
                    return false;
                }
                sessions = seen;
                muted = localMuted;
                volume = localVolume;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool WithProcessVolumes(
            int pid,
            Action<ISimpleAudioVolume, bool> apply,
            Func<int, bool, string> describe,
            out string detail)
        {
            detail = string.Empty;
            try
            {
                EnsureCom();
                int sessions = 0;
                bool firstMute = false;
                if (!EnumerateProcessSessions(
                    pid,
                    (volume, isFirst) =>
                    {
                        if (!IsLiveSession(volume)) return;
                        if (sessions == 0)
                        {
                            try { volume.GetMute(out firstMute); } catch { }
                        }
                        apply(volume, firstMute);
                        sessions++;
                    },
                    out string error))
                {
                    detail = error;
                    return false;
                }
                detail = describe(sessions, firstMute);
                return sessions > 0;
            }
            catch (Exception exception)
            {
                detail = exception.Message;
                return false;
            }
        }

        // Invokes visit for every ISimpleAudioVolume owned by pid on the
        // default render endpoints. Returns false only on enumeration failure.
        private static bool EnumerateProcessSessions(
            int pid,
            Action<ISimpleAudioVolume, bool> visit,
            out string error,
            StringBuilder trace = null)
        {
            error = string.Empty;
            List<IMMDevice> endpoints = GetRenderEndpoints();
            try
            {
                if (trace != null)
                    trace.Append("endpoints=" + endpoints.Count + ";");
                if (endpoints.Count == 0)
                {
                    error = "No audio render endpoint.";
                    return false;
                }
                bool first = true;
                foreach (IMMDevice device in endpoints)
                {
                    IAudioSessionManager2 manager = null;
                    IAudioSessionEnumerator sessionEnum = null;
                    try
                    {
                        Guid iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                        device.Activate(
                            ref iid, CLSCTX_ALL, IntPtr.Zero, out object managerObject);
                        manager = (IAudioSessionManager2)managerObject;
                        manager.GetSessionEnumerator(out sessionEnum);
                        sessionEnum.GetCount(out int sessionCount);
                        if (trace != null)
                            trace.Append("sessions=" + sessionCount + ";");
                        for (int i = 0; i < sessionCount; i++)
                        {
                            IAudioSessionControl control = null;
                            try
                            {
                                sessionEnum.GetSession(i, out control);
                                string kind = control is IAudioSessionControl2 ? "c2" : "c1";
                                string vol = control is ISimpleAudioVolume ? "+v" : "-v";
                                uint sessionPid = 0;
                                int sessionState = -1;
                                if (control is IAudioSessionControl2 control2)
                                    control2.GetProcessId(out sessionPid);
                                try { control.GetState(out sessionState); } catch { }
                                if (trace != null)
                                {
                                    string peakText = string.Empty;
                                    if (control is IAudioMeterInformation meter)
                                    {
                                        try
                                        {
                                            meter.GetPeakValue(out float peak);
                                            peakText = ",peak=" + peak.ToString("0.00");
                                        }
                                        catch
                                        {
                                        }
                                    }
                                    trace.Append(
                                        "s" + i + "=" + kind + vol +
                                        ",pid=" + sessionPid + ",st=" + sessionState + peakText + ";");
                                }
                                if (!(control is IAudioSessionControl2 pidControl))
                                    continue;
                                pidControl.GetProcessId(out uint matchPid);
                                if (unchecked((int)matchPid) != pid)
                                    continue;
                                if (!(control is ISimpleAudioVolume simpleVolume))
                                    continue;
                                visit(simpleVolume, first);
                                first = false;
                            }
                            finally
                            {
                                if (control != null)
                                    Marshal.ReleaseComObject(control);
                            }
                        }
                    }
                    finally
                    {
                        if (sessionEnum != null)
                            Marshal.ReleaseComObject(sessionEnum);
                        if (manager != null)
                            Marshal.ReleaseComObject(manager);
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                if (trace != null)
                    trace.Append("EX=" + exception.GetType().Name + ";");
                return false;
            }
            finally
            {
                foreach (IMMDevice endpoint in endpoints)
                {
                    try { Marshal.ReleaseComObject(endpoint); } catch { }
                }
            }
        }

        internal static bool TryGetForegroundProcessId(out int pid)
        {
            pid = 0;
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                    return false;
                GetWindowThreadProcessId(foreground, out uint processId);
                if (processId == 0)
                    return false;
                pid = unchecked((int)processId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureCom()
        {
            if (comInitialized)
                return;
            CoInitializeEx(IntPtr.Zero, 0);
            comInitialized = true;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            internal uint Size;
            internal uint Usage;
            internal uint ProcessId;
            internal UIntPtr DefaultHeapId;
            internal uint ModuleId;
            internal uint Threads;
            internal uint ParentProcessId;
            internal int PriorityClassBase;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private enum EDataFlow
        {
            Render = 0,
        }

        private enum ERole
        {
            Console = 0,
            Multimedia = 1,
            Communications = 2,
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(
                EDataFlow dataFlow,
                uint stateMask,
                out IMMDeviceCollection devices);
            void GetDefaultAudioEndpoint(
                EDataFlow dataFlow,
                ERole role,
                out IMMDevice device);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            void GetCount(out uint count);
            void Item(uint index, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            // NOTE: vtable order follows mmdeviceapi.h exactly: Activate is
            // slot 0, NOT last. (A wrong order here fails silently with
            // garbage out-params instead of an exception.)
            void Activate(
                ref Guid iid,
                uint clsCtx,
                IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object @interface);
            void OpenPropertyStore(
                int access, [MarshalAs(UnmanagedType.IUnknown)] out object properties);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out uint state);
        }

        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            void GetAudioSessionControl(
                ref Guid sessionId,
                uint streamFlags,
                out IAudioSessionControl sessionControl);
            // Placeholder: IAudioSessionManager.GetSimpleAudioVolume occupies
            // slot 1 per audiopolicy.h; GetSessionEnumerator is slot 2.
            void GetSimpleAudioVolume(
                ref Guid sessionId,
                uint streamFlags,
                out ISimpleAudioVolume audioVolume);
            void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            void GetCount(out int count);
            void GetSession(int index, out IAudioSessionControl session);
        }

        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            // Slot 0 per audiopolicy.h. The only base method used here: skip
            // stale (expired/inactive) sessions when reading levels.
            void GetState(out int state);
        }

        [ComImport]
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            void GetState(out int state);
            void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetDisplayName(
                [MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
            void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            void SetIconPath(
                [MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
            void GetGroupingParam(out Guid groupingId);
            void SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
            void RegisterAudioSessionNotification(
                [MarshalAs(UnmanagedType.IUnknown)] object notifications);
            void UnregisterAudioSessionNotification(
                [MarshalAs(UnmanagedType.IUnknown)] object notifications);
            void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetSessionInstanceIdentifier(
                [MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetProcessId(out uint processId);
        }

        [ComImport]
        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            void SetMasterVolume(float level, ref Guid eventContext);
            void GetMasterVolume(out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport]
        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            void GetPeakValue(out float peak);
            void GetMeteringChannelCount(out uint channelCount);
            void GetChannelsPeakValues(uint channelCount, IntPtr peakValues);
            void QueryHardwareSupport(out uint supportMask);
        }
    }
}
