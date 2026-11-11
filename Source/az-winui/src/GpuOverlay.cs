using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace AccessibilityZoom
{
    /// <summary>
    /// GPU-only desktop duplication and zoom renderer. The input thread never waits
    /// for a capture or Present call; it only updates the requested state.
    /// </summary>
    internal sealed class GpuOverlay : IZoomBackend
    {
        private static readonly FeatureLevel[] FeatureLevels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        private readonly object stateLock = new object();
        private readonly OverlayWindow window;
        private readonly Thread renderThread;
        private readonly ManualResetEvent stopSignal = new ManualResetEvent(false);
        private readonly ManualResetEvent initializationComplete = new ManualResetEvent(false);
        private readonly AutoResetEvent requestSignal = new AutoResetEvent(false);
        private Rectangle requestedDisplay;
        private float requestedZoom = 2.0f;
        private ColorFilterSettings requestedColorFilter = ColorFilterSettings.Neutral;
        private bool requestedActive;
        private int showGeneration;
        private bool disposed;
        private Exception initializationError;

        internal GpuOverlay(Rectangle initialDisplay)
        {
            requestedDisplay = initialDisplay;
            window = new OverlayWindow();
            NativeMethods.SetWindowDisplayAffinity(
                window.Handle,
                NativeMethods.WDA_EXCLUDEFROMCAPTURE);

            renderThread = new Thread(RenderLoop);
            renderThread.Name = "Accessibility Zoom DXGI GPU renderer";
            renderThread.IsBackground = true;
            renderThread.Priority = ThreadPriority.AboveNormal;
            renderThread.Start();

            if (!initializationComplete.WaitOne(10000))
            {
                Dispose();
                throw new InvalidOperationException(
                    "The Direct3D renderer did not start within 10 seconds.");
            }

            if (initializationError != null)
            {
                Exception error = initializationError;
                Dispose();
                throw new InvalidOperationException(
                    "The Direct3D desktop renderer could not start. " + error.Message,
                    error);
            }
        }

        public event EventHandler<ZoomBackendErrorEventArgs> Error;

        public string Name
        {
            get { return "Direct3D 11 GPU shader"; }
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
                requestedColorFilter = colorFilter.Clamped();
                requestedActive = true;
                unchecked
                {
                    showGeneration++;
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
                requestedActive = false;
            }

            requestSignal.Set();
            // Hide synchronously so releasing the thumb button is never held up by
            // capture, shader, or Present work on the renderer thread.
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_HIDE);
        }

        private void RenderLoop()
        {
            RenderResources resources = null;
            bool firstInitialization = true;
            Rectangle resourceDisplay = Rectangle.Empty;
            float renderedZoom = 0.0f;
            ColorFilterSettings renderedColorFilter = ColorFilterSettings.Neutral;
            bool lastActive = false;
            bool hasFrame = false;
            bool errorReported = false;
            int lastShownGeneration = int.MinValue;

            try
            {
                while (!IsStopRequested())
                {
                    Rectangle display;
                    float zoom;
                    ColorFilterSettings colorFilter;
                    bool active;
                    int currentShowGeneration;
                    lock (stateLock)
                    {
                        display = requestedDisplay;
                        zoom = requestedZoom;
                        colorFilter = requestedColorFilter;
                        active = requestedActive;
                        currentShowGeneration = showGeneration;
                    }

                    try
                    {
                        if (resources == null || resourceDisplay != display)
                        {
                            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_HIDE);
                            if (resources != null)
                            {
                                resources.Dispose();
                            }

                            NativeMethods.SetWindowPos(
                                window.Handle,
                                NativeMethods.HwndTopmost,
                                display.Left,
                                display.Top,
                                display.Width,
                                display.Height,
                                NativeMethods.SWP_NOACTIVATE);

                            resources = RenderResources.Create(
                                window.Handle,
                                display,
                                zoom,
                                colorFilter);
                            resourceDisplay = display;
                            renderedZoom = zoom;
                            renderedColorFilter = colorFilter;
                            hasFrame = false;
                            errorReported = false;
                            lastShownGeneration = int.MinValue;

                            if (firstInitialization)
                            {
                                firstInitialization = false;
                                SignalInitializationComplete();
                            }
                        }

                        bool zoomChanged = Math.Abs(zoom - renderedZoom) > 0.001f;
                        if (zoomChanged)
                        {
                            resources.SetZoom(zoom);
                            renderedZoom = zoom;
                            hasFrame = false;
                        }

                        if (!colorFilter.NearlyEquals(renderedColorFilter))
                        {
                            resources.SetColorFilter(colorFilter);
                            renderedColorFilter = colorFilter;
                        }

                        if (!active)
                        {
                            if (lastActive)
                            {
                                NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_HIDE);
                            }

                            lastActive = false;
                            WaitHandleIgnoringDispose(requestSignal, 50);
                            continue;
                        }

                        // Block in DXGI for at most 1 ms instead of polling and then
                        // sleeping. This removes an extra scheduler round-trip and
                        // still bounds input-to-overlay response to roughly 1 ms.
                        bool receivedFrame = resources.TryAcquireLatestFrame(1);
                        hasFrame |= receivedFrame;

                        bool activationChanged = active != lastActive;
                        bool showPending = currentShowGeneration != lastShownGeneration;
                        if (active && hasFrame &&
                            (receivedFrame || activationChanged || showPending))
                        {
                            resources.Render();
                            if (showPending)
                            {
                                // Window-manager work happens once per activation,
                                // never once per captured frame.
                                NativeMethods.ShowWindow(
                                    window.Handle,
                                    NativeMethods.SW_SHOWNOACTIVATE);
                                lastShownGeneration = currentShowGeneration;
                            }
                        }
                        else if (!active && lastActive)
                        {
                            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_HIDE);
                        }

                        if (errorReported)
                        {
                            RaiseError(string.Empty, 0);
                            errorReported = false;
                        }

                        lastActive = active;

                    }
                    catch (Exception exception)
                    {
                        NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_HIDE);
                        if (firstInitialization)
                        {
                            initializationError = exception;
                            SignalInitializationComplete();
                            return;
                        }

                        if (!errorReported)
                        {
                            RaiseError(
                                "The GPU capture was interrupted; Accessibility Zoom is reconnecting.",
                                exception.HResult);
                            errorReported = true;
                        }

                        if (resources != null)
                        {
                            resources.Dispose();
                            resources = null;
                        }

                        resourceDisplay = Rectangle.Empty;
                        hasFrame = false;
                        lastActive = false;
                        lastShownGeneration = int.MinValue;
                        WaitHandleIgnoringDispose(stopSignal, 100);
                    }
                }
            }
            catch (Exception exception)
            {
                initializationError = exception;
                if (firstInitialization)
                {
                    SignalInitializationComplete();
                }
                else
                {
                    RaiseError("The Direct3D renderer stopped.", exception.HResult);
                }
            }
            finally
            {
                if (resources != null)
                {
                    resources.Dispose();
                }

                NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_HIDE);
                if (firstInitialization)
                {
                    SignalInitializationComplete();
                }
            }
        }

        // The render thread can outlive Dispose when Join times out: a late
        // WaitOne/Set on a disposed handle must end the loop, never crash it.
        private bool IsStopRequested()
        {
            try { return stopSignal.WaitOne(0); }
            catch (ObjectDisposedException) { return true; }
        }

        private static void WaitHandleIgnoringDispose(WaitHandle handle, int timeoutMilliseconds)
        {
            try { handle.WaitOne(timeoutMilliseconds); }
            catch (ObjectDisposedException) { }
        }

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
            }

            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_HIDE);
            stopSignal.Set();
            requestSignal.Set();
            if (Thread.CurrentThread != renderThread)
            {
                renderThread.Join(3000);
            }

            window.Dispose();
            requestSignal.Dispose();
            initializationComplete.Dispose();
            stopSignal.Dispose();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ZoomVertex
        {
            internal ZoomVertex(float x, float y, float u, float v)
            {
                Position = new Vector2(x, y);
                Texcoord = new Vector2(u, v);
            }

            internal Vector2 Position;
            internal Vector2 Texcoord;

            internal const uint SizeInBytes = 16;

            internal static readonly InputElementDescription[] InputElements =
            {
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
            };
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ShaderFilterConstants
        {
            internal ShaderFilterConstants(ColorFilterSettings settings)
            {
                Tone = new Vector4(
                    settings.Gamma,
                    settings.Brightness,
                    settings.Contrast,
                    settings.Saturation);
                ColorAndShadows = new Vector4(
                    settings.RedGain,
                    settings.GreenGain,
                    settings.BlueGain,
                    settings.ShadowBoost);
                Visibility = new Vector4(
                    settings.HighlightProtection,
                    0.0f,
                    0.0f,
                    0.0f);
            }

            internal Vector4 Tone;
            internal Vector4 ColorAndShadows;
            internal Vector4 Visibility;
        }

        private sealed class RenderResources : IDisposable
        {
            private readonly IDXGIFactory2 factory;
            private readonly IDXGIAdapter1 adapter;
            private readonly IDXGIOutput1 output;
            private readonly ID3D11Device device;
            private readonly ID3D11DeviceContext context;
            private readonly IDXGIOutputDuplication duplication;
            private readonly IDXGISwapChain1 swapChain;
            private readonly ID3D11Texture2D backBuffer;
            private readonly ID3D11RenderTargetView renderTarget;
            private ID3D11Texture2D capturedFrame;
            private ID3D11ShaderResourceView capturedFrameView;
            private readonly ID3D11SamplerState sampler;
            private readonly ID3D11VertexShader vertexShader;
            private readonly ID3D11PixelShader pixelShader;
            private readonly ID3D11Buffer colorFilterBuffer;
            private readonly ID3D11InputLayout inputLayout;
            private ID3D11Buffer vertexBuffer;
            private readonly bool allowTearing;
            private readonly uint sourceWidth;
            private readonly uint sourceHeight;
            private readonly Format sourceFormat;
            private Box captureRegion;
            private bool frameAcquired;

            private RenderResources(
                IDXGIFactory2 factory,
                IDXGIAdapter1 adapter,
                IDXGIOutput1 output,
                ID3D11Device device,
                ID3D11DeviceContext context,
                IDXGIOutputDuplication duplication,
                IDXGISwapChain1 swapChain,
                ID3D11Texture2D backBuffer,
                ID3D11RenderTargetView renderTarget,
                ID3D11Texture2D capturedFrame,
                ID3D11ShaderResourceView capturedFrameView,
                ID3D11SamplerState sampler,
                ID3D11VertexShader vertexShader,
                ID3D11PixelShader pixelShader,
                ID3D11Buffer colorFilterBuffer,
                ID3D11InputLayout inputLayout,
                ID3D11Buffer vertexBuffer,
                bool allowTearing,
                uint sourceWidth,
                uint sourceHeight,
                Format sourceFormat)
            {
                this.factory = factory;
                this.adapter = adapter;
                this.output = output;
                this.device = device;
                this.context = context;
                this.duplication = duplication;
                this.swapChain = swapChain;
                this.backBuffer = backBuffer;
                this.renderTarget = renderTarget;
                this.capturedFrame = capturedFrame;
                this.capturedFrameView = capturedFrameView;
                this.sampler = sampler;
                this.vertexShader = vertexShader;
                this.pixelShader = pixelShader;
                this.colorFilterBuffer = colorFilterBuffer;
                this.inputLayout = inputLayout;
                this.vertexBuffer = vertexBuffer;
                this.allowTearing = allowTearing;
                this.sourceWidth = sourceWidth;
                this.sourceHeight = sourceHeight;
                this.sourceFormat = sourceFormat;
            }

            internal static RenderResources Create(
                IntPtr window,
                Rectangle display,
                float initialZoom,
                ColorFilterSettings initialColorFilter)
            {
                IDXGIFactory2 factory = CreateDXGIFactory1<IDXGIFactory2>();
                IDXGIAdapter1 selectedAdapter = null;
                IDXGIOutput1 selectedOutput = null;

                int centerX = display.Left + (display.Width / 2);
                int centerY = display.Top + (display.Height / 2);
                for (uint adapterIndex = 0;
                    factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 candidateAdapter).Success;
                    adapterIndex++)
                {
                    bool keepAdapter = false;
                    for (uint outputIndex = 0;
                        candidateAdapter.EnumOutputs(outputIndex, out IDXGIOutput candidateOutput).Success;
                        outputIndex++)
                    {
                        OutputDescription description = candidateOutput.Description;
                        var bounds = description.DesktopCoordinates;
                        bool contains = centerX >= bounds.Left && centerX < bounds.Right &&
                            centerY >= bounds.Top && centerY < bounds.Bottom;
                        if (contains)
                        {
                            selectedAdapter = candidateAdapter;
                            selectedOutput = candidateOutput.QueryInterface<IDXGIOutput1>();
                            keepAdapter = true;
                            candidateOutput.Dispose();
                            break;
                        }

                        candidateOutput.Dispose();
                    }

                    if (keepAdapter)
                    {
                        break;
                    }

                    candidateAdapter.Dispose();
                }

                if (selectedAdapter == null || selectedOutput == null)
                {
                    factory.Dispose();
                    throw new InvalidOperationException("No DirectX output matches the selected display.");
                }

                ID3D11Device device = null;
                ID3D11DeviceContext context = null;
                try
                {
                    D3D11CreateDevice(
                        selectedAdapter,
                        DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport,
                        FeatureLevels,
                        out device,
                        out FeatureLevel actualFeatureLevel,
                        out context).CheckError();

                    using (IDXGIDevice1 dxgiDevice = device.QueryInterface<IDXGIDevice1>())
                    {
                        dxgiDevice.SetMaximumFrameLatency(1).CheckError();
                    }

                    IDXGIOutputDuplication duplication = selectedOutput.DuplicateOutput(device);
                    var duplicationDescription = duplication.Description;

                    bool allowTearing = false;
                    using (IDXGIFactory5 factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
                    {
                        allowTearing = factory5 != null && factory5.PresentAllowTearing;
                    }

                    SwapChainDescription1 swapDescription = new SwapChainDescription1
                    {
                        Width = (uint)display.Width,
                        Height = (uint)display.Height,
                        Format = Format.B8G8R8A8_UNorm,
                        BufferCount = 2,
                        BufferUsage = Usage.RenderTargetOutput,
                        SampleDescription = SampleDescription.Default,
                        Scaling = Scaling.Stretch,
                        SwapEffect = SwapEffect.FlipDiscard,
                        AlphaMode = AlphaMode.Ignore,
                        Flags = allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None
                    };
                    SwapChainFullscreenDescription fullscreenDescription =
                        new SwapChainFullscreenDescription { Windowed = true };
                    IDXGISwapChain1 swapChain = factory.CreateSwapChainForHwnd(
                        device,
                        window,
                        swapDescription,
                        fullscreenDescription);
                    factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
                    ID3D11Texture2D backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
                    ID3D11RenderTargetView renderTarget =
                        device.CreateRenderTargetView(backBuffer);

                    uint sourceWidth = duplicationDescription.ModeDescription.Width;
                    uint sourceHeight = duplicationDescription.ModeDescription.Height;
                    Format sourceFormat = duplicationDescription.ModeDescription.Format;
                    CalculateCrop(
                        sourceWidth,
                        sourceHeight,
                        initialZoom,
                        out Box initialCaptureRegion,
                        out uint initialCropWidth,
                        out uint initialCropHeight);
                    Texture2DDescription captureDescription = CreateCropTextureDescription(
                        sourceFormat,
                        initialCropWidth,
                        initialCropHeight);
                    ID3D11Texture2D capturedFrame =
                        device.CreateTexture2D(captureDescription);
                    ID3D11ShaderResourceView capturedFrameView =
                        device.CreateShaderResourceView(capturedFrame);
                    ID3D11SamplerState sampler =
                        device.CreateSamplerState(SamplerDescription.LinearClamp);

                    string shaderSource = LoadShaderSource();
                    ReadOnlyMemory<byte> vertexBytecode = Compiler.Compile(
                        shaderSource,
                        "VSMain",
                        "Zoom.hlsl",
                        "vs_4_0",
                        ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3);
                    ReadOnlyMemory<byte> pixelBytecode = Compiler.Compile(
                        shaderSource,
                        "PSMain",
                        "Zoom.hlsl",
                        "ps_4_0",
                        ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3);
                    ID3D11VertexShader vertexShader =
                        device.CreateVertexShader(vertexBytecode.Span);
                    ID3D11PixelShader pixelShader =
                        device.CreatePixelShader(pixelBytecode.Span);
                    ID3D11Buffer colorFilterBuffer =
                        device.CreateConstantBuffer<ShaderFilterConstants>();
                    ID3D11InputLayout inputLayout =
                        device.CreateInputLayout(ZoomVertex.InputElements, vertexBytecode.Span);

                    ZoomVertex[] initialVertices = CreateVertices();
                    ID3D11Buffer vertexBuffer =
                        device.CreateBuffer(initialVertices, BindFlags.VertexBuffer);

                    RenderResources resources = new RenderResources(
                        factory,
                        selectedAdapter,
                        selectedOutput,
                        device,
                        context,
                        duplication,
                        swapChain,
                        backBuffer,
                        renderTarget,
                        capturedFrame,
                        capturedFrameView,
                        sampler,
                        vertexShader,
                        pixelShader,
                        colorFilterBuffer,
                        inputLayout,
                        vertexBuffer,
                        allowTearing,
                        sourceWidth,
                        sourceHeight,
                        sourceFormat);
                    resources.captureRegion = initialCaptureRegion;
                    resources.SetColorFilter(initialColorFilter);
                    return resources;
                }
                catch
                {
                    if (context != null)
                    {
                        context.Dispose();
                    }
                    if (device != null)
                    {
                        device.Dispose();
                    }
                    selectedOutput.Dispose();
                    selectedAdapter.Dispose();
                    factory.Dispose();
                    throw;
                }
            }

            internal bool TryAcquireLatestFrame(uint timeoutMilliseconds)
            {
                OutduplFrameInfo frameInfo;
                IDXGIResource desktopResource;
                Result result = duplication.AcquireNextFrame(
                    timeoutMilliseconds,
                    out frameInfo,
                    out desktopResource);
                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    return false;
                }

                result.CheckError();
                frameAcquired = true;
                try
                {
                    using (ID3D11Texture2D desktopTexture =
                        desktopResource.QueryInterface<ID3D11Texture2D>())
                    {
                        context.CopySubresourceRegion(
                            capturedFrame,
                            0,
                            0,
                            0,
                            0,
                            desktopTexture,
                            0,
                            captureRegion);
                    }
                }
                finally
                {
                    desktopResource.Dispose();
                    duplication.ReleaseFrame();
                    frameAcquired = false;
                }

                return true;
            }

            internal void SetZoom(float zoom)
            {
                CalculateCrop(
                    sourceWidth,
                    sourceHeight,
                    zoom,
                    out Box nextCaptureRegion,
                    out uint cropWidth,
                    out uint cropHeight);

                ID3D11Texture2D nextFrame = device.CreateTexture2D(
                    CreateCropTextureDescription(sourceFormat, cropWidth, cropHeight));
                ID3D11ShaderResourceView nextView =
                    device.CreateShaderResourceView(nextFrame);

                context.PSSetShaderResource(0, null);
                ID3D11ShaderResourceView oldView = capturedFrameView;
                ID3D11Texture2D oldFrame = capturedFrame;
                capturedFrameView = nextView;
                capturedFrame = nextFrame;
                captureRegion = nextCaptureRegion;
                oldView.Dispose();
                oldFrame.Dispose();
            }

            internal void SetColorFilter(ColorFilterSettings settings)
            {
                ShaderFilterConstants constants =
                    new ShaderFilterConstants(settings.Clamped());
                context.UpdateSubresource(in constants, colorFilterBuffer);
            }

            internal void Render()
            {
                context.OMSetRenderTargets(renderTarget, null);
                context.RSSetViewport(new Viewport(
                    swapChain.Description1.Width,
                    swapChain.Description1.Height));
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.IASetInputLayout(inputLayout);
                context.IASetVertexBuffer(0, vertexBuffer, ZoomVertex.SizeInBytes);
                context.VSSetShader(vertexShader);
                context.PSSetShader(pixelShader);
                context.PSSetConstantBuffer(0, colorFilterBuffer);
                context.PSSetShaderResource(0, capturedFrameView);
                context.PSSetSampler(0, sampler);
                context.Draw(6, 0);

                PresentFlags flags = allowTearing
                    ? PresentFlags.AllowTearing
                    : PresentFlags.None;
                swapChain.Present(0, flags).CheckError();
            }

            private static ZoomVertex[] CreateVertices()
            {
                return new[]
                {
                    new ZoomVertex(-1.0f,  1.0f, 0.0f, 0.0f),
                    new ZoomVertex( 1.0f,  1.0f, 1.0f, 0.0f),
                    new ZoomVertex(-1.0f, -1.0f, 0.0f, 1.0f),
                    new ZoomVertex(-1.0f, -1.0f, 0.0f, 1.0f),
                    new ZoomVertex( 1.0f,  1.0f, 1.0f, 0.0f),
                    new ZoomVertex( 1.0f, -1.0f, 1.0f, 1.0f)
                };
            }

            private static Texture2DDescription CreateCropTextureDescription(
                Format format,
                uint width,
                uint height)
            {
                return new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = format,
                    SampleDescription = SampleDescription.Default,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                };
            }

            private static void CalculateCrop(
                uint width,
                uint height,
                float zoom,
                out Box region,
                out uint cropWidth,
                out uint cropHeight)
            {
                double safeZoom = Math.Max(1.0, zoom);
                cropWidth = Math.Max(1u, (uint)Math.Round(width / safeZoom));
                cropHeight = Math.Max(1u, (uint)Math.Round(height / safeZoom));
                int left = (int)((width - cropWidth) / 2u);
                int top = (int)((height - cropHeight) / 2u);
                region = new Box(
                    left,
                    top,
                    0,
                    left + (int)cropWidth,
                    top + (int)cropHeight,
                    1);
            }

            private static string LoadShaderSource()
            {
                System.Reflection.Assembly assembly =
                    System.Reflection.Assembly.GetExecutingAssembly();
                using (System.IO.Stream stream = assembly.GetManifestResourceStream(
                    "AccessibilityZoom.Shaders.Zoom.hlsl"))
                {
                    if (stream == null)
                    {
                        throw new InvalidOperationException(
                            "The embedded Direct3D zoom shader is missing.");
                    }

                    using (System.IO.StreamReader reader = new System.IO.StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }

            public void Dispose()
            {
                if (frameAcquired)
                {
                    duplication.ReleaseFrame();
                    frameAcquired = false;
                }

                context.ClearState();
                context.Flush();
                vertexBuffer.Dispose();
                inputLayout.Dispose();
                colorFilterBuffer.Dispose();
                pixelShader.Dispose();
                vertexShader.Dispose();
                sampler.Dispose();
                capturedFrameView.Dispose();
                capturedFrame.Dispose();
                renderTarget.Dispose();
                backBuffer.Dispose();
                swapChain.Dispose();
                duplication.Dispose();
                context.Dispose();
                device.Dispose();
                output.Dispose();
                adapter.Dispose();
                factory.Dispose();
            }
        }

        // Message-only style host window for the swap chain. Implemented with
        // raw Win32 (no WinForms): the engine must not depend on
        // System.Windows.Forms in the WinUI 3 build.
        private sealed class OverlayWindow : IDisposable
        {
            private const string ClassName = "AccessibilityZoomGpuOverlay";
            // Referenced by native code; keep it rooted for process lifetime.
            private static readonly NativeMethods.WndProcDelegate WindowProcedure = WndProc;
            private static bool classRegistered;
            private bool disposed;

            internal IntPtr Handle { get; private set; }

            internal OverlayWindow()
            {
                RegisterWindowClass();
                Handle = NativeMethods.CreateWindowExW(
                    NativeMethods.WS_EX_TOPMOST |
                    NativeMethods.WS_EX_TRANSPARENT |
                    NativeMethods.WS_EX_TOOLWINDOW |
                    NativeMethods.WS_EX_NOACTIVATE,
                    ClassName,
                    "Accessibility Zoom GPU overlay",
                    NativeMethods.WS_POPUP,
                    0, 0, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
                if (Handle == IntPtr.Zero)
                    throw new InvalidOperationException("The GPU overlay window could not be created.");
            }

            private static void RegisterWindowClass()
            {
                if (classRegistered) return;
                NativeMethods.WndClassEx wc = new NativeMethods.WndClassEx
                {
                    lpfnWndProc = WindowProcedure,
                    lpszClassName = ClassName,
                    hInstance = NativeMethods.GetModuleHandleW(null),
                };
                wc.cbSize = (uint)Marshal.SizeOf(wc);
                if (NativeMethods.RegisterClassExW(ref wc) == 0)
                    throw new InvalidOperationException("The GPU overlay window class could not be registered.");
                classRegistered = true;
            }

            private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                const uint WmNcHitTest = 0x0084;
                const uint WmMouseActivate = 0x0021;
                // Click-through and never activate, like the old NativeWindow proc.
                if (msg == WmNcHitTest) return new IntPtr(-1); // HTTRANSPARENT
                if (msg == WmMouseActivate) return new IntPtr(3); // MA_NOACTIVATE
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (Handle != IntPtr.Zero)
                {
                    NativeMethods.DestroyWindow(Handle);
                    Handle = IntPtr.Zero;
                }
            }
        }
    }

}
