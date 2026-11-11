using System;

namespace AccessibilityZoom
{
    // Stand-in engine for --ui-preview: exercises the whole UI without touching
    // the user's displays, so the new look can be reviewed on any machine.
    internal sealed class PreviewZoomController : IZoomController
    {
        public event EventHandler StateChanged;

        public bool Active { get; private set; }
        public float Zoom { get; private set; } = 2.0f;
        public int ThumbButton => ZoomBinding.Key == 6 ? 2 : 1;
        public InputBinding ZoomBinding { get; private set; } = new InputBinding(5);
        public System.Drawing.Rectangle DisplayBounds => DisplayHelper.GetPrimaryBounds();
        public string RendererName => "Preview renderer";
        public bool FilterAvailable => true;
        public string ActiveFilterRenderer => "Preview color path";
        public bool FilterEnabled { get; private set; } = true;
        public string FilterMonitorDevice => "";
        public ColorFilterSettings ColorFilter { get; private set; } = ColorFilterSettings.General;

        public void Toggle() { Active = !Active; Notify(); }
        public void SetActive(bool active) { Active = active; Notify(); }
        public void SetZoom(float zoom) { Zoom = Math.Max(1.25f, Math.Min(8f, zoom)); Notify(); }
        public void SetThumbButton(int button) => SetZoomBinding(new InputBinding(button == 2 ? 6 : 5));
        public void SetZoomBinding(InputBinding binding) { ZoomBinding = binding; Notify(); }
        public void IncreaseZoom() => SetZoom(Zoom + 0.25f);
        public void DecreaseZoom() => SetZoom(Zoom - 0.25f);
        public void ToggleFilter() { FilterEnabled = !FilterEnabled; Notify(); }
        public void SetFilterEnabled(bool enabled) { FilterEnabled = enabled; Notify(); }
        public void SetColorFilter(ColorFilterSettings settings) { ColorFilter = settings; Notify(); }

        private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
