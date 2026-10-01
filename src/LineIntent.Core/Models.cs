namespace LineIntent.Core;

public enum JevProvider { OpenRouter, TypeSafe }

/// <summary>API credentials are transient. Never serialize this object to disk.</summary>
public sealed record JevSettings
{
    public JevProvider Provider { get; init; } = JevProvider.OpenRouter;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";

    public override string ToString() => $"JevSettings {{ Provider = {Provider}, ApiKey = [REDACTED] }}";
}

public sealed record ChoiceResult(string Code, string Label, double Confidence, double Probability);
public sealed record ScoreResult(double Value, double Confidence);

public sealed record AnalysisResult(
    ChoiceResult Intent,
    ChoiceResult Need,
    ChoiceResult Action,
    ScoreResult Tension,
    double LiteralProbability,
    double ReplySubstanceProbability,
    double ResolvedProbability,
    IReadOnlyList<string> Warnings);

/// <summary>Contains a safe UI message only, never service response text or request details.</summary>
public sealed class JevException(string message) : Exception(message);
