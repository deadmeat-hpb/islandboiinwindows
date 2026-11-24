using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace DynamicIsland;

/// <summary>
/// Captures one monitor so the glass can refract what's behind it.
/// The island window excludes itself from capture, so it never sees itself.
/// </summary>
sealed class ScreenCapture : IDisposable
{
    readonly CanvasDevice _device;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly int _height;

    public CanvasRenderTarget? Image { get; private set; }

    /// <param name="height">How many rows from the top of the monitor to keep.</param>
    public ScreenCapture(CanvasDevice device, nint monitor, int height)
    {
        _device = device;
        _height = height;
        var item = CreateItemForMonitor(monitor);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _session = _pool.CreateCaptureSession(item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        try { _session.IsBorderRequired = false; } catch { } // Windows 11 only
        _session.StartCapture();
    }

    /// <summary>Copies the newest frame, if any, into <see cref="Image"/>. Call on the UI thread.</summary>
    public bool TryUpdate()
    {
        Direct3D11CaptureFrame? latest = null;
        while (_pool.TryGetNextFrame() is { } frame) { latest?.Dispose(); latest = frame; }
        if (latest is null) return false;

        using (latest)
        using (var src = CanvasBitmap.CreateFromDirect3D11Surface(_device, latest.Surface, 96))
        {
            // Only the top band is ever sampled; copying the whole monitor every frame was most of the GPU load.
            uint w = src.SizeInPixels.Width, h = Math.Min(src.SizeInPixels.Height, (uint)_height);
            if (Image is null || Image.SizeInPixels.Width != w || Image.SizeInPixels.Height != h)
            {
                Image?.Dispose();
                Image = new CanvasRenderTarget(_device, w, h, 96);
            }
            Image.CopyPixelsFromBitmap(src, 0, 0, 0, 0, (int)w, (int)h);
        }
        return true;
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
        Image?.Dispose();
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, ref interopIid, out nint interopPtr));
        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            nint itemPtr = interop.CreateForMonitor(monitor, ref itemIid);
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally { Marshal.Release(interopPtr); }
    }
}
