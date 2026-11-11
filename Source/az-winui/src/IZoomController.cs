using System;

namespace AccessibilityZoom
{
    // Keeps the control panel testable without touching the user's displays.
    internal interface IZoomController
    {
        event EventHandler StateChanged;
        bool Active { get; }
        float Zoom { get; }
        int ThumbButton { get; }
        InputBinding ZoomBinding { get; }
        System.Drawing.Rectangle DisplayBounds { get; }
        string RendererName { get; }
        bool FilterAvailable { get; }
        string ActiveFilterRenderer { get; }
        bool FilterEnabled { get; }
        string FilterMonitorDevice { get; }
        ColorFilterSettings ColorFilter { get; }
        void Toggle();
        void SetActive(bool active);
        void SetZoom(float zoom);
        void SetThumbButton(int button);
        void SetZoomBinding(InputBinding binding);
        void IncreaseZoom();
        void DecreaseZoom();
        void ToggleFilter();
        void SetFilterEnabled(bool enabled);
        void SetColorFilter(ColorFilterSettings settings);
    }
}
