using System.Text.Json.Serialization;
using LineIntent.Core;

namespace LineIntent.Settings;

public sealed record AppSettings
{
    public JevProvider Provider { get; init; } = JevProvider.OpenRouter;
    public string Model { get; init; } = "";
    [JsonIgnore]
    public string OpenRouterKey { get; init; } = "";
    [JsonIgnore]
    public string TypeSafeKey { get; init; } = "";
    public bool EnableReplySuggestions { get; init; }
    public string LlmModel { get; init; } = "";
    [JsonIgnore]
    public string LlmApiKey { get; init; } = "";
    public string Relationship { get; init; } = "一般朋友";
    public string OcrLanguage { get; init; } = "";

    [JsonIgnore]
    public string ApiKey => Provider == JevProvider.OpenRouter ? OpenRouterKey : TypeSafeKey;

    [JsonIgnore]
    public string EffectiveLlmApiKey => string.IsNullOrWhiteSpace(LlmApiKey) ? OpenRouterKey : LlmApiKey;

    public override string ToString() => $"AppSettings {{ Provider = {Provider}, API keys = [redacted] }}";
}
