using System;
using System.Drawing;

namespace AccessibilityZoom
{
    internal interface IZoomBackend : IDisposable
    {
        event EventHandler<ZoomBackendErrorEventArgs> Error;

        string Name { get; }

        bool Show(
            Rectangle display,
            float zoom,
            ColorFilterSettings colorFilter,
            out int errorCode);

        void Hide();
    }

    internal sealed class ZoomBackendErrorEventArgs : EventArgs
    {
        internal ZoomBackendErrorEventArgs(string message, int errorCode)
        {
            Message = message;
            ErrorCode = errorCode;
        }

        internal string Message { get; private set; }
        internal int ErrorCode { get; private set; }
    }
}
