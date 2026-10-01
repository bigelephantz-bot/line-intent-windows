using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using IMemoryBufferReference = Windows.Foundation.IMemoryBufferReference;
using Windows.Graphics.Imaging;

namespace LineIntent.Capture;

/// <summary>Stride-aware pixel copies; no PNG encoding or full-size managed pixel array.</summary>
internal static class BitmapPixels
{
    internal static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        var software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Ignore);
        try
        {
            using var buffer = software.LockBuffer(BitmapBufferAccessMode.Write);
            using var reference = buffer.CreateReference();
            var plane = buffer.GetPlaneDescription(0);
            var pointer = GetPointer(reference, out var capacity);
            ValidatePlane(plane, capacity, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { CopyRows(data.Scan0, data.Stride, pointer + plane.StartIndex, plane.Stride, bitmap.Width, bitmap.Height); }
            finally { bitmap.UnlockBits(data); }
            return software;
        }
        catch { software.Dispose(); throw; }
    }

    internal static Bitmap ToBitmap(SoftwareBitmap software)
    {
        if (software.BitmapPixelFormat != BitmapPixelFormat.Bgra8) throw new InvalidOperationException("影像像素格式不是 BGRA8。");
        var bitmap = new Bitmap(software.PixelWidth, software.PixelHeight, PixelFormat.Format32bppRgb);
        try
        {
            using var buffer = software.LockBuffer(BitmapBufferAccessMode.Read);
            using var reference = buffer.CreateReference();
            var plane = buffer.GetPlaneDescription(0);
            var pointer = GetPointer(reference, out var capacity);
            ValidatePlane(plane, capacity, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { CopyRows(pointer + plane.StartIndex, plane.Stride, data.Scan0, data.Stride, bitmap.Width, bitmap.Height); }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void ValidatePlane(BitmapPlaneDescription plane, uint capacity, int width, int height)
    {
        if (width < 1 || height < 1 || plane.Width != width || plane.Height != height || plane.StartIndex < 0
            || plane.Stride < checked(width * 4) || (long)plane.StartIndex + (long)(height - 1) * plane.Stride + width * 4L > capacity)
            throw new InvalidOperationException("影像像素緩衝區範圍無效。");
    }

    private static unsafe void CopyRows(nint source, int sourceStride, nint target, int targetStride, int width, int height)
    {
        var bytes = checked(width * 4);
        if (Math.Abs((long)sourceStride) < bytes || Math.Abs((long)targetStride) < bytes)
            throw new InvalidOperationException("影像列寬無效。");
        for (var y = 0; y < height; y++)
            new ReadOnlySpan<byte>((void*)(source + (nint)((long)y * sourceStride)), bytes)
                .CopyTo(new Span<byte>((void*)(target + (nint)((long)y * targetStride)), bytes));
    }

    private static nint GetPointer(IMemoryBufferReference reference, out uint capacity)
    {
        var unknown = WinRT.MarshalInterface<IMemoryBufferReference>.FromManaged(reference);
        nint access = 0;
        try
        {
            var id = new Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in id, out access));
            var method = Marshal.GetDelegateForFunctionPointer<GetBuffer>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(access), 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(method(access, out var pointer, out capacity));
            return pointer; // Caller owns the live reference/lock until copying completes.
        }
        finally { if (access != 0) Marshal.Release(access); Marshal.Release(unknown); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBuffer(nint self, out nint pointer, out uint capacity);
}
