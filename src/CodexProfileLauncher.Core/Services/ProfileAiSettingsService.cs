using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexProfileLauncher.Core.Configuration;
using CodexProfileLauncher.Core.Models;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;

namespace CodexProfileLauncher.Core.Services;

public interface IProfileAiSettingsService
{
    Task<ProfileAiSettings> LoadAsync(ProfilePaths paths, CancellationToken cancellationToken = default);

    Task<ProfileAiSettings> ReloadAsync(ProfilePaths paths, CancellationToken cancellationToken = default);

    Task<ProfileAiSettings> SaveAsync(
        ProfilePaths paths,
        ProfileAiSettings settings,
        CancellationToken cancellationToken = default);

    Task<AiConnectionTestResult> TestConnectionAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<AiConnectionTestResult> FetchModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<ProfileAiLaunchConfiguration> ResolveLaunchConfigurationAsync(
        ProfilePaths paths,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileAiSettingsService : IProfileAiSettingsService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly HttpClient _httpClient;

    public ProfileAiSettingsService(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? SharedHttpClient;

    public Task<ProfileAiSettings> ReloadAsync(
        ProfilePaths paths,
        CancellationToken cancellationToken = default) =>
        LoadAsync(paths, cancellationToken);

    public async Task<ProfileAiSettings> LoadAsync(
        ProfilePaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await using var operationLock = await AcquireLockAsync(paths, cancellationToken).ConfigureAwait(false);
        return await LoadUnlockedAsync(paths, initializeMissing: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfileAiSettings> SaveAsync(
        ProfilePaths paths,
        ProfileAiSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        await using var operationLock = await AcquireLockAsync(paths, cancellationToken).ConfigureAwait(false);
        var current = await LoadUnlockedAsync(paths, initializeMissing: true, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(settings.RevisionToken) &&
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(settings.RevisionToken),
                Encoding.UTF8.GetBytes(current.RevisionToken)))
        {
            throw new ProfileAiSettingsException(
                "AI_SETTINGS_CONFLICT",
                "AI 配置已被外部修改。",
                "磁盘内容与当前编辑所基于的版本不同；请重新加载或明确保留当前输入后再保存。");
        }

        var effectiveSettings = settings with
        {
            ModelReasoningEffort = CodexModelCatalogBuilder.NormalizeReasoningEffort(settings.ModelReasoningEffort),
        };
        effectiveSettings = ApplyDefaultInstructionContent(effectiveSettings);

        string? catalogJson = null;
        IReadOnlyList<string>? liveModelIds = null;
        if (settings.ApiEnabled)
        {
            var models = await FetchModelsUnlockedAsync(settings.BaseUrl, settings.ApiKey, cancellationToken)
                .ConfigureAwait(false);
            effectiveSettings = ApplyLiveModels(effectiveSettings, models);
            // Official third-party path: real model IDs in model_catalog_json + custom provider.
            catalogJson = CodexModelCatalogBuilder.BuildJson(models.ModelIds, effectiveSettings.SelectedModel);
            liveModelIds = models.ModelIds;
        }

        var oldSettings = await ReadOptionalTextAsync(paths.AiSettingsFile, cancellationToken).ConfigureAwait(false);
        var oldPrompt = await ReadOptionalTextAsync(paths.SystemPromptFile, cancellationToken).ConfigureAwait(false);
        var oldManaged = await ReadOptionalTextAsync(paths.ManagedConfigFile, cancellationToken).ConfigureAwait(false);
        var oldCatalog = await ReadOptionalTextAsync(paths.ModelCatalogFile, cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteSystemPromptFilesAsync(paths, effectiveSettings, cancellationToken)
                .ConfigureAwait(false);
            await AtomicFile.WriteTextAsync(paths.AiSettingsFile, Serialize(effectiveSettings), cancellationToken)
                .ConfigureAwait(false);
            if (catalogJson is not null)
            {
                await AtomicFile.WriteTextAsync(paths.ModelCatalogFile, catalogJson, cancellationToken)
                    .ConfigureAwait(false);
                await WriteModelProfileLayersAsync(paths, effectiveSettings, liveModelIds!, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (effectiveSettings.KeysmithModeEnabled)
            {
                await KeysmithBootstrap.ApplyAsync(paths, effectiveSettings, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await KeysmithBootstrap.EnsureUserConfigInstructionPointerAsync(
                        paths,
                        "./system-prompt.md",
                        cancellationToken)
                    .ConfigureAwait(false);
                await AtomicFile.WriteTextAsync(
                    paths.ManagedConfigFile,
                    ConfigIsolationAuditor.CreateManagedConfig(paths, effectiveSettings),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception saveException)
        {
            try
            {
                await RestoreAsync(paths.SystemPromptFile, oldPrompt, cancellationToken).ConfigureAwait(false);
                await RestoreAsync(paths.AiSettingsFile, oldSettings, cancellationToken).ConfigureAwait(false);
                await RestoreAsync(paths.ManagedConfigFile, oldManaged, cancellationToken).ConfigureAwait(false);
                await RestoreAsync(paths.ModelCatalogFile, oldCatalog, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new ProfileAiSettingsException(
                    "AI_SETTINGS_ROLLBACK_FAILED",
                    "AI 配置保存失败，且无法完整回滚。",
                    $"保存错误：{saveException.Message}{Environment.NewLine}回滚错误：{rollbackException.Message}",
                    new AggregateException(saveException, rollbackException));
            }

            throw;
        }

        if (string.IsNullOrEmpty(effectiveSettings.ApiKey))
        {
            DeleteIfExists(paths.AiSettingsFile + ".bak");
        }

        return await LoadUnlockedAsync(paths, initializeMissing: false, cancellationToken).ConfigureAwait(false);
    }

    public Task<AiConnectionTestResult> TestConnectionAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default) =>
        FetchModelsAsync(baseUrl, apiKey, cancellationToken);

    public Task<AiConnectionTestResult> FetchModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("拉取模型时 Key 不能为空。", nameof(apiKey));
        }

        return FetchModelsUnlockedAsync(baseUrl, apiKey, cancellationToken);
    }

    public async Task<ProfileAiLaunchConfiguration> ResolveLaunchConfigurationAsync(
        ProfilePaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await using var operationLock = await AcquireLockAsync(paths, cancellationToken).ConfigureAwait(false);
        var settings = await LoadUnlockedAsync(paths, initializeMissing: true, cancellationToken).ConfigureAwait(false);

        // Ensure instruction file is present for this profile before Codex starts.
        settings = ApplyDefaultInstructionContent(settings);
        if (settings.KeysmithModeEnabled)
        {
            settings = await KeysmithBootstrap.ApplyAsync(paths, settings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteSystemPromptFilesAsync(paths, settings, cancellationToken).ConfigureAwait(false);
            await KeysmithBootstrap.EnsureUserConfigInstructionPointerAsync(
                    paths,
                    "./system-prompt.md",
                    cancellationToken)
                .ConfigureAwait(false);
            await AtomicFile.WriteTextAsync(
                paths.ManagedConfigFile,
                ConfigIsolationAuditor.CreateManagedConfig(paths, settings),
                cancellationToken).ConfigureAwait(false);
        }

        await AtomicFile.WriteTextAsync(paths.AiSettingsFile, Serialize(settings), cancellationToken)
            .ConfigureAwait(false);

        if (!settings.ApiEnabled)
        {
            await AtomicFile.WriteTextAsync(
                paths.ManagedConfigFile,
                ConfigIsolationAuditor.CreateManagedConfig(paths, settings),
                cancellationToken).ConfigureAwait(false);
            return ProfileAiLaunchConfiguration.Disabled;
        }

        var models = await FetchModelsUnlockedAsync(settings.BaseUrl, settings.ApiKey, cancellationToken)
            .ConfigureAwait(false);
        var effectiveSettings = ApplyLiveModels(
            settings with
            {
                ModelReasoningEffort = CodexModelCatalogBuilder.NormalizeReasoningEffort(settings.ModelReasoningEffort),
            },
            models);
        var catalogJson = CodexModelCatalogBuilder.BuildJson(models.ModelIds, effectiveSettings.SelectedModel);

        var oldSettings = await ReadOptionalTextAsync(paths.AiSettingsFile, cancellationToken).ConfigureAwait(false);
        var oldManaged = await ReadOptionalTextAsync(paths.ManagedConfigFile, cancellationToken).ConfigureAwait(false);
        var oldCatalog = await ReadOptionalTextAsync(paths.ModelCatalogFile, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!effectiveSettings.SelectedModel.Equals(settings.SelectedModel, StringComparison.Ordinal) ||
                !effectiveSettings.ModelReasoningEffort.Equals(settings.ModelReasoningEffort, StringComparison.Ordinal))
            {
                await AtomicFile.WriteTextAsync(paths.AiSettingsFile, Serialize(effectiveSettings), cancellationToken)
                    .ConfigureAwait(false);
            }

            await AtomicFile.WriteTextAsync(paths.ModelCatalogFile, catalogJson, cancellationToken)
                .ConfigureAwait(false);
            await WriteModelProfileLayersAsync(paths, effectiveSettings, models.ModelIds, cancellationToken)
                .ConfigureAwait(false);
            await AtomicFile.WriteTextAsync(
                paths.ManagedConfigFile,
                ConfigIsolationAuditor.CreateManagedConfig(paths, effectiveSettings),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception writeException)
        {
            try
            {
                await RestoreAsync(paths.AiSettingsFile, oldSettings, cancellationToken).ConfigureAwait(false);
                await RestoreAsync(paths.ManagedConfigFile, oldManaged, cancellationToken).ConfigureAwait(false);
                await RestoreAsync(paths.ModelCatalogFile, oldCatalog, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new ProfileAiSettingsException(
                    "AI_SETTINGS_ROLLBACK_FAILED",
                    "启动前刷新模型目录失败，且无法完整回滚。",
                    $"写入错误：{writeException.Message}{Environment.NewLine}回滚错误：{rollbackException.Message}",
                    new AggregateException(writeException, rollbackException));
            }

            throw;
        }

        return new(true, effectiveSettings.ApiKey);
    }

    private async Task<AiConnectionTestResult> FetchModelsUnlockedAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var baseUri = ValidateBaseUrl(baseUrl);
        var requestUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/models", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var modelIds = TryReadModelIds(body);
            return new(
                requestUri,
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                stopwatch.Elapsed,
                modelIds.Count > 0 ? modelIds.Count : TryReadModelCount(body),
                modelIds,
                response.IsSuccessStatusCode
                    ? $"HTTP {(int)response.StatusCode}; 模型数量：{(modelIds.Count > 0 ? modelIds.Count.ToString(CultureInfo.InvariantCulture) : TryReadModelCount(body)?.ToString(CultureInfo.InvariantCulture) ?? "无法解析")}。"
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{body}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new(requestUri, false, null, stopwatch.Elapsed, null, AiConnectionTestResult.EmptyModelIds, "请求超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            stopwatch.Stop();
            return new(requestUri, false, null, stopwatch.Elapsed, null, AiConnectionTestResult.EmptyModelIds, ex.ToString());
        }
    }

    private static ProfileAiSettings ApplyLiveModels(ProfileAiSettings settings, AiConnectionTestResult models)
    {
        if (!models.IsSuccess)
        {
            throw new ProfileAiSettingsException(
                "AI_MODELS_FETCH_FAILED",
                "实时拉取模型列表失败。",
                models.Details);
        }

        if (models.ModelIds.Count == 0)
        {
            throw new ProfileAiSettingsException(
                "AI_MODELS_EMPTY",
                "实时拉取的模型列表为空。",
                $"请求地址：{models.RequestUri.AbsoluteUri}");
        }

        var selected = settings.SelectedModel.Trim();
        if (string.IsNullOrEmpty(selected))
        {
            selected = models.ModelIds[0];
        }
        else if (!models.ModelIds.Contains(selected, StringComparer.Ordinal))
        {
            throw new ProfileAiSettingsException(
                "AI_MODEL_NOT_IN_LIVE_LIST",
                "所选模型不在实时模型列表中。",
                $"selected_model={selected}；请重新拉取并选择当前可用的模型。");
        }

        return settings with
        {
            SelectedModel = selected,
            ModelReasoningEffort = CodexModelCatalogBuilder.NormalizeReasoningEffort(settings.ModelReasoningEffort),
        };
    }

    private static async Task WriteModelProfileLayersAsync(
        ProfilePaths paths,
        ProfileAiSettings settings,
        IReadOnlyList<string> modelIds,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.CodexHome);
        var selected = settings.SelectedModel.Trim();
        var effort = CodexModelCatalogBuilder.NormalizeReasoningEffort(settings.ModelReasoningEffort);
        var ordered = CodexModelCatalogBuilder.SelectModels(modelIds, selected);
        foreach (var modelId in ordered)
        {
            var stem = CodexModelCatalogBuilder.ToProfileFileStem(modelId);
            var path = Path.Combine(paths.CodexHome, stem + ".config.toml");
            var content =
                $"""
                # Generated by Codex Profile Launcher for model switching:
                #   codex --profile {stem}
                model = "{EscapeToml(modelId)}"
                model_provider = "profile_launcher"
                model_reasoning_effort = "{EscapeToml(effort)}"
                model_catalog_json = "{EscapeToml(PathUtilities.Normalize(paths.ModelCatalogFile).Replace('\\', '/'))}"
                """.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine;
            await AtomicFile.WriteTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ProfileAiSettings> LoadUnlockedAsync(
        ProfilePaths paths,
        bool initializeMissing,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.LauncherDirectory);
        if (initializeMissing && !File.Exists(paths.AiSettingsFile))
        {
            await AtomicFile.WriteTextAsync(paths.AiSettingsFile, Serialize(new()), cancellationToken)
                .ConfigureAwait(false);
        }

        if (initializeMissing && !File.Exists(paths.SystemPromptFile))
        {
            // New environments default to keysmith unrestricted text (product default).
            await AtomicFile.WriteTextAsync(
                paths.SystemPromptFile,
                KeysmithBootstrap.GetBundledPrompt(),
                cancellationToken).ConfigureAwait(false);
        }

        // Keep CODEX_HOME instruction mirror in sync on first load so Desktop can honor it
        // even before the user presses Save (launch path also rewrites it).
        if (initializeMissing &&
            File.Exists(paths.SystemPromptFile) &&
            !File.Exists(paths.CodexSystemPromptFile))
        {
            Directory.CreateDirectory(paths.CodexHome);
            var seedPrompt = await File.ReadAllTextAsync(paths.SystemPromptFile, cancellationToken)
                .ConfigureAwait(false);
            await AtomicFile.WriteTextAsync(paths.CodexSystemPromptFile, seedPrompt, cancellationToken)
                .ConfigureAwait(false);
        }

        var settingsText = await File.ReadAllTextAsync(paths.AiSettingsFile, cancellationToken).ConfigureAwait(false);
        var prompt = await File.ReadAllTextAsync(paths.SystemPromptFile, cancellationToken).ConfigureAwait(false);
        var settings = Parse(settingsText) with { SystemPrompt = prompt };
        return settings with { RevisionToken = ComputeRevision(settingsText, prompt) };
    }

    private static ProfileAiSettings Parse(string text)
    {
        try
        {
            _ = SyntaxParser.ParseStrict(text, "ai-settings.toml");
            var model = TomlSerializer.Deserialize<TomlTable>(text)
                ?? throw new InvalidOperationException("配置没有生成 TOML 模型。");
            var settings = new ProfileAiSettings
            {
                SchemaVersion = ReadInteger(model, "schema_version"),
                ApiEnabled = ReadBoolean(model, "api_enabled"),
                BaseUrl = ReadString(model, "base_url"),
                ApiKey = ReadString(model, "api_key"),
                SelectedModel = ReadOptionalString(model, "selected_model"),
                ModelReasoningEffort = CodexModelCatalogBuilder.NormalizeReasoningEffort(
                    ReadOptionalString(model, "model_reasoning_effort")),
                // Missing keys: system prompt replacement and keysmith mode are both on by default.
                SystemPromptEnabled = ReadBooleanOptional(model, "system_prompt_enabled", defaultValue: true),
                KeysmithModeEnabled = ReadBooleanOptional(model, "keysmith_mode_enabled", defaultValue: true),
            };
            Validate(settings);
            return settings;
        }
        catch (ProfileAiSettingsException)
        {
            throw;
        }
        catch (Exception ex) when (ex is TomlException or InvalidOperationException or IOException)
        {
            throw new ProfileAiSettingsException(
                "AI_SETTINGS_INVALID",
                "AI 配置文件无法读取。",
                ex.Message,
                ex);
        }
    }

    private static string Serialize(ProfileAiSettings settings) =>
        $$"""
        schema_version = {{ProfileAiSettings.CurrentSchemaVersion}}
        api_enabled = {{settings.ApiEnabled.ToString().ToLowerInvariant()}}
        base_url = "{{EscapeToml(settings.BaseUrl)}}"
        api_key = "{{EscapeToml(settings.ApiKey)}}"
        selected_model = "{{EscapeToml(settings.SelectedModel)}}"
        model_reasoning_effort = "{{EscapeToml(CodexModelCatalogBuilder.NormalizeReasoningEffort(settings.ModelReasoningEffort))}}"
        system_prompt_enabled = {{settings.SystemPromptEnabled.ToString().ToLowerInvariant()}}
        keysmith_mode_enabled = {{settings.KeysmithModeEnabled.ToString().ToLowerInvariant()}}
        """.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine;

    private static void Validate(ProfileAiSettings settings)
    {
        if (settings.SchemaVersion != ProfileAiSettings.CurrentSchemaVersion)
        {
            throw new ProfileAiSettingsException(
                "AI_SETTINGS_SCHEMA_UNSUPPORTED",
                "AI 配置版本不受支持。",
                $"只支持 schema_version={ProfileAiSettings.CurrentSchemaVersion}，实际为 {settings.SchemaVersion}。");
        }

        _ = ValidateBaseUrl(settings.BaseUrl);
        if (settings.ApiEnabled && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ProfileAiSettingsException(
                "AI_SETTINGS_KEY_REQUIRED",
                "启用 API 时 Key 不能为空。",
                "Key 不限制前缀，但必须包含至少一个非空白字符。");
        }
    }

    private static Uri ValidateBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ProfileAiSettingsException(
                "AI_SETTINGS_URL_INVALID",
                "Base URL 无效。",
                "地址必须是绝对 http/https URL。");
        }

        return uri;
    }

    public static IReadOnlyList<string> TryReadModelIds(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return AiConnectionTestResult.EmptyModelIds;
            }

            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadModelId(item);
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                ids.Add(id);
            }

            return ids;
        }
        catch (JsonException)
        {
            return AiConnectionTestResult.EmptyModelIds;
        }
    }

    private static string? ReadModelId(JsonElement item)
    {
        foreach (var propertyName in new[] { "id", "model", "model_id" })
        {
            if (item.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static int? TryReadModelCount(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ReadInteger(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is long number && number is >= int.MinValue and <= int.MaxValue
            ? (int)number
            : throw new InvalidOperationException($"{key} 必须是整数。");

    private static bool ReadBoolean(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is bool result
            ? result
            : throw new InvalidOperationException($"{key} 必须是布尔值。");

    private static bool ReadBooleanOptional(TomlTable table, string key, bool defaultValue) =>
        table.TryGetValue(key, out var value) && value is bool result
            ? result
            : defaultValue;

    /// <summary>
    /// Product policy: always replace Codex built-in instructions (SystemPromptEnabled=true).
    /// Keysmith mode uses unrestricted text; otherwise use bundled operator-core (or non-empty custom text).
    /// </summary>
    private static ProfileAiSettings ApplyDefaultInstructionContent(ProfileAiSettings settings)
    {
        if (settings.KeysmithModeEnabled)
        {
            return settings with
            {
                SystemPromptEnabled = true,
                SystemPrompt = KeysmithBootstrap.GetBundledPrompt(),
            };
        }

        var prompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
            ? DefaultSystemPrompt.GetBundled()
            : settings.SystemPrompt;

        return settings with
        {
            SystemPromptEnabled = true,
            SystemPrompt = prompt,
        };
    }

    /// <summary>
    /// Write launcher-side editor source and the CODEX_HOME instruction file Codex actually loads.
    /// </summary>
    private static async Task WriteSystemPromptFilesAsync(
        ProfilePaths paths,
        ProfileAiSettings settings,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.LauncherDirectory);
        Directory.CreateDirectory(paths.CodexHome);
        await AtomicFile.WriteTextAsync(paths.SystemPromptFile, settings.SystemPrompt, cancellationToken)
            .ConfigureAwait(false);
        await AtomicFile.WriteTextAsync(paths.CodexSystemPromptFile, settings.SystemPrompt, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ReadString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string result
            ? result
            : throw new InvalidOperationException($"{key} 必须是字符串。");

    private static string ReadOptionalString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string result
            ? result
            : string.Empty;

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string ComputeRevision(string settings, string prompt)
    {
        var bytes = Encoding.UTF8.GetBytes(settings + "\0" + prompt);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async Task<FileStream> AcquireLockAsync(
        ProfilePaths paths,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.LauncherDirectory);
        var started = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    paths.AiSettingsLockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException ex) when (started.Elapsed < TimeSpan.FromSeconds(5))
            {
                _ = ex;
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new ProfileAiSettingsException(
                    "AI_SETTINGS_LOCK_TIMEOUT",
                    "AI 配置正在被另一进程使用。",
                    "等待 profile 级文件锁 5 秒后仍未成功。",
                    ex);
            }
        }
    }

    private static async Task<string?> ReadOptionalTextAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;

    private static async Task RestoreAsync(string path, string? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            DeleteIfExists(path);
            return;
        }

        await AtomicFile.WriteTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
