using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace AccessibilityZoom
{
    internal sealed class GlobalHotkeyService : IDisposable
    {
        internal const int QuietHoldMilliseconds = 500;
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        private readonly HookProc keyboardCallback;
        private readonly HookProc mouseCallback;
        private readonly Thread thread;
        private readonly ManualResetEvent ready = new(false);
        private readonly object gate = new();
        private readonly HotkeyStateMachine state;
        private IntPtr keyboardHook, mouseHook;
        private uint threadId;
        private Exception startError;
        private bool disposed;
        internal event Action<HotkeyPress> Pressed;

        internal GlobalHotkeyService(HotkeyBindings bindings)
        {
            state = new HotkeyStateMachine(bindings);
            keyboardCallback = KeyboardHook;
            mouseCallback = MouseHook;
            thread = new Thread(Run) { Name = "Accessibility Zoom shortcuts", IsBackground = true };
            thread.Start();
            if (!ready.WaitOne(5000))
            {
                Dispose();
                throw new InvalidOperationException("The shortcut thread did not start.");
            }
            if (startError != null)
            {
                Dispose();
                throw new InvalidOperationException("Global shortcuts could not start.", startError);
            }
        }

        internal void SetBindings(HotkeyBindings bindings)
        {
            lock (gate) state.SetBindings(bindings);
        }

        private void Run()
        {
            try
            {
                threadId = GetCurrentThreadId();
                PeekMessageW(out _, IntPtr.Zero, 0, 0, 0); // Create the queue before signaling readiness.
                lock (gate)
                    for (int key = 1; key <= 0xFE; key++)
                        if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0) state.SeedDown(key);
                IntPtr module = NativeMethods.GetModuleHandleW(null);
                keyboardHook = SetWindowsHookExW(13, keyboardCallback, module, 0);
                mouseHook = SetWindowsHookExW(14, mouseCallback, module, 0);
                if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                ready.Set();
                while (GetMessageW(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
            catch (Exception exception) { startError = exception; ready.Set(); }
            finally
            {
                if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            }
        }

        private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
        {
            int msg = message.ToInt32();
            if (code >= 0 && (msg is 0x100 or 0x101 or 0x104 or 0x105) &&
                Process(Marshal.ReadInt32(data), msg is 0x100 or 0x104)) return (IntPtr)1;
            return CallNextHookEx(keyboardHook, code, message, data);
        }

        private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                int msg = message.ToInt32();
                int key = msg switch
                {
                    0x201 or 0x202 => 1, 0x204 or 0x205 => 2, 0x207 or 0x208 => 4,
                    0x20B or 0x20C => ((Marshal.ReadInt32(data, 8) >> 16) & 0xFFFF) == 2 ? 6 : 5,
                    _ => 0
                };
                if (key != 0 && Process(key, msg is 0x201 or 0x204 or 0x207 or 0x20B)) return (IntPtr)1;
            }
            return CallNextHookEx(mouseHook, code, message, data);
        }

        private bool Process(int key, bool down)
        {
            HotkeyPress? press;
            bool handled;
            // Left/right mouse bindings must leave the app's own settings clickable.
            bool allowNewPress = key is not (1 or 2) || !NativeMethods.IsOwnWindowForeground();
            lock (gate) handled = state.Process(key, down, InputBinding.ReadModifiers(), Environment.TickCount, out press, allowNewPress);
            // Subscribers only queue work: slow audio/display calls never run inside a hook.
            if (press.HasValue)
                try { Pressed?.Invoke(press.Value); } catch { }
            return handled;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (threadId != 0) PostThreadMessageW(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != thread && thread.Join(2000)) ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            internal IntPtr Window;
            internal uint Message;
            internal UIntPtr WParam;
            internal IntPtr LParam;
            internal uint Time;
            internal NativeMethods.Point Point;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int type, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern int GetMessageW(out NativeMessage message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PeekMessageW(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref NativeMessage message);
        [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}
