using System.Diagnostics;
using System.Runtime.InteropServices;
using AccessibilityZoom;

// Opt-in Windows integration test. Only a child of this test process receives
// audio changes. It outputs PCM silence, never alters master/device volume.
internal static class AudioIntegrationTests
{
    internal static void Run(Action<bool, string> check)
    {
        string storePath = Path.Combine(Path.GetTempPath(), "az-test-volumes-" + Guid.NewGuid() + ".txt");
        FocusedAppAudio.VolumeStorePathOverride = storePath;
        try
        {
            RunWithTestSessions(check);
        }
        finally
        {
            FocusedAppAudio.VolumeStorePathOverride = null;
            File.Delete(storePath);
        }
    }

    private static void RunWithTestSessions(Action<bool, string> check)
    {
        using Process child = StartChild();
        using Process quietChild = StartChild();
        try
        {
            WaitForChild(check, child);
            WaitForChild(check, quietChild);
            int root = Environment.ProcessId;
            check(FocusedAppAudio.QueryProcessAudio(root, out int own, out _, out _) && own == 0,
                "The window-owning test parent must have no audio, matching the Signal regression.");
            check(FocusedAppAudio.SetProcessVolume(child.Id, .8f, out string detail), "Initialize child volume: " + detail);
            check(FocusedAppAudio.SetProcessVolume(quietChild.Id, .1f, out detail), "Initialize quieter child volume: " + detail);
            check(FocusedAppAudio.ToggleQuietMode(root, 30, out detail), "Reduce child audio through silent parent: " + detail);
            AssertAudio(check, child.Id, false, .24f, "first hold");
            AssertAudio(check, quietChild.Id, false, .03f, "first hold must scale the quieter session independently");
            check(FocusedAppAudio.ToggleQuietMode(root, 75, out detail), "Restore child audio: " + detail);
            AssertAudio(check, child.Id, false, .8f, "second hold");
            AssertAudio(check, quietChild.Id, false, .1f, "second hold must restore the quieter session independently");
            check(FocusedAppAudio.ToggleMuteApp(root, out detail), "Mute child audio: " + detail);
            AssertAudio(check, child.Id, true, .8f, "tap mute must preserve volume and set Windows mute");
            check(FocusedAppAudio.ToggleMuteApp(root, out detail), "Unmute child audio: " + detail);
            AssertAudio(check, child.Id, false, .8f, "tap unmute");
            check(FocusedAppAudio.ToggleMuteApp(root, out detail), "Mute before hold: " + detail);
            check(FocusedAppAudio.ToggleQuietMode(root, 100, out detail), "Hold at 100%: " + detail);
            AssertAudio(check, child.Id, true, .8f, "a 100% hold must preserve mute and level");
            check(FocusedAppAudio.ToggleQuietMode(root, 30, out detail), "Hold while muted: " + detail);
            AssertAudio(check, child.Id, false, .24f, "hold after mute");
            check(FocusedAppAudio.ToggleQuietMode(root, 30, out detail), "Restore after muted hold: " + detail);
            AssertAudio(check, child.Id, false, .8f, "muted hold must retain the original restore level");
            check(FocusedAppAudio.ToggleQuietMode(root, 0, out detail), "Reduce to silence: " + detail);
            AssertAudio(check, child.Id, false, 0, "zero percent hold");
            check(FocusedAppAudio.ToggleMuteApp(root, out detail), "Mute reduced audio: " + detail);
            check(FocusedAppAudio.ToggleMuteApp(root, out detail), "Unmute reduced audio: " + detail);
            AssertAudio(check, child.Id, false, 0, "tap unmute must keep the reduced level at zero");
            check(FocusedAppAudio.ToggleQuietMode(root, 0, out detail), "Restore from silence: " + detail);
            AssertAudio(check, child.Id, false, .8f, "hold must restore from zero");
        }
        finally
        {
            StopChild(quietChild);
            StopChild(child);
        }
    }

    private static Process StartChild() => Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
    {
        ArgumentList = { "--audio-child" }, UseShellExecute = false,
        RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true
    })!;

    private static void WaitForChild(Action<bool, string> check, Process child)
    {
        var ready = child.StandardOutput.ReadLineAsync();
        check(ready.Wait(10000) && ready.Result == "ready", "Silent audio child must initialize.");
    }

    private static void StopChild(Process child)
    {
        if (child.HasExited) return;
        child.StandardInput.WriteLine("stop");
        if (!child.WaitForExit(5000)) child.Kill();
    }

    private static void AssertAudio(Action<bool, string> check, int pid, bool mute, float level, string label)
    {
        check(FocusedAppAudio.QueryProcessAudio(pid, out int count, out bool actualMute, out float actualLevel) &&
            count > 0 && actualMute == mute && Math.Abs(actualLevel - level) < .005f,
            $"Audio readback failed ({label}): sessions={count}, muted={actualMute}, level={actualLevel}.");
    }

    internal static void RunChild()
    {
        IntPtr handle = IntPtr.Zero, data = IntPtr.Zero, header = IntPtr.Zero;
        int headerSize = Marshal.SizeOf<WaveHeader>();
        bool prepared = false;
        try
        {
            WaveFormat format = new() { Tag = 1, Channels = 1, Samples = 44100, BytesPerSecond = 88200, Align = 2, Bits = 16 };
            Verify(waveOutOpen(out handle, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
            byte[] silence = new byte[88200 * 20];
            data = Marshal.AllocHGlobal(silence.Length);
            Marshal.Copy(silence, 0, data, silence.Length);
            header = Marshal.AllocHGlobal(headerSize);
            Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)silence.Length }, header, false);
            Verify(waveOutPrepareHeader(handle, header, (uint)headerSize));
            prepared = true;
            Verify(waveOutWrite(handle, header, (uint)headerSize));
            Thread.Sleep(250);
            Console.WriteLine("ready");
            Console.ReadLine();
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                waveOutReset(handle);
                if (prepared) waveOutUnprepareHeader(handle, header, (uint)headerSize);
                waveOutClose(handle);
            }
            if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
        }
    }

    private static void Verify(uint result) { if (result != 0) throw new Exception("WinMM error " + result); }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { internal ushort Tag, Channels; internal uint Samples, BytesPerSecond; internal ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { internal IntPtr Data; internal uint Length, Recorded; internal UIntPtr User; internal uint Flags, Loops; internal IntPtr Next; internal UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
}
