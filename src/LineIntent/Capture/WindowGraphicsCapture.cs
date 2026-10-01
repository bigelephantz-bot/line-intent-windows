using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace LineIntent.Capture;

/// <summary>A single HWND-targeted Windows Graphics Capture snapshot, held entirely in memory.</summary>
internal static class WindowGraphicsCapture
{
    internal static bool IsSupported => GraphicsCaptureSession.IsSupported();

    internal static async Task<Bitmap> CaptureAsync(nint handle, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var token = timeout.Token;
        var stage = "建立視窗擷取項目";
        try
        {
            token.ThrowIfCancellationRequested();
            var item = CreateItem(handle);
            var size = item.Size;
            if (size.Width < 1 || size.Height < 1 || size.Width > 16384 || size.Height > 16384
                || (long)size.Width * size.Height > 64_000_000)
                throw new InvalidOperationException("LINE 視窗大小不適合擷取，請還原為一般桌面視窗後重試。");

            stage = "建立 Direct3D 裝置";
            using var device = CreateDevice();
            stage = "啟動 Windows 視窗擷取";
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();

            stage = "等候 LINE 畫面";
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var frame = pool.TryGetNextFrame();
                if (frame is null)
                {
                    await Task.Delay(35, token).ConfigureAwait(false);
                    continue;
                }
                if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                    throw new InvalidOperationException("擷取時 LINE 視窗大小改變，請保持視窗大小不變後重試。");

                stage = "讀取 LINE 視窗畫面";
                using var surface = frame.Surface;
                using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                    surface, BitmapAlphaMode.Ignore).AsTask(token).ConfigureAwait(false);
                return BitmapPixels.ToBitmap(softwareBitmap);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{stage}逾時（6 秒）。請確認 LINE 已開啟聊天視窗後重試。");
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{stage}失敗（Windows 0x{error.HResult:X8}）。請重新開啟 LINE 視窗後重試。", error);
        }
    }

    private static GraphicsCaptureItem CreateItem(nint handle)
    {
        const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var interopId = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        nint className = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out className));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref interopId, out factory));
            // IGraphicsCaptureItemInterop derives directly from IUnknown: CreateForWindow is slot 3.
            var vtable = Marshal.ReadIntPtr(factory);
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(
                Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(create(factory, handle, ref itemId, out item));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (className != 0) WindowsDeleteString(className);
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        nint native = 0, context = 0, dxgi = 0, projected = 0;
        try
        {
            var result = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out native, out _, out context);
            if (result < 0)
            {
                if (context != 0) { Marshal.Release(context); context = 0; }
                if (native != 0) { Marshal.Release(native); native = 0; }
                // WARP is the Windows software adapter, for machines without a usable hardware adapter.
                result = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out native, out _, out context);
            }
            Marshal.ThrowExceptionForHR(result);
            var dxgiId = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in dxgiId, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out projected));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(projected);
        }
        finally
        {
            if (projected != 0) Marshal.Release(projected);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (native != 0) Marshal.Release(native);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindow(nint self, nint handle, ref Guid iid, out nint item);
    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string text, int length, out nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
