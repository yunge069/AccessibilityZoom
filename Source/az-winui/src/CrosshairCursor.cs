using System;
using System.ComponentModel;

namespace AccessibilityZoom
{
    // Owns cursor suppression separately from the zoom backend's lifetime.
    internal sealed class CrosshairCursor : IDisposable
    {
        private readonly Func<bool> initialize;
        private readonly Func<bool, bool> show;
        private readonly Func<bool> uninitialize;
        private bool initialized, hidden, disposed;

        internal CrosshairCursor() : this(NativeMethods.MagInitialize,
            NativeMethods.MagShowSystemCursor, NativeMethods.MagUninitialize) { }

        internal CrosshairCursor(Func<bool> initialize, Func<bool, bool> show, Func<bool> uninitialize)
        {
            this.initialize = initialize;
            this.show = show;
            this.uninitialize = uninitialize;
        }

        internal void SetHidden(bool value)
        {
            if (disposed || hidden == value) return;
            if (value && !initialized)
            {
                if (!initialize()) throw new Win32Exception("Could not initialize crosshair cursor control.");
                initialized = true;
            }
            if (!show(!value)) throw new Win32Exception("Could not change system cursor visibility.");
            hidden = value;
        }

        public void Dispose()
        {
            if (disposed) return;
            try { SetHidden(false); }
            finally
            {
                disposed = true;
                if (initialized) uninitialize();
            }
        }
    }
}
