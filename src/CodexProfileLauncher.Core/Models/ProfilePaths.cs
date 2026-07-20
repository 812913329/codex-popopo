namespace CodexProfileLauncher.Core.Models;

public sealed record ProfilePaths(
    string DataRoot,
    string CodexHome,
    string AppData,
    string ConfigFile,
    string ManagedConfigFile,
    string LogDirectory,
    string MarkerFile)
{
    public string LauncherDirectory => Path.Combine(DataRoot, ".launcher");

    public string AiSettingsFile => Path.Combine(LauncherDirectory, "ai-settings.toml");

    /// <summary>Editable launcher-side system prompt source of truth.</summary>
    public string SystemPromptFile => Path.Combine(LauncherDirectory, "system-prompt.md");

    public string AiSettingsLockFile => Path.Combine(LauncherDirectory, "ai-settings.lock");

    public string ModelCatalogFile => Path.Combine(LauncherDirectory, "model-catalog.json");

    /// <summary>
    /// Default model instructions path inside CODEX_HOME. Desktop/CLI more reliably honor
    /// <c>model_instructions_file</c> when the target lives under the profile home.
    /// </summary>
    public string CodexSystemPromptFile => Path.Combine(CodexHome, "system-prompt.md");

    /// <summary>
    /// Keysmith-compatible instruction path inside CODEX_HOME (Codex loads this more reliably than files outside home).
    /// </summary>
    public string KeysmithInstructionFile => Path.Combine(CodexHome, "gpt-unrestricted.md");

    /// <summary>Codex discovers user skills under $CODEX_HOME/skills.</summary>
    public string SkillsDirectory => Path.Combine(CodexHome, "skills");

    /// <summary>Disabled-but-kept skill folders (not loaded by Codex).</summary>
    public string SkillsDisabledDirectory => Path.Combine(LauncherDirectory, "skills-disabled");

    public static ProfilePaths FromRoot(string dataRoot)
    {
        var root = PathUtilities.Normalize(dataRoot);
        var codexHome = Path.Combine(root, "codex-home");
        var appData = Path.Combine(root, "app-data");

        return new ProfilePaths(
            root,
            codexHome,
            appData,
            Path.Combine(codexHome, "config.toml"),
            Path.Combine(codexHome, "managed_config.toml"),
            Path.Combine(codexHome, "log"),
            Path.Combine(root, ".codex-profile.json"));
    }
}

public static class PathUtilities
{
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。", nameof(path));
        }

        var trimmedPath = path.Trim();
        if (!Path.IsPathFullyQualified(trimmedPath))
        {
            throw new ArgumentException("路径必须是完整的绝对路径。", nameof(path));
        }

        var fullPath = Path.GetFullPath(trimmedPath);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            fullPath.Equals(root, PathComparison))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsSameOrNested(string candidate, string root)
    {
        var normalizedCandidate = Normalize(candidate);
        var normalizedRoot = Normalize(root);

        if (normalizedCandidate.Equals(normalizedRoot, PathComparison))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, PathComparison);
    }

    public static bool Overlaps(string first, string second) =>
        IsSameOrNested(first, second) || IsSameOrNested(second, first);

    public static bool IsFileSystemRoot(string path)
    {
        var normalized = Normalize(path);
        var root = Path.GetPathRoot(normalized);
        return !string.IsNullOrWhiteSpace(root) &&
               normalized.Equals(Normalize(root), PathComparison);
    }

    public static string? FindFirstReparsePoint(string path)
    {
        var normalized = Normalize(path);
        for (var probe = new DirectoryInfo(normalized); probe is not null; probe = probe.Parent)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(probe.FullName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Normalize(probe.FullName);
            }
        }

        return null;
    }
}
