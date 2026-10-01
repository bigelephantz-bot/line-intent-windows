using System.Text;
using LineIntent.Settings;

namespace LineIntent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test"))
            return Diagnostics.Run(args);
        if (args.Contains("--capture-diagnostics"))
            return Diagnostics.CaptureReport(args);
        var preflight = Array.IndexOf(args, "--ci-preflight");
        if (preflight >= 0)
            return preflight + 1 < args.Length ? UiTestPreflight.Run(args[preflight + 1]) : 2;

        Application.ThreadException += (_, _) => MessageBox.Show(
            "操作失敗，請重試。目前對話不會寫入日誌。", "LINE 意圖助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        AppSettings settings;
        try { settings = SettingsStore.Load(); }
        catch
        {
            MessageBox.Show("無法讀取設定或解密密鑰，請重新輸入。原設定檔會保留，直到你按下儲存。", "LINE 意圖助手");
            settings = new();
        }
        Application.Run(new MainForm(settings));
        return 0;
    }
}
