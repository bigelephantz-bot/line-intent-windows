using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace LineIntent.Capture;

public sealed record LineWindow(nint Handle, int ProcessId, string Title)
{
    // HWNDs and process IDs can be reused after a process exits.
    internal long ProcessStartTicks { get; init; }

    public override string ToString() => $"{Title}  (LINE · PID {ProcessId})";
}

/// <summary>Captures only an explicitly selected LINE desktop window, preferring HWND-targeted capture.</summary>
public static class LineCapture
{
    /// <summary>
    /// Reports only window-system metadata. Does not read titles, capture pixels, change focus,
    /// perform OCR, access credentials, or persist a report.
    /// </summary>
    public static IReadOnlyList<string> GetDiagnostics(LineWindow? window = null)
    {
        var report = new List<string>();
        try { report.Add($"capture.method={(WindowGraphicsCapture.IsSupported ? "WindowsGraphicsCapture-HWND" : "visible-client-SourceCopy")}"); }
        catch { report.Add("capture.method=support-check-failed"); }
        var previousContext = Native.SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            using (var current = Process.GetCurrentProcess())
                report.Add($"host.session_id={current.SessionId} interactive={Environment.UserInteractive}");
            var lineProcessIds = new HashSet<int>();
            foreach (var process in Process.GetProcessesByName("LINE"))
            {
                using (process)
                {
                    lineProcessIds.Add(process.Id);
                    try { report.Add($"line.process pid={process.Id} session_id={process.SessionId}"); }
                    catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                    { report.Add($"line.process pid={process.Id} session_id=unavailable"); }
                }
            }
            report.Add($"line.process_count={lineProcessIds.Count}");
            var lineWindowCount = 0;
            Native.EnumWindows((handle, _) =>
            {
                Native.GetWindowThreadProcessId(handle, out var processId);
                if (lineProcessIds.Contains((int)processId))
                {
                    lineWindowCount++;
                    report.Add("line.window " + DescribeWindow(handle));
                }
                return true;
            }, 0);
            report.Add($"line.top_level_window_count={lineWindowCount}");
            if (window is null)
                return report;

            report.Add("target " + DescribeWindow(window.Handle));
            try { ValidateIdentity(window); report.Add("stage.identity=ok"); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                report.Add("stage.identity=failed:" + error.GetType().Name);
                return report;
            }
            Rectangle bounds;
            try
            {
                bounds = GetCaptureBounds(window);
                report.Add($"stage.bounds=ok client={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                report.Add("stage.bounds=failed:" + error.GetType().Name);
                return report;
            }
            var foreground = Native.GetForegroundWindow();
            report.Add($"stage.foreground={(foreground == window.Handle ? "ok" : "different")} hwnd=0x{foreground.ToInt64():X}");
            var visited = new HashSet<nint>();
            var overlaps = 0;
            for (var above = Native.GetWindow(window.Handle, 3); above != 0; above = Native.GetWindow(above, 3))
            {
                if (!visited.Add(above)) { report.Add("stage.overlap=z-order-changed"); break; }
                if (!Native.IsWindowVisible(above) || Native.IsIconic(above) || IsCloaked(above)
                    || !TryGetVisibleBounds(above, out var other) || !bounds.IntersectsWith(other))
                    continue;
                overlaps++;
                report.Add("overlap " + DescribeWindow(above));
            }
            report.Add($"stage.overlap.count={overlaps}");
            return report;
        }
        finally
        {
            if (previousContext != 0)
                Native.SetThreadDpiAwarenessContext(previousContext);
        }
    }

    public static IReadOnlyList<LineWindow> ListWindows()
    {
        var windows = new List<LineWindow>();
        Native.EnumWindows((handle, _) =>
        {
            if (!Native.IsWindowVisible(handle) || IsCloaked(handle))
                return true;

            Native.GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                if (!string.Equals(process.ProcessName, "LINE", StringComparison.OrdinalIgnoreCase))
                    return true;

                var length = Native.GetWindowTextLength(handle);
                if (length <= 0)
                    return true;
                var title = new StringBuilder(length + 1);
                Native.GetWindowText(handle, title, title.Capacity);
                windows.Add(new LineWindow(handle, process.Id, title.ToString())
                {
                    ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // A window may close while EnumWindows is running, or belong to an elevated process.
            }

            return true;
        }, 0);

        return windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static async Task<Bitmap> CaptureAsync(LineWindow window, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ct.ThrowIfCancellationRequested();
        ValidateIdentity(window);

        if (Native.IsIconic(window.Handle))
            Native.ShowWindow(window.Handle, 9); // SW_RESTORE; only on explicit capture.

        Native.SetForegroundWindow(window.Handle);
        await Task.Delay(250, ct);

        ct.ThrowIfCancellationRequested();
        ValidateIdentity(window);
        if (WindowGraphicsCapture.IsSupported)
        {
            if (!Native.IsWindowVisible(window.Handle) || Native.IsIconic(window.Handle) || IsCloaked(window.Handle))
                throw new InvalidOperationException("LINE 聊天視窗尚未顯示。請從工作列開啟 LINE 後重試。");
            var captured = await WindowGraphicsCapture.CaptureAsync(window.Handle, ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                ValidateIdentity(window);
                if (LooksBlank(captured))
                    throw new InvalidOperationException("LINE 視窗擷取為空白。請開啟聊天內容後重試。");
                return captured;
            }
            catch { captured.Dispose(); throw; }
        }

        // Older/unsupported systems retain the strict foreground + no-overlap desktop fallback.
        // Keep DPI context changes synchronous; never leave them active across await/thread hops.
        var previousContext = Native.SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var bounds = GetCaptureBounds(window);
            ValidateVisibleWindow(window, bounds);

            // Desktop pixels are opaque; an RGB target avoids undefined GDI alpha during OCR compositing.
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    // Graphics.CopyFromScreen rejects OR-ed CopyPixelOperation values.
                    // SourceCopy suffices because visible overlays are rejected before/after capture.
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                        CopyPixelOperation.SourceCopy);
                }

                ct.ThrowIfCancellationRequested();
                ValidateIdentity(window);
                if (GetCaptureBounds(window) != bounds)
                    throw new InvalidOperationException("擷取時 LINE 視窗移動或改變大小，請保持視窗不動後重試。");
                ValidateVisibleWindow(window, bounds);
                if (LooksBlank(bitmap))
                    throw new InvalidOperationException("LINE 畫面擷取為空白或單色。請開啟聊天內容、確認 LINE 位於前景後重試。");

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        finally
        {
            if (previousContext != 0)
                Native.SetThreadDpiAwarenessContext(previousContext);
        }
    }

    private static void ValidateIdentity(LineWindow window)
    {
        if (window.Handle == 0 || !Native.IsWindow(window.Handle))
            throw new InvalidOperationException("選取的 LINE 視窗已關閉，請重新整理視窗清單。");

        Native.GetWindowThreadProcessId(window.Handle, out var processId);
        if (processId != window.ProcessId || window.ProcessStartTicks == 0)
            throw new InvalidOperationException("LINE 視窗識別已變更，請重新整理並選取視窗。");

        try
        {
            using var process = Process.GetProcessById(window.ProcessId);
            if (!string.Equals(process.ProcessName, "LINE", StringComparison.OrdinalIgnoreCase)
                || process.StartTime.ToUniversalTime().Ticks != window.ProcessStartTicks)
                throw new InvalidOperationException("選取的視窗已不屬於原本的 LINE 程序，請重新整理視窗清單。");
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception)
        {
            throw new InvalidOperationException("無法驗證 LINE 程序。請以一般權限啟動 LINE，並重新整理視窗清單。", exception);
        }
    }

    private static Rectangle GetCaptureBounds(LineWindow window)
    {
        if (!Native.GetClientRect(window.Handle, out var client))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "無法取得 LINE 視窗大小。");
        var origin = new Native.Point { X = client.Left, Y = client.Top };
        if (!Native.ClientToScreen(window.Handle, ref origin))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "無法取得 LINE 視窗位置。");
        var bounds = new Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        if (bounds.Width < 80 || bounds.Height < 80 || bounds.Width > 16384 || bounds.Height > 16384
            || (long)bounds.Width * bounds.Height > 64_000_000)
            throw new InvalidOperationException("LINE 視窗大小不適合擷取，請調整為一般桌面視窗大小。");

        // Use the monitor union rather than its bounding box: gaps and negative coordinates matter.
        using var uncovered = new Region(bounds);
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Native.Rect rectangle, nint data) =>
        {
            uncovered.Exclude(rectangle.ToRectangle());
            return true;
        }, 0);
        using var matrix = new Matrix();
        if (uncovered.GetRegionScans(matrix).Length > 0)
            throw new InvalidOperationException("LINE 視窗有部分位於螢幕外，請將完整視窗移入螢幕後重試。");
        return bounds;
    }

    private static void ValidateVisibleWindow(LineWindow window, Rectangle bounds)
    {
        if (!Native.IsWindowVisible(window.Handle) || Native.IsIconic(window.Handle) || IsCloaked(window.Handle)
            || Native.GetForegroundWindow() != window.Handle)
            throw new InvalidOperationException("LINE 未保持在前景。請關閉彈出視窗，重新選取 LINE 視窗後擷取。");

        // CopyFromScreen is used because GPU-rendered LINE windows can return black with PrintWindow.
        // Reject other top-level windows above LINE, including always-on-top overlays.
        var visited = new HashSet<nint>();
        for (var above = Native.GetWindow(window.Handle, 3); above != 0; above = Native.GetWindow(above, 3))
        {
            if (!visited.Add(above))
                throw new InvalidOperationException("視窗排列正在變動，請保持 LINE 在前景後重試。");
            if (!Native.IsWindowVisible(above) || Native.IsIconic(above) || IsCloaked(above))
                continue;
            if (TryGetVisibleBounds(above, out var other) && bounds.IntersectsWith(other))
                throw new InvalidOperationException("LINE 畫面被其他視窗或浮動工具列遮住。請移開遮擋內容後重試。");
        }
    }

    private static bool TryGetVisibleBounds(nint handle, out Rectangle bounds)
    {
        // GetWindowRect includes invisible resize borders, which can overlap adjacent windows.
        // DWM's visible bounds are physical pixels, matching our PerMonitorV2 capture coordinates.
        if (Native.DwmGetWindowAttributeRect(handle, 9, out var frame, Marshal.SizeOf<Native.Rect>()) == 0
            && frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            bounds = frame.ToRectangle();
            return true;
        }
        if (Native.GetWindowRect(handle, out frame))
        {
            bounds = frame.ToRectangle();
            return true;
        }
        bounds = Rectangle.Empty;
        return false;
    }

    private static bool IsCloaked(nint handle) =>
        Native.DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string DescribeWindow(nint handle)
    {
        Native.GetWindowThreadProcessId(handle, out var processId);
        var className = new StringBuilder(256);
        Native.GetClassName(handle, className, className.Capacity);
        var extendedStyle = IntPtr.Size == 8
            ? Native.GetWindowLongPtr(handle, -20).ToInt64()
            : Native.GetWindowLong(handle, -20);
        TryGetVisibleBounds(handle, out var frame);
        Native.GetClientRect(handle, out var client);
        var opacity = "unknown";
        if (Native.GetLayeredWindowAttributes(handle, out _, out var alpha, out var flags))
            opacity = $"alpha:{alpha},flags:0x{flags:X}";
        var regionKind = -1;
        var region = Native.CreateRectRgn(0, 0, 0, 0);
        if (region != 0)
        {
            try { regionKind = Native.GetWindowRgn(handle, region); }
            finally { Native.DeleteObject(region); }
        }
        // Class names are native implementation metadata; window titles are deliberately never read.
        return $"hwnd=0x{handle.ToInt64():X} pid={processId} class={className} " +
            $"visible={Native.IsWindowVisible(handle)} minimized={Native.IsIconic(handle)} cloaked={IsCloaked(handle)} " +
            $"frame={frame.X},{frame.Y},{frame.Width},{frame.Height} client={client.Right - client.Left},{client.Bottom - client.Top} " +
            $"exstyle=0x{extendedStyle:X} opacity={opacity} region={regionKind}";
    }

    private static bool LooksBlank(Bitmap bitmap)
    {
        var first = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 40))
        for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 40))
        {
            var pixel = bitmap.GetPixel(x, y);
            if (Math.Abs(pixel.R - first.R) + Math.Abs(pixel.G - first.G) + Math.Abs(pixel.B - first.B) > 24)
                return false;
        }
        return true;
    }

    private static class Native
    {
        internal delegate bool EnumWindowsCallback(nint handle, nint parameter);
        internal delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rect, nint data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X, Y; }

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint handle, StringBuilder text, int maxCount);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint handle, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint handle, int index);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetLayeredWindowAttributes(nint handle, out uint colorKey, out byte alpha, out uint flags);
        [DllImport("user32.dll")] internal static extern int GetWindowRgn(nint handle, nint region);
        [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint handle, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint handle);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint handle, uint command);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint handle, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint handle, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint handle, ref Point point);
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int valueSize);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int DwmGetWindowAttributeRect(nint handle, int attribute, out Rect value, int valueSize);
    }
}
