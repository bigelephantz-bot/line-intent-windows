using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LineIntent.Core;

namespace LineIntent.Settings;

public static class SettingsStore
{
    private const uint CryptProtectUiForbidden = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LineIntent", "settings.json");

    public static AppSettings Load() => Load(SettingsPath);

    internal static AppSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return new AppSettings();

        try
        {
            var saved = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("設定檔內容為空。");
            if (saved.Version != 1 || !Enum.IsDefined(saved.Provider))
                throw new InvalidDataException("設定檔版本或 API 服務不受支援。");

            return new AppSettings
            {
                Provider = saved.Provider,
                Model = saved.Model ?? "",
                OpenRouterKey = Unprotect(saved.EncryptedOpenRouterKey),
                TypeSafeKey = Unprotect(saved.EncryptedTypeSafeKey),
                EnableReplySuggestions = saved.EnableReplySuggestions,
                LlmModel = saved.LlmModel ?? "",
                LlmApiKey = Unprotect(saved.EncryptedLlmApiKey),
                Relationship = string.IsNullOrWhiteSpace(saved.Relationship) ? "一般朋友" : saved.Relationship,
                OcrLanguage = saved.OcrLanguage ?? ""
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            throw new InvalidDataException(
                "無法讀取設定或解密 API Key。密鑰只能由儲存它的 Windows 使用者解密；請重新開啟設定輸入密鑰。", ex);
        }
    }

    public static void Save(AppSettings settings) => Save(settings, SettingsPath);

    internal static void Save(AppSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(settings.Provider))
            throw new ArgumentException("不支援的 API 服務。", nameof(settings));

        var saved = new StoredSettings
        {
            Provider = settings.Provider,
            Model = settings.Model.Trim(),
            EncryptedOpenRouterKey = Protect(settings.OpenRouterKey.Trim()),
            EncryptedTypeSafeKey = Protect(settings.TypeSafeKey.Trim()),
            EnableReplySuggestions = settings.EnableReplySuggestions,
            LlmModel = settings.LlmModel.Trim(),
            EncryptedLlmApiKey = Protect(settings.LlmApiKey.Trim()),
            Relationship = settings.Relationship.Trim(),
            OcrLanguage = settings.OcrLanguage.Trim()
        };
        var destinationPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(saved, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string Protect(string value)
    {
        if (value.Length == 0)
            return "";

        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(Transform(bytes, protect: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var bytes = Transform(Convert.FromBase64String(value), protect: false);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var outputBlob = new DataBlob();
        IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);
            var success = protect
                ? CryptProtectData(ref inputBlob, "LineIntent API Key", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, out description, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
            if (!success)
                throw new CryptographicException("Windows 無法保護或解密 API Key。",
                    new Win32Exception(Marshal.GetLastWin32Error()));

            var result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroUnmanaged(inputBlob.Data, inputBlob.Length);
            Marshal.FreeHGlobal(inputBlob.Data);
            if (outputBlob.Data != IntPtr.Zero)
            {
                ZeroUnmanaged(outputBlob.Data, outputBlob.Length);
                LocalFree(outputBlob.Data);
            }
            if (description != IntPtr.Zero)
                LocalFree(description);
        }
    }

    private static void ZeroUnmanaged(IntPtr pointer, int length)
    {
        // Keep the native plaintext buffers short lived without requiring unsafe code.
        for (var i = 0; i < length; i++)
            Marshal.WriteByte(pointer, i, 0);
    }

    private sealed class StoredSettings
    {
        public int Version { get; set; } = 1;
        public JevProvider Provider { get; set; }
        public string? Model { get; set; }
        public string? EncryptedOpenRouterKey { get; set; }
        public string? EncryptedTypeSafeKey { get; set; }
        public bool EnableReplySuggestions { get; set; }
        public string? LlmModel { get; set; }
        public string? EncryptedLlmApiKey { get; set; }
        public string? Relationship { get; set; }
        public string? OcrLanguage { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
