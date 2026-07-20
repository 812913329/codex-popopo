using System.Text;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;

namespace CodexProfileLauncher.Core.Configuration;

public sealed record IsolationIssue(string Code, string Message, string Details);

public sealed record IsolationReport(IReadOnlyList<IsolationIssue> Issues)
{
    public bool IsIsolated => Issues.Count == 0;

    public static IsolationReport Success { get; } = new([]);
}

public sealed class ConfigIsolationAuditor
{
    private const string DesktopTableKey = "desktop";
    private const string WslBackendKey = "runCodexInWindowsSubsystemForLinux";

    private static readonly string[] ProjectIsolationOverrideKeys =
    [
        "cli_auth_credentials_store",
        "mcp_oauth_credentials_store",
        "sqlite_home",
        "log_dir",
    ];

    public static string CreateDefaultConfig()
    {
        return """
            # Codex 环境启动器：凭据保存在当前环境的数据目录中。
            cli_auth_credentials_store = "file"
            mcp_oauth_credentials_store = "file"
            # WSL 后端会把 SQLite 放入 Linux 用户共享目录，无法提供环境级隔离。
            desktop.runCodexInWindowsSubsystemForLinux = false
            """.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine;
    }

    public static string CreateManagedConfig(
        ProfilePaths paths,
        ProfileAiSettings? aiSettings = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // Keep a trailing newline after the locked isolation keys so subsequent appends
        // never glue onto log_dir when API overrides are off.
        var builder = new StringBuilder($$"""
            # Codex 环境启动器维护此文件，用最高优先级锁定隔离关键项。
            cli_auth_credentials_store = "file"
            mcp_oauth_credentials_store = "file"
            sqlite_home = "{{ToTomlPath(paths.CodexHome)}}"
            log_dir = "{{ToTomlPath(paths.LogDirectory)}}"

            """.ReplaceLineEndings(Environment.NewLine));

        if (aiSettings?.ApiEnabled == true)
        {
            var selectedModel = aiSettings.SelectedModel.Trim();
            var reasoning = CodexModelCatalogBuilder.NormalizeReasoningEffort(aiSettings.ModelReasoningEffort);

            if (!string.IsNullOrWhiteSpace(selectedModel))
            {
                builder.Append("model = \"")
                    .Append(ToTomlString(selectedModel))
                    .AppendLine("\"");
            }

            builder.Append("model_reasoning_effort = \"")
                .Append(ToTomlString(reasoning))
                .AppendLine("\"");
            // Encourage full reasoning-effort UI for custom providers/catalogs.
            builder.AppendLine("model_supports_reasoning_summaries = true");
            builder.AppendLine("model_provider = \"profile_launcher\"");
            builder.Append("model_catalog_json = \"")
                .Append(ToTomlPath(paths.ModelCatalogFile))
                .AppendLine("\"");
        }

        if (aiSettings?.SystemPromptEnabled == true)
        {
            // Always point at a file under CODEX_HOME. Desktop/CLI sometimes ignore
            // instruction paths that live outside the home (e.g. profile .launcher/).
            var instructionFile = aiSettings.KeysmithModeEnabled
                ? paths.KeysmithInstructionFile
                : paths.CodexSystemPromptFile;
            builder.Append("model_instructions_file = \"")
                .Append(ToTomlPath(instructionFile))
                .AppendLine("\"");
        }

        if (aiSettings?.ApiEnabled == true)
        {
            builder.AppendLine();
            builder.AppendLine("[model_providers.profile_launcher]");
            builder.AppendLine("name = \"Profile Launcher API\"");
            builder.Append("base_url = \"").Append(ToTomlString(aiSettings.BaseUrl)).AppendLine("\"");
            builder.Append("env_key = \"")
                .Append(ProfileAiLaunchConfiguration.ApiKeyEnvironmentVariable)
                .AppendLine("\"");
            builder.AppendLine("wire_api = \"responses\"");
            // Do not require ChatGPT login for third-party providers.
            builder.AppendLine("requires_openai_auth = false");
        }

        return builder.ToString().TrimEnd('\r', '\n') + Environment.NewLine;
    }

    public static async Task EnsureInitializedAsync(
        ProfilePaths paths,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.CodexHome);
        if (!File.Exists(paths.ConfigFile))
        {
            await AtomicFile.WriteTextAsync(
                paths.ConfigFile,
                CreateDefaultConfig(),
                cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(paths.ManagedConfigFile))
        {
            await AtomicFile.WriteTextAsync(
                paths.ManagedConfigFile,
                CreateManagedConfig(paths),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<IsolationReport> AuditFileAsync(
        ProfilePaths paths,
        string effectiveWorkingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.ConfigFile))
        {
            return new([
                new(
                    "PROFILE_CONFIG_MISSING",
                    "当前环境缺少配置文件。",
                    $"未找到：{paths.ConfigFile}")
            ]);
        }

        var text = await File.ReadAllTextAsync(paths.ConfigFile, cancellationToken).ConfigureAwait(false);
        var profileReport = AuditText(text, paths, effectiveWorkingDirectory);
        if (!profileReport.IsIsolated)
        {
            return profileReport;
        }


        var managedReport = await AuditManagedConfigAsync(paths, cancellationToken).ConfigureAwait(false);
        if (!managedReport.IsIsolated)
        {
            return managedReport;
        }

        return await AuditProjectConfigLayersAsync(effectiveWorkingDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<IsolationReport> AuditManagedConfigAsync(
        ProfilePaths paths,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.ManagedConfigFile))
        {
            return new([
                new(
                    "PROFILE_MANAGED_CONFIG_MISSING",
                    "当前环境缺少隔离锁定配置。",
                    $"未找到：{paths.ManagedConfigFile}")
            ]);
        }

        TomlTable model;
        try
        {
            var text = await File.ReadAllTextAsync(paths.ManagedConfigFile, cancellationToken)
                .ConfigureAwait(false);
            _ = SyntaxParser.ParseStrict(text, paths.ManagedConfigFile);
            model = TomlSerializer.Deserialize<TomlTable>(text)
                ?? throw new TomlException("隔离锁定配置没有生成有效的 TOML 模型。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is TomlException or InvalidOperationException or IOException or
                UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new([
                new(
                    "PROFILE_MANAGED_CONFIG_INVALID",
                    "隔离锁定配置无法安全核验。",
                    ex.Message)
            ]);
        }

        var issues = new List<IsolationIssue>();
        RequirePinnedString(model, "cli_auth_credentials_store", "file", "Codex 登录凭据", issues);
        RequirePinnedString(model, "mcp_oauth_credentials_store", "file", "MCP OAuth 凭据", issues);
        RequirePinnedPath(model, "sqlite_home", paths.CodexHome, "SQLite 状态目录", issues);
        RequirePinnedPath(model, "log_dir", paths.LogDirectory, "日志目录", issues);
        return new(issues);
    }

    public static async Task<IsolationReport> AuditProjectConfigLayersAsync(
        string effectiveWorkingDirectory,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> candidates;
        try
        {
            candidates = EnumerateProjectConfigCandidates(effectiveWorkingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new([
                new(
                    "PROJECT_CONFIG_PATH_INVALID",
                    "无法检查工作目录的项目配置。",
                    ex.Message)
            ]);
        }

        var issues = new List<IsolationIssue>();
        foreach (var configPath in candidates.Where(File.Exists))
        {
            string text;
            TomlTable model;
            try
            {
                text = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
                _ = SyntaxParser.ParseStrict(text, configPath);
                model = TomlSerializer.Deserialize<TomlTable>(text)
                    ?? throw new TomlException("项目配置没有生成有效的 TOML 模型。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is TomlException or InvalidOperationException or IOException or
                    UnauthorizedAccessException or System.Security.SecurityException)
            {
                issues.Add(new(
                    "PROJECT_CONFIG_UNVERIFIABLE",
                    "工作目录中的项目配置无法安全核验。",
                    $"{configPath}{Environment.NewLine}{ex.Message}"));
                continue;
            }

            var overrides = ProjectIsolationOverrideKeys
                .Where(model.ContainsKey)
                .ToArray();
            if (overrides.Length > 0)
            {
                issues.Add(new(
                    "PROJECT_CONFIG_OVERRIDES_ISOLATION",
                    "工作目录中的项目配置会覆盖环境隔离设置。",
                    $"请从“{configPath}”移除这些顶层键后再启动：{string.Join(", ", overrides)}。"));
            }

            RejectUnsupportedWslBackend(
                model,
                issues,
                "PROJECT_CONFIG_ENABLES_WSL_UNSUPPORTED",
                "工作目录中的项目配置会启用不受支持的 WSL 后端。",
                $"请在“{configPath}”中移除 {DesktopTableKey}.{WslBackendKey}，或将其严格设为 false。",
                requireExplicitFalse: false);
        }

        return new(issues);
    }

    internal static IReadOnlyList<string> EnumerateProjectConfigCandidates(string effectiveWorkingDirectory)
    {
        var workingDirectory = PathUtilities.Normalize(effectiveWorkingDirectory);
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddCandidate(List<string> values, HashSet<string> seenPaths, string path)
        {
            var normalized = PathUtilities.Normalize(path);
            if (seenPaths.Add(normalized))
            {
                values.Add(normalized);
            }
        }

        AddCandidate(candidates, seen, Path.Combine(workingDirectory, "config.toml"));
        for (var current = new DirectoryInfo(workingDirectory); current is not null; current = current.Parent)
        {
            AddCandidate(candidates, seen, Path.Combine(current.FullName, ".codex", "config.toml"));
        }

        return candidates;
    }

    public static IsolationReport AuditText(
        string text,
        ProfilePaths paths,
        string effectiveWorkingDirectory)
    {
        TomlTable model;
        try
        {
            _ = SyntaxParser.ParseStrict(text, paths.ConfigFile);
            model = TomlSerializer.Deserialize<TomlTable>(text)
                ?? throw new TomlException("配置没有生成有效的 TOML 模型。");
        }
        catch (Exception ex) when (ex is TomlException or InvalidOperationException)
        {
            return new([
                new(
                    "PROFILE_CONFIG_INVALID",
                    "配置文件存在语法错误。",
                    ex.Message)
            ]);
        }

        var issues = new List<IsolationIssue>();
        RequireFileCredentialStore(model, "cli_auth_credentials_store", "Codex 登录凭据", issues);
        RequireFileCredentialStore(model, "mcp_oauth_credentials_store", "MCP OAuth 凭据", issues);
        ValidateStatePath(model, "sqlite_home", "SQLite 状态目录", paths.DataRoot, effectiveWorkingDirectory, issues);
        ValidateStatePath(model, "log_dir", "日志目录", paths.DataRoot, effectiveWorkingDirectory, issues);
        RejectUnsupportedWslBackend(
            model,
            issues,
            "PROFILE_WSL_BACKEND_UNSUPPORTED",
            "当前环境不能使用 WSL 后端。",
            $"请在 config.toml 中将 {DesktopTableKey}.{WslBackendKey} 严格设为 false；WSL 后端会使用 Linux 用户共享的 SQLite 目录。",
            requireExplicitFalse: true);
        return new(issues);
    }

    public static async Task SaveValidatedAsync(
        string text,
        ProfilePaths paths,
        string effectiveWorkingDirectory,
        CancellationToken cancellationToken = default)
    {
        var report = AuditText(text, paths, effectiveWorkingDirectory);
        if (!report.IsIsolated)
        {
            throw new IsolationValidationException(report);
        }

        await AtomicFile.WriteTextAsync(paths.ConfigFile, text, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireFileCredentialStore(
        TomlTable model,
        string key,
        string label,
        List<IsolationIssue> issues)
    {
        if (!model.TryGetValue(key, out var rawValue))
        {
            issues.Add(new(
                "PROFILE_AUTH_NOT_ISOLATED",
                $"{label}尚未设置为环境内存储。",
                $"请在 config.toml 中设置 {key} = \"file\"。"));
            return;
        }

        if (rawValue is not string value)
        {
            issues.Add(new(
                "PROFILE_AUTH_STORE_INVALID",
                $"{label}存储设置无效。",
                $"{key} 必须是字符串 \"file\"。"));
            return;
        }

        if (!value.Equals("file", StringComparison.Ordinal))
        {
            issues.Add(new(
                "PROFILE_AUTH_NOT_ISOLATED",
                $"{label}可能与其他环境共享。",
                $"{key} 当前为 \"{value}\"；严格隔离要求使用 \"file\"。"));
        }
    }

    private static void RejectUnsupportedWslBackend(
        TomlTable model,
        List<IsolationIssue> issues,
        string code,
        string message,
        string details,
        bool requireExplicitFalse)
    {
        if (!model.TryGetValue(DesktopTableKey, out var rawDesktop) ||
            rawDesktop is not TomlTable desktop ||
            !desktop.TryGetValue(WslBackendKey, out var rawSetting))
        {
            if (requireExplicitFalse)
            {
                issues.Add(new(code, message, details));
            }

            return;
        }

        if (rawSetting is bool enabled && !enabled)
        {
            return;
        }

        issues.Add(new(code, message, details));
    }

    private static void RequirePinnedString(
        TomlTable model,
        string key,
        string expected,
        string label,
        List<IsolationIssue> issues)
    {
        if (!model.TryGetValue(key, out var rawValue) ||
            rawValue is not string value ||
            !value.Equals(expected, StringComparison.Ordinal))
        {
            issues.Add(new(
                "PROFILE_MANAGED_CONFIG_NOT_ISOLATED",
                $"{label}未被隔离锁定。",
                $"managed_config.toml 必须设置 {key} = \"{expected}\"。"));
        }
    }

    private static void RequirePinnedPath(
        TomlTable model,
        string key,
        string expected,
        string label,
        List<IsolationIssue> issues)
    {
        if (!model.TryGetValue(key, out var rawValue) || rawValue is not string value)
        {
            issues.Add(new(
                "PROFILE_MANAGED_CONFIG_NOT_ISOLATED",
                $"{label}未被隔离锁定。",
                $"managed_config.toml 缺少字符串键 {key}。"));
            return;
        }

        try
        {
            var normalizedValue = PathUtilities.Normalize(value);
            var normalizedExpected = PathUtilities.Normalize(expected);
            if (!normalizedValue.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(
                    "PROFILE_MANAGED_CONFIG_NOT_ISOLATED",
                    $"{label}未锁定到当前环境。",
                    $"{key} 必须为“{normalizedExpected}”，当前为“{value}”。"));
                return;
            }

            var reparsePoint = PathUtilities.FindFirstReparsePoint(normalizedExpected);
            if (reparsePoint is not null)
            {
                issues.Add(new(
                    "PROFILE_MANAGED_CONFIG_NOT_ISOLATED",
                    $"{label}经过不受支持的链接路径。",
                    $"检测到 reparse point/junction：{reparsePoint}"));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new(
                "PROFILE_MANAGED_CONFIG_NOT_ISOLATED",
                $"{label}锁定路径无效。",
                ex.Message));
        }
    }

    private static string ToTomlPath(string path) =>
        PathUtilities.Normalize(path)
            .Replace('\\', '/')
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ToTomlString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void ValidateStatePath(
        TomlTable model,
        string key,
        string label,
        string dataRoot,
        string effectiveWorkingDirectory,
        List<IsolationIssue> issues)
    {
        if (!model.TryGetValue(key, out var rawValue))
        {
            return;
        }

        if (rawValue is not string value || string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new(
                "PROFILE_STATE_PATH_INVALID",
                $"{label}设置无效。",
                $"{key} 必须是非空路径字符串。"));
            return;
        }

        try
        {
            if (!Path.IsPathFullyQualified(value))
            {
                issues.Add(new(
                    "PROFILE_STATE_PATH_INVALID",
                    $"{label}必须使用绝对路径。",
                    $"{key} 当前为“{value}”；Codex 只接受绝对路径。"));
                return;
            }

            var normalizedRoot = PathUtilities.Normalize(dataRoot);
            var resolved = PathUtilities.Normalize(value);
            if (!PathUtilities.IsSameOrNested(resolved, normalizedRoot))
            {
                issues.Add(new(
                    "PROFILE_STATE_ESCAPES_ROOT",
                    $"{label}位于当前环境之外。",
                    $"{key} 解析为“{resolved}”，必须位于“{normalizedRoot}”之内。"));
                return;
            }

            if (File.Exists(resolved))
            {
                issues.Add(new(
                    "PROFILE_STATE_PATH_INVALID",
                    $"{label}不是目录。",
                    $"{key} 解析为现有文件“{resolved}”，必须指向目录。"));
                return;
            }

            var reparsePoint = PathUtilities.FindFirstReparsePoint(resolved);
            if (reparsePoint is not null)
            {
                issues.Add(new(
                    "PROFILE_STATE_REPARSE_POINT_UNSUPPORTED",
                    $"{label}使用了链接或重定向。",
                    $"{key} 的路径链包含 reparse/junction“{reparsePoint}”，无法证明状态仍位于当前环境内。"));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new(
                "PROFILE_STATE_PATH_INVALID",
                $"{label}路径无效。",
                ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            issues.Add(new(
                "PROFILE_STATE_PATH_UNVERIFIABLE",
                $"{label}路径无法安全核验。",
                ex.Message));
        }
    }
}

public sealed class IsolationValidationException(IsolationReport report)
    : Exception(report.Issues.Count > 0 ? report.Issues[0].Message : "配置未通过隔离检查。")
{
    public IsolationReport Report { get; } = report;
}

public static class AtomicFile
{
    public static async Task WriteTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"无法确定文件目录：{path}");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = path + ".bak";

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
