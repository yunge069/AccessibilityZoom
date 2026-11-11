using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace AccessibilityZoom
{
    internal sealed class MagnifierEngine : IDisposable, IZoomController
    {
        private const int PollIntervalMilliseconds = 1;
        private const float MinimumZoom = 1.25f;
        private const float MaximumZoom = 8.0f;

        private readonly Thread inputThread;
        private readonly ManualResetEvent stopSignal;
        private readonly object stateLock = new object();
        private IZoomBackend zoomOverlay;
        private GpuOverlay filterOverlay;
        private AmdDriverColorController amdColor;
        private bool timerResolutionActive;
        private bool disposed;
        private bool active;
        private bool filterEnabled;
        private bool latchedActive;
        private bool thumbHeld;
        private bool lastPhysicalThumbState;
        private bool suppressThumbUntilRelease;
        private bool hasReportedError;
        private bool amdFailedForFilterSession;
        private bool amdRestorePending;
        private float zoom;
        private InputBinding zoomBinding;
        private ColorFilterSettings colorFilter;
        private Rectangle activeDisplay;
        private Rectangle filterDisplay;
        private string filterMonitorDevice = "";
        private string activeFilterRenderer = "Off";

        internal MagnifierEngine(
            float initialZoom,
            int initialThumbButton,
            ColorFilterSettings initialColorFilter,
            bool initialFilterEnabled,
            InputBinding? initialZoomBinding = null,
            string initialFilterMonitorDevice = "")
        {
            zoom = ClampZoom(initialZoom);
            zoomBinding = initialZoomBinding ?? new InputBinding(initialThumbButton == 2 ? 6 : 5);
            colorFilter = initialColorFilter.Clamped();
            stopSignal = new ManualResetEvent(false);

            activeDisplay = DisplayHelper.GetPrimaryBounds();
            amdColor = new AmdDriverColorController();
            try
            {
                zoomOverlay = new DirectFullscreenZoom(activeDisplay);
            }
            catch
            {
                // Keep the previous GPU path available on systems where Windows
                // refuses to initialize its direct full-screen transform.
                zoomOverlay = new GpuOverlay(activeDisplay);
            }
            zoomOverlay.Error += OverlayError;

            filterOverlay = zoomOverlay as GpuOverlay;
            if (filterOverlay == null)
            {
                try
                {
                    filterOverlay = new GpuOverlay(activeDisplay);
                    filterOverlay.Error += OverlayError;
                }
                catch
                {
                    // The low-latency zoom remains usable if Desktop Duplication
                    // is unavailable. The UI reports that the color filter cannot run.
                    filterOverlay = null;
                }
            }

            // Startup uses the saved output, or the primary output for older
            // settings. A disconnected saved output must not redirect driver color.
            filterMonitorDevice = initialFilterMonitorDevice ?? "";
            DisplayChoice startupDisplay = DisplayHelper.FindSavedDisplay(DisplayHelper.GetDisplays(), filterMonitorDevice);
            if (startupDisplay != null)
            {
                filterDisplay = startupDisplay.Bounds;
                filterMonitorDevice = startupDisplay.DeviceName;
            }
            // Keep immediate multi-output driver writes out of construction.
            // App enables its startup preference after observing a stable layout.
            bool safeToAutoEnable = amdColor == null || amdColor.DisplayCount <= 1;
            filterEnabled = initialFilterEnabled && safeToAutoEnable && !filterDisplay.IsEmpty &&
                FilterAvailableLocked();
            if (filterEnabled)
            {
                ApplyOverlayLocked(false);
            }

            NativeMethods.timeBeginPeriod(1);
            timerResolutionActive = true;

            lastPhysicalThumbState = zoomBinding.IsDown();
            suppressThumbUntilRelease = lastPhysicalThumbState;

            inputThread = new Thread(InputLoop);
            inputThread.Name = "Accessibility Zoom 1ms thumb-button input";
            inputThread.IsBackground = true;
            inputThread.Priority = ThreadPriority.AboveNormal;
            inputThread.Start();
        }

        public event EventHandler StateChanged;
        public string FilterMonitorDevice { get { lock (stateLock) return filterMonitorDevice; } }
        internal event EventHandler<EngineErrorEventArgs> Error;

        public bool Active
        {
            get
            {
                lock (stateLock)
                {
                    return active;
                }
            }
        }

        public float Zoom
        {
            get
            {
                lock (stateLock)
                {
                    return zoom;
                }
            }
        }

        public int ThumbButton
        {
            get
            {
                lock (stateLock)
                {
                    return zoomBinding.Key == 6 ? 2 : 1;
                }
            }
        }

        public string RendererName
        {
            get
            {
                lock (stateLock)
                {
                    string zoomName = zoomOverlay == null ? string.Empty : zoomOverlay.Name;
                    string filterName;
                    if (amdColor != null && amdColor.SafetyBlocked)
                    {
                        filterName = "AMD recovery pending; color filter locked";
                    }
                    else if (amdColor != null && amdColor.Available)
                    {
                        filterName = "AMD ADLX display-driver color; " +
                            (filterOverlay == null
                                ? "no shader fallback"
                                : filterOverlay.Name + " fallback");
                    }
                    else
                    {
                        filterName = filterOverlay == null
                            ? "color filter unavailable"
                            : filterOverlay.Name + " color-filter fallback";
                    }
                    return zoomName + "; " + filterName;
                }
            }
        }

        public bool FilterAvailable
        {
            get
            {
                lock (stateLock)
                {
                    return FilterAvailableLocked();
                }
            }
        }

        public string ActiveFilterRenderer
        {
            get
            {
                lock (stateLock)
                {
                    return activeFilterRenderer;
                }
            }
        }

        public bool FilterEnabled
        {
            get
            {
                lock (stateLock)
                {
                    return filterEnabled;
                }
            }
        }

        public ColorFilterSettings ColorFilter
        {
            get
            {
                lock (stateLock)
                {
                    return colorFilter;
                }
            }
        }

        public void Toggle()
        {
            bool changed;
            lock (stateLock)
            {
                ThrowIfDisposed();
                if (active)
                {
                    changed = DeactivateAllLocked();
                }
                else
                {
                    latchedActive = true;
                    changed = UpdateEffectiveStateLocked();
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }

        public void SetActive(bool shouldBeActive)
        {
            bool changed;
            lock (stateLock)
            {
                ThrowIfDisposed();
                if (shouldBeActive)
                {
                    latchedActive = true;
                    changed = UpdateEffectiveStateLocked();
                }
                else
                {
                    changed = DeactivateAllLocked();
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }

        public void SetZoom(float value)
        {
            lock (stateLock)
            {
                ThrowIfDisposed();
                float next = ClampZoom(value);
                if (Math.Abs(next - zoom) < 0.001f)
                {
                    return;
                }

                zoom = next;
                if (active)
                {
                    ApplyOverlayLocked(false);
                }
            }

            RaiseStateChanged();
        }

        public InputBinding ZoomBinding
        {
            get { lock (stateLock) return zoomBinding; }
        }

        public Rectangle DisplayBounds
        {
            get { lock (stateLock) return filterEnabled && !filterDisplay.IsEmpty ? filterDisplay : activeDisplay; }
        }

        public void SetThumbButton(int button) => SetZoomBinding(new InputBinding(button == 2 ? 6 : 5));

        public void SetZoomBinding(InputBinding binding)
        {
            if (!binding.IsValid) throw new ArgumentException("Invalid zoom binding.", nameof(binding));
            lock (stateLock)
            {
                ThrowIfDisposed();
                if (binding == zoomBinding) return;
                zoomBinding = binding;
                thumbHeld = false;
                lastPhysicalThumbState = zoomBinding.IsDown();
                suppressThumbUntilRelease = lastPhysicalThumbState;
                UpdateEffectiveStateLocked();
            }
            RaiseStateChanged();
        }

        public void IncreaseZoom()
        {
            SetZoom(Zoom + 0.25f);
        }

        public void DecreaseZoom()
        {
            SetZoom(Zoom - 0.25f);
        }

        public void ToggleFilter()
        {
            SetFilterEnabled(!FilterEnabled);
        }

        internal bool EnableStartupFilter()
        {
            bool enabled;
            lock (stateLock)
            {
                if (disposed) return false;
                if (filterEnabled) return true;
                DisplayChoice display = DisplayHelper.FindSavedDisplay(DisplayHelper.GetDisplays(), filterMonitorDevice);
                if (display == null || !FilterAvailableLocked()) return false;
                filterDisplay = display.Bounds;
                filterEnabled = true;
                ApplyOverlayLocked(false);
                enabled = !hasReportedError;
            }
            RaiseStateChanged();
            return enabled;
        }

        public void SetFilterEnabled(bool enabled)
        {
            lock (stateLock)
            {
                ThrowIfDisposed();
                bool next = enabled && FilterAvailableLocked();
                if (filterEnabled == next)
                {
                    return;
                }

                filterEnabled = next;
                if (next)
                {
                    // Lock the output selected when display color is enabled. Zoom may subsequently
                    // follow the cursor without moving the driver color target.
                    filterDisplay = Rectangle.Empty;
                    if (!amdRestorePending)
                    {
                        amdFailedForFilterSession = false;
                    }
                }
                ApplyOverlayLocked(next);
                if (!next && !amdRestorePending)
                {
                    amdFailedForFilterSession = false;
                }
            }

            RaiseStateChanged();
        }

        public void SetColorFilter(ColorFilterSettings settings)
        {
            lock (stateLock)
            {
                ThrowIfDisposed();
                ColorFilterSettings next = settings.Clamped();
                if (colorFilter.NearlyEquals(next))
                {
                    return;
                }

                colorFilter = next;
                if (filterEnabled)
                {
                    ApplyOverlayLocked(false);
                }
            }

            RaiseStateChanged();
        }

        internal void SetFilterPreset(int preset)
        {
            if (preset == 0)
            {
                SetColorFilter(ColorFilterSettings.General);
                SetFilterEnabled(true);
            }
            else if (preset == 1)
            {
                SetColorFilter(ColorFilterSettings.Visibility);
                SetFilterEnabled(true);
            }
            else if (preset == 2)
            {
                SetColorFilter(ColorFilterSettings.Night);
                SetFilterEnabled(true);
            }
            else if (preset == 3)
            {
                SetColorFilter(ColorFilterSettings.Gaming);
                SetFilterEnabled(true);
            }
        }

        internal void CycleFilterPreset()
        {
            int preset = ColorFilter.DetectPreset();
            if (!FilterEnabled || preset == 4)
            {
                SetFilterPreset(0);
            }
            else if (preset == 0)
            {
                SetFilterPreset(1);
            }
            else if (preset == 1)
            {
                SetFilterPreset(2);
            }
            else if (preset == 2)
            {
                SetFilterPreset(3);
            }
            else
            {
                SetFilterPreset(0);
            }
        }

        private void InputLoop()
        {
            while (!stopSignal.WaitOne(PollIntervalMilliseconds))
            {
                bool changed = false;
                lock (stateLock)
                {
                    if (disposed) return;
                    bool isDown = zoomBinding.IsDown();
                    if (isDown != lastPhysicalThumbState)
                    {
                        lastPhysicalThumbState = isDown;
                        if (isDown)
                        {
                            if (!suppressThumbUntilRelease) thumbHeld = true;
                        }
                        else
                        {
                            thumbHeld = false;
                            suppressThumbUntilRelease = false;
                        }
                        changed = UpdateEffectiveStateLocked();
                    }
                }
                if (changed) RaiseStateChanged();
            }
        }

        private bool DeactivateAllLocked()
        {
            latchedActive = false;
            thumbHeld = false;
            lastPhysicalThumbState = IsSelectedThumbButtonDown();
            suppressThumbUntilRelease = lastPhysicalThumbState;
            return UpdateEffectiveStateLocked();
        }

        private bool UpdateEffectiveStateLocked()
        {
            bool shouldBeActive = latchedActive || thumbHeld;
            if (active == shouldBeActive)
            {
                return false;
            }

            active = shouldBeActive;
            ApplyOverlayLocked(active);

            if (!active && !filterEnabled)
            {
                ClearReportedError();
            }

            return true;
        }

        private void RememberFilterDisplay(Rectangle bounds)
        {
            filterDisplay = bounds;
            foreach (DisplayChoice display in DisplayHelper.GetDisplays())
                if (display.Bounds == bounds) { filterMonitorDevice = display.DeviceName; break; }
        }

        private void ApplyOverlayLocked(bool chooseDisplay)
        {
            if (chooseDisplay || activeDisplay.Width <= 0 || activeDisplay.Height <= 0)
            {
                NativeMethods.Point nativeCursor;
                if (!NativeMethods.GetCursorPos(out nativeCursor))
                {
                    ReportError("Windows could not locate the active display.",
                        Marshal.GetLastWin32Error());
                    activeDisplay = DisplayHelper.GetPrimaryBounds();
                }
                else
                {
                    activeDisplay = DisplayHelper.GetBoundsFromPoint(nativeCursor.X, nativeCursor.Y);
                }
            }

            if (filterEnabled &&
                (filterDisplay.Width <= 0 || filterDisplay.Height <= 0))
            {
                RememberFilterDisplay(activeDisplay);
            }

            int errorCode;
            if (filterEnabled)
            {
                if (amdRestorePending)
                {
                    activeFilterRenderer = "AMD restore pending";
                    if (filterOverlay != null &&
                        !object.ReferenceEquals(zoomOverlay, filterOverlay))
                    {
                        filterOverlay.Hide();
                    }
                    ShowZoomOnlyLocked();
                    ReportError(amdColor == null
                        ? "AMD color restoration is still pending."
                        : amdColor.Status, 0);
                    return;
                }

                if (!amdFailedForFilterSession &&
                    amdColor != null && amdColor.Available)
                {
                    if (amdColor.Apply(colorFilter, filterDisplay))
                    {
                        amdRestorePending = false;
                        activeFilterRenderer = "AMD display driver";
                        if (filterOverlay != null &&
                            !object.ReferenceEquals(zoomOverlay, filterOverlay))
                        {
                            filterOverlay.Hide();
                        }

                        if (ShowZoomOnlyLocked())
                        {
                            ClearReportedError();
                        }
                        return;
                    }

                    // Never stack the shader on top of an uncertain partial AMD
                    // change. The native bridge rolls back on apply failure; this
                    // second restore verifies the safe fallback boundary.
                    if (!amdColor.Restore())
                    {
                        amdFailedForFilterSession = true;
                        amdRestorePending = true;
                        activeFilterRenderer = "AMD restore pending";
                        zoomOverlay.Hide();
                        if (filterOverlay != null &&
                            !object.ReferenceEquals(zoomOverlay, filterOverlay))
                        {
                            filterOverlay.Hide();
                        }
                        ReportError(amdColor.Status, 0);
                        return;
                    }
                    amdFailedForFilterSession = true;
                    amdRestorePending = false;
                }

                if (filterOverlay != null)
                {
                    activeFilterRenderer = "D3D11 shader fallback";
                    if (!object.ReferenceEquals(zoomOverlay, filterOverlay))
                    {
                        zoomOverlay.Hide();
                    }

                    Rectangle fallbackDisplay = filterDisplay;
                    if (active && activeDisplay != filterDisplay)
                    {
                        // A single combined shader window cannot filter one
                        // output while zooming another. In fallback mode it
                        // follows the requested zoom output so zoom is never
                        // shown on the wrong monitor.
                        fallbackDisplay = activeDisplay;
                        RememberFilterDisplay(activeDisplay);
                    }
                    float effectiveZoom = active ? zoom : 1.0f;
                    if (!filterOverlay.Show(
                        fallbackDisplay,
                        effectiveZoom,
                        colorFilter,
                        out errorCode))
                    {
                        ReportError("The GPU color-filter fallback could not be shown.", errorCode);
                    }
                    else
                    {
                        ClearReportedError();
                    }
                    return;
                }

                activeFilterRenderer = "Unavailable";
                ReportError(amdColor == null
                    ? "No color-filter backend is available."
                    : amdColor.Status, 0);
            }
            else
            {
                bool restored = amdColor == null || amdColor.Restore();
                amdRestorePending = !restored;
                activeFilterRenderer = restored ? "Off" : "AMD restore pending";
                filterDisplay = Rectangle.Empty;
                if (filterOverlay != null && !object.ReferenceEquals(zoomOverlay, filterOverlay))
                {
                    filterOverlay.Hide();
                }

                bool zoomShown = ShowZoomOnlyLocked();
                if (!restored)
                {
                    ReportError(amdColor.Status, 0);
                }
                else if (zoomShown)
                {
                    ClearReportedError();
                }
            }
        }

        private bool ShowZoomOnlyLocked()
        {
            if (!active)
            {
                zoomOverlay.Hide();
                return true;
            }

            if (!zoomOverlay.Show(
                activeDisplay,
                zoom,
                ColorFilterSettings.Neutral,
                out int errorCode))
            {
                ReportError("Windows could not show the magnification overlay.", errorCode);
                return false;
            }

            return true;
        }

        private bool FilterAvailableLocked()
        {
            if (amdRestorePending)
            {
                return false;
            }
            if (amdColor != null && amdColor.SafetyBlocked)
            {
                return false;
            }
            return (amdColor != null && amdColor.Available) || filterOverlay != null;
        }

        private bool IsSelectedThumbButtonDown() => zoomBinding.IsDown();

        private static float ClampZoom(float value)
        {
            if (!float.IsFinite(value)) return 2.0f;
            return Math.Max(MinimumZoom, Math.Min(MaximumZoom, value));
        }

        private void RaiseStateChanged()
        {
            EventHandler handler = StateChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void ReportError(string message, int errorCode)
        {
            hasReportedError = true;
            EventHandler<EngineErrorEventArgs> handler = Error;
            if (handler != null)
            {
                handler(this, new EngineErrorEventArgs(message, errorCode));
            }
        }

        private void ClearReportedError()
        {
            if (!hasReportedError)
            {
                return;
            }

            hasReportedError = false;
            EventHandler<EngineErrorEventArgs> handler = Error;
            if (handler != null)
            {
                handler(this, new EngineErrorEventArgs(string.Empty, 0));
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("MagnifierEngine");
            }
        }

        public void Dispose()
        {
            AmdDriverColorController driverColor;
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                active = false;
                filterEnabled = false;
                latchedActive = false;
                thumbHeld = false;
                zoomOverlay.Hide();
                if (filterOverlay != null && !object.ReferenceEquals(zoomOverlay, filterOverlay))
                {
                    filterOverlay.Hide();
                }
                driverColor = amdColor;
                amdColor = null;
            }

            stopSignal.Set();
            bool inputThreadStopped = true;
            if (Thread.CurrentThread != inputThread)
            {
                inputThreadStopped = inputThread.Join(1000);
            }

            if (driverColor != null)
            {
                driverColor.Dispose();
            }

            lock (stateLock)
            {
                if (filterOverlay != null && !object.ReferenceEquals(zoomOverlay, filterOverlay))
                {
                    filterOverlay.Dispose();
                    filterOverlay = null;
                }

                if (zoomOverlay != null)
                {
                    zoomOverlay.Dispose();
                    zoomOverlay = null;
                }

                filterOverlay = null;

                if (timerResolutionActive)
                {
                    NativeMethods.timeEndPeriod(1);
                    timerResolutionActive = false;
                }
            }

            if (inputThreadStopped && Thread.CurrentThread != inputThread)
            {
                stopSignal.Dispose();
            }
        }

        private void OverlayError(object sender, ZoomBackendErrorEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Message))
            {
                ClearReportedError();
            }
            else
            {
                ReportError(e.Message, e.ErrorCode);
            }
        }
    }

    internal sealed class EngineErrorEventArgs : EventArgs
    {
        internal EngineErrorEventArgs(string message, int nativeErrorCode)
        {
            Message = message;
            NativeErrorCode = nativeErrorCode;
        }

        internal string Message { get; private set; }
        internal int NativeErrorCode { get; private set; }
    }
}

