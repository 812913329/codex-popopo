namespace CodexProfileLauncher.Core.Models;

public sealed record ProfileAiSettings
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultBaseUrl = "https://ai98pro.xyz/";
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public bool ApiEnabled { get; init; }
    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string ApiKey { get; init; } = string.Empty;
    public string SelectedModel { get; init; } = string.Empty;
    public string ModelReasoningEffort { get; init; } = "medium";
    public bool SystemPromptEnabled { get; init; } = true;
    public string SystemPrompt { get; init; } = string.Empty;
    /// <summary>
    /// When true, bootstrap keysmith-equivalent model_instructions + hooks isolation on save/launch.
    /// Default true: unrestricted instructions are the product default for every environment.
    /// </summary>
    public bool KeysmithModeEnabled { get; init; } = true;
    public string RevisionToken { get; init; } = string.Empty;
}

public sealed record ProfileAiLaunchConfiguration(bool ApiEnabled, string ApiKey)
{
    public const string ApiKeyEnvironmentVariable = "CODEX_PROFILE_LAUNCHER_API_KEY";
    public static ProfileAiLaunchConfiguration Disabled { get; } = new(false, string.Empty);
}

public sealed record AiConnectionTestResult(
    Uri RequestUri,
    bool IsSuccess,
    int? HttpStatusCode,
    TimeSpan Elapsed,
    int? ModelCount,
    IReadOnlyList<string> ModelIds,
    string Details)
{
    public static IReadOnlyList<string> EmptyModelIds { get; } = Array.Empty<string>();
}

public sealed class ProfileAiSettingsException : Exception
{
    public ProfileAiSettingsException(string code, string message, string details, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }
    public string Details { get; }
}
