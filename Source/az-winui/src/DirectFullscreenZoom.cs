using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace AccessibilityZoom
{
    /// <summary>
    /// Uses Windows' compositor-owned full-screen transform. No desktop frame is
    /// captured or presented by this process, so this is the lowest-latency path.
    /// </summary>
    internal sealed class DirectFullscreenZoom : IZoomBackend
    {
        private readonly object stateLock = new object();
        private readonly AutoResetEvent requestSignal = new AutoResetEvent(false);
        private readonly ManualResetEvent stopSignal = new ManualResetEvent(false);
        private readonly ManualResetEvent initializationComplete = new ManualResetEvent(false);
        private readonly AutoResetEvent appliedSignal = new AutoResetEvent(false);
        private readonly Thread workerThread;
        private Rectangle requestedDisplay;
        private float requestedZoom = 2.0f;
        private bool requestedActive;
        private int requestGeneration;
        private int appliedGeneration = int.MinValue;
        private bool appliedActive;
        private bool disposed;
        private Exception initializationError;

        internal DirectFullscreenZoom(Rectangle initialDisplay)
        {
            requestedDisplay = initialDisplay;
            workerThread = new Thread(WorkerLoop);
            workerThread.Name = "Accessibility Zoom direct compositor worker";
            workerThread.IsBackground = true;
            workerThread.Priority = ThreadPriority.Highest;
            workerThread.Start();

            if (!initializationComplete.WaitOne(5000))
            {
                Dispose();
                throw new InvalidOperationException(
                    "The direct Windows zoom renderer did not start within 5 seconds.");
            }

            if (initializationError != null)
            {
                Exception error = initializationError;
                Dispose();
                throw new InvalidOperationException(
                    "Windows could not initialize direct full-screen zoom. " + error.Message,
                    error);
            }
        }

        public event EventHandler<ZoomBackendErrorEventArgs> Error;

        public string Name
        {
            get { return "Direct Windows compositor (lowest latency)"; }
        }

        public bool Show(
            Rectangle display,
            float zoom,
            ColorFilterSettings colorFilter,
            out int errorCode)
        {
            lock (stateLock)
            {
                if (disposed)
                {
                    errorCode = unchecked((int)0x80004005);
                    return false;
                }

                requestedDisplay = display;
                requestedZoom = zoom;
                requestedActive = true;
                unchecked
                {
                    requestGeneration++;
                }
            }

            requestSignal.Set();
            errorCode = 0;
            return true;
        }

        public void Hide()
        {
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }

                requestedActive = false;
                unchecked
                {
                    requestGeneration++;
                }
            }

            requestSignal.Set();
        }

        internal bool WaitForAppliedState(bool expectedActive, int timeoutMilliseconds)
        {
            int startedAt = Environment.TickCount;
            while (true)
            {
                lock (stateLock)
                {
                    if (appliedGeneration == requestGeneration &&
                        appliedActive == expectedActive)
                    {
                        return true;
                    }
                }

                int elapsed = unchecked(Environment.TickCount - startedAt);
                int remaining = timeoutMilliseconds - elapsed;
                if (remaining <= 0 || !appliedSignal.WaitOne(remaining))
                {
                    return false;
                }
            }
        }

        private void WorkerLoop()
        {
            bool initialized = false;
            bool firstErrorReported = false;
            bool retryPending = false;
            WaitHandle[] waitHandles = { stopSignal, requestSignal };

            try
            {
                if (!NativeMethods.MagInitialize())
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The Windows Magnification runtime could not initialize.");
                }

                initialized = true;

                // The first scaled transform after MagInitialize can make Windows
                // lazily start compositor-side magnification resources. Pay that
                // one-time cost at an imperceptible 1.0001x during app startup,
                // then immediately restore the exact 1x identity transform.
                if (!NativeMethods.MagSetFullscreenTransform(1.0001f, 0, 0) ||
                    !NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not warm up direct full-screen zoom.");
                }

                lock (stateLock)
                {
                    appliedGeneration = requestGeneration;
                    appliedActive = false;
                }

                initializationComplete.Set();

                while (true)
                {
                    int signaled = WaitHandle.WaitAny(
                        waitHandles,
                        retryPending ? 1 : Timeout.Infinite);
                    if (signaled == 0)
                    {
                        break;
                    }

                    Rectangle display;
                    float zoom;
                    bool active;
                    int generation;
                    int lastAppliedGeneration;
                    lock (stateLock)
                    {
                        display = requestedDisplay;
                        zoom = requestedZoom;
                        active = requestedActive;
                        generation = requestGeneration;
                        lastAppliedGeneration = appliedGeneration;
                    }

                    if (!retryPending && generation == lastAppliedGeneration)
                    {
                        continue;
                    }

                    bool succeeded;
                    if (active)
                    {
                        Point offset = ScopeMath.CalculateFixedCenterOffset(display, zoom);
                        succeeded = NativeMethods.MagSetFullscreenTransform(
                            zoom,
                            offset.X,
                            offset.Y);
                    }
                    else
                    {
                        succeeded = NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0);
                    }

                    if (succeeded)
                    {
                        // Query on the owning magnification thread so the test and
                        // status signal only acknowledge an actually applied state.
                        succeeded = NativeMethods.MagGetFullscreenTransform(
                            out float appliedZoom,
                            out int appliedX,
                            out int appliedY) &&
                            (active
                                ? Math.Abs(appliedZoom - zoom) < 0.01f
                                : Math.Abs(appliedZoom - 1.0f) < 0.01f &&
                                  appliedX == 0 && appliedY == 0);
                    }

                    if (succeeded)
                    {
                        lock (stateLock)
                        {
                            appliedGeneration = generation;
                            appliedActive = active;
                        }

                        appliedSignal.Set();
                        retryPending = false;
                        if (firstErrorReported)
                        {
                            RaiseError(string.Empty, 0);
                            firstErrorReported = false;
                        }
                    }
                    else
                    {
                        retryPending = true;
                        if (!firstErrorReported)
                        {
                            RaiseError(
                                active
                                    ? "Windows is retrying the direct zoom transform."
                                    : "Windows is retrying normal desktop view.",
                                Marshal.GetLastWin32Error());
                            firstErrorReported = true;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                initializationError = exception;
                SignalInitializationComplete();
                if (initialized)
                {
                    RaiseError("The direct Windows zoom renderer stopped.", exception.HResult);
                }
            }
            finally
            {
                if (initialized)
                {
                    // A display transition can temporarily reject a reset. Keep
                    // trying briefly so exit cannot strand the desktop magnified.
                    for (int attempt = 0; attempt < 50; attempt++)
                    {
                        if (NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0))
                        {
                            break;
                        }

                        Thread.Sleep(1);
                    }

                    NativeMethods.MagUninitialize();
                }

                SignalInitializationComplete();
            }
        }

        // The worker thread can outlive Dispose when Join times out: signaling
        // a disposed handle must be a no-op, never a crash. The display reset
        // above still runs, so the desktop is never stranded magnified.
        private void SignalInitializationComplete()
        {
            try { initializationComplete.Set(); }
            catch (ObjectDisposedException) { }
        }

        private void RaiseError(string message, int errorCode)
        {
            EventHandler<ZoomBackendErrorEventArgs> handler = Error;
            if (handler != null)
            {
                handler(this, new ZoomBackendErrorEventArgs(message, errorCode));
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                requestedActive = false;
                unchecked
                {
                    requestGeneration++;
                }
            }

            requestSignal.Set();
            stopSignal.Set();
            if (Thread.CurrentThread != workerThread)
            {
                workerThread.Join(3000);
            }

            initializationComplete.Dispose();
            requestSignal.Dispose();
            stopSignal.Dispose();
            appliedSignal.Dispose();
        }
    }
}
