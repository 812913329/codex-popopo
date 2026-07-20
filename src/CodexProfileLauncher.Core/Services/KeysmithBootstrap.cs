using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodexProfileLauncher.Core.Configuration;
using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Services;

/// <summary>
/// Native integration of the codex-keysmith deployment model for isolated profiles:
/// 1) write a model instruction markdown file
/// 2) set managed <c>model_instructions_file</c>
/// 3) isolate active hooks.json (same intent as keysmith default isolation)
/// Does not shell out to Python; embeds the v0.1.0 bundled prompt (MIT).
/// </summary>
public static class KeysmithBootstrap
{
    public const string PromptFileName = "gpt-unrestricted.md";
    public const string ExpectedPromptSha256 =
        "0ac8420d504f1a42db87be9f8555f740bf4c1e7b72beb0dde6a4b8d70b6cda07";

    public const string SourceProjectUrl = "https://github.com/Jia-Ethan/codex-keysmith";
    public const string SourceVersionNote = "codex-keysmith v0.1.0 examples/gpt-unrestricted.md";

    private static readonly Lazy<string> BundledPrompt = new(LoadBundledPrompt);

    public static string GetBundledPrompt() => BundledPrompt.Value;

    public static string ComputeSha256(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Apply keysmith-equivalent profile state: instruction file + hooks isolation.
    /// Returns updated settings (system prompt enabled + content when keysmith mode is on).
    /// </summary>
    public static async Task<ProfileAiSettings> ApplyAsync(
        ProfilePaths paths,
        ProfileAiSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(paths.CodexHome);
        Directory.CreateDirectory(paths.LauncherDirectory);

        var effective = settings;
        if (settings.KeysmithModeEnabled)
        {
            var prompt = GetBundledPrompt();
            effective = settings with
            {
                SystemPromptEnabled = true,
                SystemPrompt = prompt,
            };

            await AtomicFile.WriteTextAsync(paths.SystemPromptFile, prompt, cancellationToken)
                .ConfigureAwait(false);

            // Canonical instruction path lives inside CODEX_HOME (same as upstream keysmith).
            await AtomicFile.WriteTextAsync(paths.KeysmithInstructionFile, prompt, cancellationToken)
                .ConfigureAwait(false);

            // Also pin relative path in user config.toml for loaders that prefer user config.
            await EnsureUserConfigInstructionPointerAsync(
                    paths,
                    "./" + PromptFileName,
                    cancellationToken)
                .ConfigureAwait(false);

            await IsolateHooksAsync(paths.CodexHome, cancellationToken).ConfigureAwait(false);
        }

        await AtomicFile.WriteTextAsync(
            paths.ManagedConfigFile,
            ConfigIsolationAuditor.CreateManagedConfig(paths, effective),
            cancellationToken).ConfigureAwait(false);

        return effective;
    }

    /// <summary>
    /// Upserts top-level <c>model_instructions_file</c> into config.toml without rewriting unrelated content.
    /// </summary>
    public static Task EnsureUserConfigInstructionPointerAsync(
        ProfilePaths paths,
        CancellationToken cancellationToken = default) =>
        EnsureUserConfigInstructionPointerAsync(paths, "./" + PromptFileName, cancellationToken);

    /// <summary>
    /// Upserts top-level <c>model_instructions_file = "{relativePath}"</c> into config.toml
    /// without rewriting unrelated content (keysmith-compatible relative form).
    /// </summary>
    public static async Task EnsureUserConfigInstructionPointerAsync(
        ProfilePaths paths,
        string relativeInstructionPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeInstructionPath);
        Directory.CreateDirectory(paths.CodexHome);
        const string key = "model_instructions_file";
        var desired = relativeInstructionPath.Trim();
        var desiredLine = $"{key} = \"{desired}\"";

        string existing = string.Empty;
        if (File.Exists(paths.ConfigFile))
        {
            existing = await File.ReadAllTextAsync(paths.ConfigFile, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            existing = ConfigIsolationAuditor.CreateDefaultConfig();
        }

        var lines = existing.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();

        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            // Only rewrite top-level assignment (before any [table]).
            if (trimmed.StartsWith('[') && !trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = desiredLine;
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            // Insert after initial comment block / at top of keys.
            var insertAt = 0;
            while (insertAt < lines.Count &&
                   (string.IsNullOrWhiteSpace(lines[insertAt]) || lines[insertAt].TrimStart().StartsWith('#')))
            {
                insertAt++;
            }

            lines.Insert(insertAt, desiredLine);
            if (insertAt + 1 < lines.Count && !string.IsNullOrWhiteSpace(lines[insertAt + 1]))
            {
                lines.Insert(insertAt + 1, string.Empty);
            }
        }

        var text = string.Join(Environment.NewLine, lines).TrimEnd('\r', '\n') + Environment.NewLine;
        await AtomicFile.WriteTextAsync(paths.ConfigFile, text, cancellationToken).ConfigureAwait(false);
    }

    public static async Task IsolateHooksAsync(string codexHome, CancellationToken cancellationToken = default)
    {
        var active = Path.Combine(codexHome, "hooks.json");
        var disabled = Path.Combine(codexHome, "hooks.json.disabled");
        if (!File.Exists(active))
        {
            return;
        }

        if (File.Exists(disabled))
        {
            // Keep existing disabled ownership; move active to timestamped backup.
            var backup = disabled + ".bak_" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            await Task.Run(() => File.Move(active, backup), cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Run(() => File.Move(active, disabled), cancellationToken).ConfigureAwait(false);
    }

    private static string LoadBundledPrompt()
    {
        var assembly = typeof(KeysmithBootstrap).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("gpt-unrestricted.md", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("无法打开内置 keysmith 提示词资源流。");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var embedded = reader.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                return NormalizeNewlines(embedded);
            }
        }

        // Dev/layout fallback: search near base directory.
        foreach (var candidate in EnumerateDiskCandidates())
        {
            if (File.Exists(candidate))
            {
                return NormalizeNewlines(File.ReadAllText(candidate, Encoding.UTF8));
            }
        }

        throw new InvalidOperationException(
            "未找到内置 keysmith 提示词（gpt-unrestricted.md）。请确认项目已嵌入 Assets/keysmith 资源。");
    }

    private static IEnumerable<string> EnumerateDiskCandidates()
    {
        var bases = new List<string> { AppContext.BaseDirectory };
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                bases.Add(dir.FullName);
            }
        }
        catch
        {
            // ignore
        }

        foreach (var root in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "Assets", "keysmith", PromptFileName);
            yield return Path.Combine(root, "assets", "keysmith", PromptFileName);
            yield return Path.Combine(root, "gpt-unrestricted.md");
        }
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
}
