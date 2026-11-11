using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AccessibilityZoom
{
    /// <summary>
    /// Loads the small, embedded native bridge that talks to AMD's documented
    /// ADLX display interfaces. All public methods are serialized because ADLX
    /// display changes and shutdown must never overlap.
    /// </summary>
    internal sealed class AmdDriverColorController : IDisposable
    {
        private const string ResourceName =
            "AccessibilityZoom.Native.AmdAdlxBridge.dll";

        private readonly object sync = new object();
        private IntPtr library;
        private InitializeDelegate initialize;
        private GetCapabilitiesDelegate getCapabilities;
        private ApplyDelegate apply;
        private RestoreDelegate restore;
        private GetLastErrorDelegate getLastError;
        private ShutdownDelegate shutdown;
        private bool initialized;
        private bool disposed;
        private bool active;
        private bool hasLastSettings;
        private ColorFilterSettings lastSettings;
        private Rectangle targetDisplay;
        private NativeCapabilities capabilities;
        private string status = string.Empty;

        internal AmdDriverColorController()
        {
            try
            {
                string bridgePath = ExtractBridge();
                library = NativeLibrary.Load(bridgePath);
                initialize = Load<InitializeDelegate>("AmdColor_Initialize");
                getCapabilities = Load<GetCapabilitiesDelegate>("AmdColor_GetCapabilities");
                apply = Load<ApplyDelegate>("AmdColor_Apply");
                restore = Load<RestoreDelegate>("AmdColor_Restore");
                getLastError = Load<GetLastErrorDelegate>("AmdColor_GetLastError");
                shutdown = Load<ShutdownDelegate>("AmdColor_Shutdown");

                string dataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AccessibilityZoom");
                Directory.CreateDirectory(dataFolder);
                string recoveryPath = Path.Combine(
                    dataFolder,
                    "amd-color-recovery.bin");

                int result = initialize(recoveryPath);
                if (result != 0)
                {
                    status = ReadNativeError("AMD ADLX initialization failed (" + result + ").");
                    return;
                }

                initialized = true;
                result = getCapabilities(out capabilities);
                if (result != 0)
                {
                    status = ReadNativeError("AMD capability detection failed (" + result + ").");
                    return;
                }

                if (capabilities.Reserved == 1)
                {
                    status = ReadNativeError(
                        "AMD color recovery is still pending; the driver path is locked for safety.");
                    return;
                }

                if (capabilities.FullFilterDisplayCount > 0)
                {
                    status = capabilities.Reserved == 2
                        ? ReadNativeError(
                            "External AMD color changes were preserved during recovery.")
                        : "AMD ADLX direct driver color is ready on " +
                            capabilities.FullFilterDisplayCount + " display(s).";
                }
                else
                {
                    status = "The AMD driver does not expose the complete color pipeline on this display.";
                }
            }
            catch (Exception exception)
            {
                status = "AMD ADLX is unavailable: " + exception.Message;
            }
        }

        internal bool Available
        {
            get
            {
                lock (sync)
                {
                    return !disposed && initialized &&
                        capabilities.FullFilterDisplayCount > 0;
                }
            }
        }

        internal bool SafetyBlocked
        {
            get
            {
                lock (sync)
                {
                    return !disposed && initialized && capabilities.Reserved == 1;
                }
            }
        }

        internal bool Active
        {
            get
            {
                lock (sync)
                {
                    return active;
                }
            }
        }

        internal string Status
        {
            get
            {
                lock (sync)
                {
                    return status;
                }
            }
        }

        internal int DisplayCount
        {
            get { return capabilities.DisplayCount; }
        }

        internal bool Apply(ColorFilterSettings settings, Rectangle display)
        {
            lock (sync)
            {
                if (disposed || !initialized ||
                    capabilities.FullFilterDisplayCount <= 0)
                {
                    return false;
                }

                ColorFilterSettings clamped = settings.Clamped();
                if (active && targetDisplay != display)
                {
                    status = "The AMD color target remains locked until the filter is turned off.";
                    return false;
                }
                if (active && hasLastSettings &&
                    lastSettings.NearlyEquals(clamped) &&
                    targetDisplay == display)
                {
                    return true;
                }

                NativeSettings native = new NativeSettings
                {
                    Gamma = clamped.Gamma,
                    Brightness = clamped.Brightness,
                    Contrast = clamped.Contrast,
                    Saturation = clamped.Saturation,
                    ShadowBoost = clamped.ShadowBoost,
                    HighlightProtection = clamped.HighlightProtection,
                    RedGain = clamped.RedGain,
                    GreenGain = clamped.GreenGain,
                    BlueGain = clamped.BlueGain,
                    ImageSharpeningEnabled = clamped.RadeonImageSharpening ? 1 : 0,
                    ImageSharpeningSharpness =
                        clamped.RadeonImageSharpeningSharpness
                };
                int result = apply(
                    ref native,
                    display.Left,
                    display.Top,
                    display.Width,
                    display.Height);
                if (result != 0)
                {
                    active = false;
                    hasLastSettings = false;
                    status = ReadNativeError(
                        "AMD rejected the complete color profile (" + result + ").");
                    return false;
                }

                active = true;
                hasLastSettings = true;
                lastSettings = clamped;
                targetDisplay = display;
                status = clamped.RadeonImageSharpening
                    ? "AMD display color and Radeon Image Sharpening at " +
                        clamped.RadeonImageSharpeningSharpness + "% are active."
                    : "AMD ADLX direct driver color is active.";
                return true;
            }
        }

        internal bool Restore()
        {
            lock (sync)
            {
                if (!initialized || disposed)
                {
                    return true;
                }

                int result = restore();
                if (result != 0)
                {
                    status = ReadNativeError(
                        "AMD could not fully restore the original display colors (" +
                        result + ").");
                    return false;
                }

                active = false;
                hasLastSettings = false;
                status = "Original AMD display colors restored.";
                return true;
            }
        }

        private T Load<T>(string name) where T : Delegate
        {
            IntPtr function = NativeLibrary.GetExport(library, name);
            return Marshal.GetDelegateForFunctionPointer<T>(function);
        }

        private string ReadNativeError(string fallback)
        {
            try
            {
                StringBuilder buffer = new StringBuilder(1024);
                if (getLastError != null && getLastError(buffer, buffer.Capacity) == 0 &&
                    buffer.Length > 0)
                {
                    return buffer.ToString();
                }
            }
            catch
            {
                // Preserve the caller's useful fallback message.
            }
            return fallback;
        }

        private static string ExtractBridge()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessibilityZoom",
                "native",
                "adlx-bridge-1");
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, "AmdAdlxBridge.dll");
            string temporary = destination + "." +
                Environment.ProcessId + ".new";

            using (Stream resource = assembly.GetManifestResourceStream(ResourceName))
            {
                if (resource == null)
                {
                    throw new InvalidOperationException(
                        "The embedded AMD ADLX bridge is missing.");
                }

                using (FileStream output = new FileStream(
                    temporary,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    resource.CopyTo(output);
                    output.Flush(true);
                }
            }

            File.Move(temporary, destination, true);
            return destination;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                if (initialized)
                {
                    try { restore(); }
                    catch { }
                    try { shutdown(); }
                    catch { }
                }

                initialized = false;
                active = false;
                disposed = true;
                if (library != IntPtr.Zero)
                {
                    NativeLibrary.Free(library);
                    library = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct NativeCapabilities
        {
            internal int DisplayCount;
            internal int FullFilterDisplayCount;
            internal int GammaDisplayCount;
            internal int CustomColorDisplayCount;
            internal int BrightnessDisplayCount;
            internal int ContrastDisplayCount;
            internal int SaturationDisplayCount;
            internal int Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct NativeSettings
        {
            internal float Gamma;
            internal float Brightness;
            internal float Contrast;
            internal float Saturation;
            internal float ShadowBoost;
            internal float HighlightProtection;
            internal float RedGain;
            internal float GreenGain;
            internal float BlueGain;
            internal int ImageSharpeningEnabled;
            internal int ImageSharpeningSharpness;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int InitializeDelegate(
            [MarshalAs(UnmanagedType.LPWStr)] string recoveryPath);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetCapabilitiesDelegate(
            out NativeCapabilities capabilities);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ApplyDelegate(
            ref NativeSettings settings,
            int left,
            int top,
            int width,
            int height);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RestoreDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int GetLastErrorDelegate(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer,
            int characterCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ShutdownDelegate();
    }
}
