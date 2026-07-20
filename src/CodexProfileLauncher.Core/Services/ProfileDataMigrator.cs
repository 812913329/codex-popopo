using System.Text;
using CodexProfileLauncher.Core.Configuration;
using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Services;

public sealed class ProfileDataMigrationException : Exception
{
    public ProfileDataMigrationException(string code, string message, string details, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public string Details { get; }
}

/// <summary>
/// Moves a profile data root and rewrites absolute path references inside text configs.
/// </summary>
public static class ProfileDataMigrator
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".toml", ".json", ".jsonl", ".md", ".txt", ".yml", ".yaml",
    };

    public static async Task MigrateDataRootAsync(
        Guid profileId,
        string sourceDataRoot,
        string destinationDataRoot,
        ProfileAiSettings? aiSettings = null,
        CancellationToken cancellationToken = default)
    {
        var source = PathUtilities.Normalize(sourceDataRoot);
        var destination = PathUtilities.Normalize(destinationDataRoot);

        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Directory.Exists(source))
        {
            throw new ProfileDataMigrationException(
                "MIGRATE_SOURCE_MISSING",
                "原环境数据目录不存在，无法迁移。",
                source);
        }

        if (PathUtilities.Overlaps(source, destination) &&
            !source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileDataMigrationException(
                "MIGRATE_NESTED_PATH",
                "新目录不能位于原目录内部，也不能把原目录嵌进新目录。",
                $"source={source}; destination={destination}");
        }

        if (Directory.Exists(destination))
        {
            if (Directory.EnumerateFileSystemEntries(destination).Any())
            {
                throw new ProfileDataMigrationException(
                    "MIGRATE_DESTINATION_NOT_EMPTY",
                    "目标目录已存在且非空，请选择空目录或新路径。",
                    destination);
            }

            // Empty destination can be removed so Directory.Move can take its place.
            Directory.Delete(destination, recursive: false);
        }

        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await Task.Run(
            () => MoveOrCopyDirectory(source, destination),
            cancellationToken).ConfigureAwait(false);

        var newPaths = ProfilePaths.FromRoot(destination);
        await RewritePathReferencesAsync(destination, source, destination, cancellationToken)
            .ConfigureAwait(false);

        // Rebuild managed isolation layer for the new absolute paths.
        // Marker file moves with the tree and remains profile-id compatible.
        _ = profileId;
        var managed = ConfigIsolationAuditor.CreateManagedConfig(newPaths, aiSettings);
        await AtomicFile.WriteTextAsync(newPaths.ManagedConfigFile, managed, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void MoveOrCopyDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Cross-volume moves fail on Windows; fall through to copy + delete.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through.
        }

        CopyDirectoryRecursive(source, destination);
        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileDataMigrationException(
                "MIGRATE_COPY_CLEANUP_FAILED",
                "数据已复制到新目录，但原目录未能删除。请手动检查并清理原目录。",
                $"source={source}; destination={destination}; {ex.Message}",
                ex);
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var target = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            CopyDirectoryRecursive(dir, Path.Combine(destinationDir, name));
        }
    }

    private static async Task RewritePathReferencesAsync(
        string searchRoot,
        string oldRoot,
        string newRoot,
        CancellationToken cancellationToken)
    {
        var oldNorm = PathUtilities.Normalize(oldRoot);
        var newNorm = PathUtilities.Normalize(newRoot);
        var replacements = BuildReplacementPairs(oldNorm, newNorm);

        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            if (!TextExtensions.Contains(ext))
            {
                continue;
            }

            // Skip very large text files (safety).
            var info = new FileInfo(file);
            if (info.Length > 4 * 1024 * 1024)
            {
                continue;
            }

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var updated = text;
            foreach (var (from, to) in replacements)
            {
                if (updated.Contains(from, StringComparison.OrdinalIgnoreCase))
                {
                    updated = ReplaceIgnoreCase(updated, from, to);
                }
            }

            if (!string.Equals(text, updated, StringComparison.Ordinal))
            {
                await AtomicFile.WriteTextAsync(file, updated, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static List<(string From, string To)> BuildReplacementPairs(string oldRoot, string newRoot)
    {
        var pairs = new List<(string, string)>
        {
            (oldRoot, newRoot),
            (oldRoot.Replace('\\', '/'), newRoot.Replace('\\', '/')),
            (oldRoot.Replace("\\", "\\\\", StringComparison.Ordinal),
                newRoot.Replace("\\", "\\\\", StringComparison.Ordinal)),
        };

        // Deduplicate identical pairs.
        return pairs
            .Where(p => !p.Item1.Equals(p.Item2, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string ReplaceIgnoreCase(string input, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(oldValue))
        {
            return input;
        }

        var sb = new StringBuilder(input.Length);
        var index = 0;
        while (index < input.Length)
        {
            var match = input.IndexOf(oldValue, index, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                sb.Append(input, index, input.Length - index);
                break;
            }

            sb.Append(input, index, match - index);
            sb.Append(newValue);
            index = match + oldValue.Length;
        }

        return sb.ToString();
    }
}

