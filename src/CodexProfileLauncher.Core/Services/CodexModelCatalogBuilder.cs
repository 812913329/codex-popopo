using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexProfileLauncher.Core.Services;

public static class CodexModelCatalogBuilder
{
    public const int MaxModels = 80;
    public const int DefaultContextWindow = 200_000;
    public const string DefaultReasoningEffort = "medium";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly string[] AllowedReasoningEfforts =
    [
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh",
        "max",
        "ultra",
    ];

    public static string NormalizeReasoningEffort(string? effort)
    {
        var value = (effort ?? string.Empty).Trim().ToLowerInvariant();
        // Accept common shorthand seen in UI talk ("ult" → official "ultra").
        if (value is "ult")
        {
            value = "ultra";
        }

        return AllowedReasoningEfforts.Contains(value, StringComparer.Ordinal)
            ? value
            : DefaultReasoningEffort;
    }

    public static string BuildJson(IEnumerable<string> modelIds, string? preferredModel = null)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        var ordered = SelectModels(modelIds, preferredModel);
        if (ordered.Count == 0)
        {
            throw new ArgumentException("模型列表不能为空。", nameof(modelIds));
        }

        var models = new List<CatalogModel>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            models.Add(CreateEntry(ordered[index], ordered[index], index));
        }

        return JsonSerializer.Serialize(new CatalogRoot(models), SerializerOptions) + Environment.NewLine;
    }

    public static IReadOnlyList<string> SelectModels(IEnumerable<string> modelIds, string? preferredModel)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var raw in modelIds)
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                continue;
            }

            ordered.Add(id);
        }

        if (ordered.Count <= MaxModels)
        {
            return ordered;
        }

        var preferred = preferredModel?.Trim();
        if (!string.IsNullOrEmpty(preferred) &&
            ordered.Contains(preferred, StringComparer.Ordinal))
        {
            var limited = new List<string>(MaxModels) { preferred };
            foreach (var id in ordered)
            {
                if (limited.Count >= MaxModels)
                {
                    break;
                }

                if (!id.Equals(preferred, StringComparison.Ordinal))
                {
                    limited.Add(id);
                }
            }

            return limited;
        }

        return ordered.Take(MaxModels).ToArray();
    }

    public static string ToProfileFileStem(string modelId)
    {
        var stem = Regex.Replace(modelId.Trim(), @"[^A-Za-z0-9_-]+", "-");
        stem = Regex.Replace(stem, "-{2,}", "-").Trim('-');
        if (string.IsNullOrEmpty(stem))
        {
            stem = "model";
        }

        if (stem.Length > 48)
        {
            stem = stem[..48].TrimEnd('-');
        }

        return "pl-" + stem.ToLowerInvariant();
    }

    private static CatalogModel CreateEntry(string slug, string displayName, int priority)
    {
        // Full effort ladder for Codex UI. Do not clip levels — Desktop uses
        // supported_reasoning_levels to build the 推理强度 menu.
        // Match official Codex models.json ladder (gpt-5.6-sol etc.):
        // minimal/low/medium/high/xhigh/max/ultra
        ReasoningLevel[] levels =
        [
            new ReasoningLevel("minimal", "Minimal reasoning, fastest responses"),
            new ReasoningLevel("low", "Fast responses with lighter reasoning"),
            new ReasoningLevel("medium", "Balances speed and reasoning depth for everyday tasks"),
            new ReasoningLevel("high", "Greater reasoning depth for complex problems"),
            new ReasoningLevel("xhigh", "Extra high reasoning depth for complex problems"),
            new ReasoningLevel("max", "Maximum reasoning depth for the hardest problems"),
            new ReasoningLevel("ultra", "Maximum reasoning with automatic task delegation"),
        ];

        return new(
            Slug: slug,
            DisplayName: displayName,
            Description: $"{displayName} via Profile Launcher API",
            DefaultReasoningLevel: DefaultReasoningEffort,
            SupportedReasoningLevels: levels,
            ShellType: "shell_command",
            Visibility: "list",
            SupportedInApi: true,
            Priority: priority,
            AdditionalSpeedTiers: [],
            ServiceTiers: [],
            AvailabilityNux: null,
            Upgrade: null,
            BaseInstructions: "You are Codex, a coding agent.",
            SupportsReasoningSummaries: true,
            DefaultReasoningSummary: "auto",
            SupportVerbosity: false,
            DefaultVerbosity: null,
            ApplyPatchToolType: null,
            WebSearchToolType: "text",
            TruncationPolicy: new("tokens", 10_000),
            SupportsParallelToolCalls: true,
            SupportsImageDetailOriginal: false,
            ContextWindow: DefaultContextWindow,
            MaxContextWindow: DefaultContextWindow,
            EffectiveContextWindowPercent: 95,
            ExperimentalSupportedTools: [],
            InputModalities: ["text", "image"],
            SupportsSearchTool: false);
    }

    private sealed record CatalogRoot(
        [property: JsonPropertyName("models")] IReadOnlyList<CatalogModel> Models);

    private sealed record CatalogModel(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("default_reasoning_level")] string DefaultReasoningLevel,
        [property: JsonPropertyName("supported_reasoning_levels")] IReadOnlyList<ReasoningLevel> SupportedReasoningLevels,
        [property: JsonPropertyName("shell_type")] string ShellType,
        [property: JsonPropertyName("visibility")] string Visibility,
        [property: JsonPropertyName("supported_in_api")] bool SupportedInApi,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("additional_speed_tiers")] IReadOnlyList<string> AdditionalSpeedTiers,
        [property: JsonPropertyName("service_tiers")] IReadOnlyList<string> ServiceTiers,
        [property: JsonPropertyName("availability_nux")] object? AvailabilityNux,
        [property: JsonPropertyName("upgrade")] object? Upgrade,
        [property: JsonPropertyName("base_instructions")] string BaseInstructions,
        [property: JsonPropertyName("supports_reasoning_summaries")] bool SupportsReasoningSummaries,
        [property: JsonPropertyName("default_reasoning_summary")] string DefaultReasoningSummary,
        [property: JsonPropertyName("support_verbosity")] bool SupportVerbosity,
        [property: JsonPropertyName("default_verbosity")] object? DefaultVerbosity,
        [property: JsonPropertyName("apply_patch_tool_type")] object? ApplyPatchToolType,
        [property: JsonPropertyName("web_search_tool_type")] string WebSearchToolType,
        [property: JsonPropertyName("truncation_policy")] TruncationPolicy TruncationPolicy,
        [property: JsonPropertyName("supports_parallel_tool_calls")] bool SupportsParallelToolCalls,
        [property: JsonPropertyName("supports_image_detail_original")] bool SupportsImageDetailOriginal,
        [property: JsonPropertyName("context_window")] int ContextWindow,
        [property: JsonPropertyName("max_context_window")] int MaxContextWindow,
        [property: JsonPropertyName("effective_context_window_percent")] int EffectiveContextWindowPercent,
        [property: JsonPropertyName("experimental_supported_tools")] IReadOnlyList<string> ExperimentalSupportedTools,
        [property: JsonPropertyName("input_modalities")] IReadOnlyList<string> InputModalities,
        [property: JsonPropertyName("supports_search_tool")] bool SupportsSearchTool);

    private sealed record ReasoningLevel(
        [property: JsonPropertyName("effort")] string Effort,
        [property: JsonPropertyName("description")] string Description);

    private sealed record TruncationPolicy(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("limit")] int Limit);
}
