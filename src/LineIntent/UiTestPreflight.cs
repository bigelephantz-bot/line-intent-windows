using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LineIntent.Capture;

namespace LineIntent;

internal static class UiTestPreflight
{
    internal static int Run(string path)
    {
        var failures = new List<string>();
        var languages = Array.Empty<string>();
        try { languages = WindowsOcr.AvailableLanguages.ToArray(); }
        catch { failures.Add("Windows OCR is unavailable."); }
        if (!languages.Any(tag => tag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || tag.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)))
            failures.Add("Traditional Chinese OCR is required. Install Language.OCR~~~zh-TW~0.0.1.0 on the test machine.");
        using var process = Process.GetCurrentProcess();
        if (!Environment.UserInteractive || process.SessionId == 0)
            failures.Add("Run the CI agent in a logged-in desktop session, not as a Windows service.");
        var input = OpenInputDesktop(0, false, 1);
        var desktopAvailable = false;
        try
        {
            desktopAvailable = input != 0 && DesktopName(input) == "Default"
                && DesktopName(GetThreadDesktop(GetCurrentThreadId())) == "Default";
        }
        finally { if (input != 0) CloseDesktop(input); }
        if (!desktopAvailable) failures.Add("An unlocked interactive Default desktop is required.");
        using var fonts = new InstalledFontCollection();
        if (!fonts.Families.Any(font => font.GetName(0x0409).Equals("Microsoft JhengHei", StringComparison.OrdinalIgnoreCase)))
            failures.Add("Microsoft JhengHei font is required.");
        var result = new { ok = failures.Count == 0, windowsBuild = Environment.OSVersion.Version.ToString(),
            runtime = Environment.Version.ToString(), interactive = Environment.UserInteractive, desktopAvailable, ocrLanguages = languages, failures };
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 1;
    }

    private static string DesktopName(nint desktop)
    {
        if (desktop == 0) return "";
        var name = new StringBuilder(256);
        return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ? name.ToString() : "";
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int size, out int needed);
}
