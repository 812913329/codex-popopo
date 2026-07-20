using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Services;

public interface IProfileSkillsService
{
    string ResolveBuiltinRoot(string? appBaseDirectory = null);

    ProfileSkillsSnapshot List(ProfilePaths paths, string? appBaseDirectory = null);

    Task InstallAllBuiltinAsync(ProfilePaths paths, string? appBaseDirectory = null, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(
        ProfilePaths paths,
        string skillId,
        bool enabled,
        string? appBaseDirectory = null,
        CancellationToken cancellationToken = default);

    Task<string> ReadSkillMarkdownAsync(ProfilePaths paths, string skillId, CancellationToken cancellationToken = default);

    Task SaveSkillMarkdownAsync(
        ProfilePaths paths,
        string skillId,
        string content,
        CancellationToken cancellationToken = default);

    Task ResetToBuiltinAsync(
        ProfilePaths paths,
        string skillId,
        string? appBaseDirectory = null,
        CancellationToken cancellationToken = default);

    Task ImportFromFolderAsync(
        ProfilePaths paths,
        string sourceDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileSkillsService : IProfileSkillsService
{
    public const string SystemSkillsFolderName = ".system";
    public const string SkillMarkdownFileName = "SKILL.md";

    private static readonly Regex FrontmatterBlock = new(
        @"^---\s*\r?\n(.*?)\r?\n---\s*(?:\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex FrontmatterField = new(
        @"^(?<key>[A-Za-z0-9_-]+)\s*:\s*(?<value>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public string ResolveBuiltinRoot(string? appBaseDirectory = null)
    {
        // Explicit app base wins (tests and alternate install layouts).
        if (!string.IsNullOrWhiteSpace(appBaseDirectory))
        {
            var forced = Path.Combine(appBaseDirectory.Trim(), "skills", "builtin");
            return Path.GetFullPath(forced)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var bases = new List<string> { AppContext.BaseDirectory };

        // Dev layout: bin/.../ -> repo/skills/builtin
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                bases.Add(dir.FullName);
                var candidate = Path.Combine(dir.FullName, "skills", "builtin");
                if (Directory.Exists(candidate) && HasAnySkill(candidate))
                {
                    return PathUtilities.Normalize(candidate);
                }
            }
        }
        catch
        {
            // Fall through to relative probes.
        }

        foreach (var root in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(root, "skills", "builtin");
            if (Directory.Exists(candidate) && HasAnySkill(candidate))
            {
                return PathUtilities.Normalize(candidate);
            }
        }

        // Last resort: return expected output path even if empty (UI can show empty state).
        return PathUtilities.Normalize(Path.Combine(AppContext.BaseDirectory, "skills", "builtin"));
    }

    public ProfileSkillsSnapshot List(ProfilePaths paths, string? appBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var builtinRoot = ResolveBuiltinRoot(appBaseDirectory);
        Directory.CreateDirectory(paths.SkillsDirectory);
        Directory.CreateDirectory(paths.SkillsDisabledDirectory);

        var map = new Dictionary<string, SkillDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in EnumerateSkillDirs(builtinRoot))
        {
            var id = dir.Name;
            var fm = TryReadFrontmatter(Path.Combine(dir.FullName, SkillMarkdownFileName), id);
            map[id] = new SkillDescriptor(
                Id: id,
                Name: fm.Name,
                Description: fm.Description,
                Source: SkillSource.Builtin,
                RootPath: dir.FullName,
                IsEnabled: false,
                IsCustomized: false,
                IsBuiltinAvailable: true);
        }

        foreach (var dir in EnumerateSkillDirs(paths.SkillsDisabledDirectory))
        {
            var id = dir.Name;
            var fm = TryReadFrontmatter(Path.Combine(dir.FullName, SkillMarkdownFileName), id);
            var builtinPath = Path.Combine(builtinRoot, id);
            var hasBuiltin = Directory.Exists(builtinPath);
            map[id] = new SkillDescriptor(
                Id: id,
                Name: fm.Name,
                Description: fm.Description,
                Source: SkillSource.Disabled,
                RootPath: dir.FullName,
                IsEnabled: false,
                IsCustomized: hasBuiltin && IsCustomized(dir.FullName, builtinPath),
                IsBuiltinAvailable: hasBuiltin);
        }

        foreach (var dir in EnumerateSkillDirs(paths.SkillsDirectory))
        {
            var id = dir.Name;
            if (id.Equals(SystemSkillsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fm = TryReadFrontmatter(Path.Combine(dir.FullName, SkillMarkdownFileName), id);
            var builtinPath = Path.Combine(builtinRoot, id);
            var hasBuiltin = Directory.Exists(builtinPath);
            map[id] = new SkillDescriptor(
                Id: id,
                Name: fm.Name,
                Description: fm.Description,
                Source: SkillSource.Environment,
                RootPath: dir.FullName,
                IsEnabled: true,
                IsCustomized: hasBuiltin && IsCustomized(dir.FullName, builtinPath),
                IsBuiltinAvailable: hasBuiltin);
        }

        var skills = map.Values
            .OrderByDescending(s => s.IsEnabled)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProfileSkillsSnapshot(
            skills,
            skills.Count(s => s.IsEnabled),
            paths.SkillsDirectory,
            builtinRoot);
    }

    public async Task InstallAllBuiltinAsync(
        ProfilePaths paths,
        string? appBaseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var builtinRoot = ResolveBuiltinRoot(appBaseDirectory);
        Directory.CreateDirectory(paths.SkillsDirectory);
        Directory.CreateDirectory(paths.SkillsDisabledDirectory);

        foreach (var dir in EnumerateSkillDirs(builtinRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var enabledPath = Path.Combine(paths.SkillsDirectory, dir.Name);
            var disabledPath = Path.Combine(paths.SkillsDisabledDirectory, dir.Name);
            if (Directory.Exists(enabledPath) || Directory.Exists(disabledPath))
            {
                continue;
            }

            await Task.Run(() => CopyDirectory(dir.FullName, enabledPath), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetEnabledAsync(
        ProfilePaths paths,
        string skillId,
        bool enabled,
        string? appBaseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var id = NormalizeSkillId(skillId);
        if (id.Equals(SystemSkillsFolderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileSkillsException(
                "SKILL_SYSTEM_PROTECTED",
                "不能修改系统技能目录。",
                $".system under {paths.SkillsDirectory}");
        }

        Directory.CreateDirectory(paths.SkillsDirectory);
        Directory.CreateDirectory(paths.SkillsDisabledDirectory);

        var enabledPath = Path.Combine(paths.SkillsDirectory, id);
        var disabledPath = Path.Combine(paths.SkillsDisabledDirectory, id);
        var builtinPath = Path.Combine(ResolveBuiltinRoot(appBaseDirectory), id);

        if (enabled)
        {
            if (Directory.Exists(enabledPath))
            {
                return;
            }

            if (Directory.Exists(disabledPath))
            {
                await Task.Run(() => MoveDirectory(disabledPath, enabledPath), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!Directory.Exists(builtinPath))
            {
                throw new ProfileSkillsException(
                    "SKILL_NOT_FOUND",
                    "找不到该技能。",
                    $"既无环境副本也无内置模板：{id}");
            }

            await Task.Run(() => CopyDirectory(builtinPath, enabledPath), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Disable
        if (!Directory.Exists(enabledPath))
        {
            return;
        }

        if (Directory.Exists(disabledPath))
        {
            await Task.Run(() => Directory.Delete(disabledPath, recursive: true), cancellationToken).ConfigureAwait(false);
        }

        await Task.Run(() => MoveDirectory(enabledPath, disabledPath), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadSkillMarkdownAsync(
        ProfilePaths paths,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var id = NormalizeSkillId(skillId);
        var file = ResolveExistingSkillMarkdown(paths, id)
            ?? throw new ProfileSkillsException(
                "SKILL_NOT_FOUND",
                "技能未安装或未找到 SKILL.md。",
                id);

        return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSkillMarkdownAsync(
        ProfilePaths paths,
        string skillId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var id = NormalizeSkillId(skillId);
        if (id.Equals(SystemSkillsFolderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileSkillsException(
                "SKILL_SYSTEM_PROTECTED",
                "不能修改系统技能。",
                id);
        }

        // Editing always targets the environment-enabled copy; enable first if needed.
        var enabledDir = Path.Combine(paths.SkillsDirectory, id);
        if (!Directory.Exists(enabledDir))
        {
            var disabledDir = Path.Combine(paths.SkillsDisabledDirectory, id);
            if (Directory.Exists(disabledDir))
            {
                Directory.CreateDirectory(paths.SkillsDirectory);
                MoveDirectory(disabledDir, enabledDir);
            }
            else
            {
                throw new ProfileSkillsException(
                    "SKILL_NOT_ENABLED",
                    "请先启用技能再编辑。",
                    id);
            }
        }

        var file = Path.Combine(enabledDir, SkillMarkdownFileName);
        var text = content ?? string.Empty;
        await AtomicWriteTextAsync(file, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetToBuiltinAsync(
        ProfilePaths paths,
        string skillId,
        string? appBaseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var id = NormalizeSkillId(skillId);
        var builtinPath = Path.Combine(ResolveBuiltinRoot(appBaseDirectory), id);
        if (!Directory.Exists(builtinPath))
        {
            throw new ProfileSkillsException(
                "SKILL_BUILTIN_MISSING",
                "内置库中没有该技能，无法重置。",
                id);
        }

        var target = Path.Combine(paths.SkillsDirectory, id);
        var disabled = Path.Combine(paths.SkillsDisabledDirectory, id);
        if (Directory.Exists(disabled))
        {
            await Task.Run(() => Directory.Delete(disabled, recursive: true), cancellationToken).ConfigureAwait(false);
        }

        if (Directory.Exists(target))
        {
            await Task.Run(() => Directory.Delete(target, recursive: true), cancellationToken).ConfigureAwait(false);
        }

        await Task.Run(() => CopyDirectory(builtinPath, target), cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportFromFolderAsync(
        ProfilePaths paths,
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            throw new ProfileSkillsException(
                "SKILL_IMPORT_INVALID",
                "导入目录不存在。",
                sourceDirectory ?? string.Empty);
        }

        var source = PathUtilities.Normalize(sourceDirectory);
        var skillMd = Path.Combine(source, SkillMarkdownFileName);
        if (!File.Exists(skillMd))
        {
            throw new ProfileSkillsException(
                "SKILL_IMPORT_INVALID",
                "导入目录必须包含 SKILL.md。",
                source);
        }

        var id = NormalizeSkillId(Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (id.Equals(SystemSkillsFolderName, StringComparison.OrdinalIgnoreCase) ||
            id is "." or ".." ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ProfileSkillsException(
                "SKILL_IMPORT_INVALID",
                "技能目录名无效。",
                id);
        }

        Directory.CreateDirectory(paths.SkillsDirectory);
        var target = Path.Combine(paths.SkillsDirectory, id);
        var disabled = Path.Combine(paths.SkillsDisabledDirectory, id);
        if (Directory.Exists(disabled))
        {
            await Task.Run(() => Directory.Delete(disabled, recursive: true), cancellationToken).ConfigureAwait(false);
        }

        if (Directory.Exists(target))
        {
            await Task.Run(() => Directory.Delete(target, recursive: true), cancellationToken).ConfigureAwait(false);
        }

        await Task.Run(() => CopyDirectory(source, target), cancellationToken).ConfigureAwait(false);
    }

    public static SkillFrontmatter ParseFrontmatter(string markdown, string fallbackId)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new SkillFrontmatter(fallbackId, string.Empty);
        }

        var match = FrontmatterBlock.Match(markdown);
        if (!match.Success)
        {
            return new SkillFrontmatter(fallbackId, string.Empty);
        }

        string? name = null;
        string? description = null;
        foreach (Match field in FrontmatterField.Matches(match.Groups[1].Value))
        {
            var key = field.Groups["key"].Value;
            var value = Unquote(field.Groups["value"].Value.Trim());
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        return new SkillFrontmatter(
            string.IsNullOrWhiteSpace(name) ? fallbackId : name.Trim(),
            description?.Trim() ?? string.Empty);
    }

    private static SkillFrontmatter TryReadFrontmatter(string skillMdPath, string fallbackId)
    {
        try
        {
            if (!File.Exists(skillMdPath))
            {
                return new SkillFrontmatter(fallbackId, string.Empty);
            }

            // Only need the header; cap read for huge files.
            using var stream = File.OpenRead(skillMdPath);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sb = new StringBuilder(2048);
            for (var i = 0; i < 80; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                sb.AppendLine(line);
                if (i > 0 && line.Trim() == "---")
                {
                    break;
                }
            }

            return ParseFrontmatter(sb.ToString(), fallbackId);
        }
        catch
        {
            return new SkillFrontmatter(fallbackId, string.Empty);
        }
    }

    private static string? ResolveExistingSkillMarkdown(ProfilePaths paths, string id)
    {
        var enabled = Path.Combine(paths.SkillsDirectory, id, SkillMarkdownFileName);
        if (File.Exists(enabled))
        {
            return enabled;
        }

        var disabled = Path.Combine(paths.SkillsDisabledDirectory, id, SkillMarkdownFileName);
        return File.Exists(disabled) ? disabled : null;
    }

    private static IEnumerable<DirectoryInfo> EnumerateSkillDirs(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (dir.Name.Equals(SystemSkillsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (dir.Name.StartsWith('.'))
            {
                continue;
            }

            if (File.Exists(Path.Combine(dir.FullName, SkillMarkdownFileName)))
            {
                yield return dir;
            }
        }
    }

    private static bool HasAnySkill(string root) => EnumerateSkillDirs(root).Any();

    private static bool IsCustomized(string envRoot, string builtinRoot)
    {
        try
        {
            var envMd = Path.Combine(envRoot, SkillMarkdownFileName);
            var builtinMd = Path.Combine(builtinRoot, SkillMarkdownFileName);
            if (!File.Exists(envMd) || !File.Exists(builtinMd))
            {
                return true;
            }

            return !HashFile(envMd).SequenceEqual(HashFile(builtinMd));
        }
        catch
        {
            return true;
        }
    }

    private static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static string NormalizeSkillId(string skillId)
    {
        var id = (skillId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id) ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.Contains(Path.DirectorySeparatorChar) ||
            id.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ProfileSkillsException(
                "SKILL_ID_INVALID",
                "技能 ID 无效。",
                skillId ?? string.Empty);
        }

        return id;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var source = new DirectoryInfo(sourceDir);
        Directory.CreateDirectory(destinationDir);
        foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
                file.Extension.Equals(".pyc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.DirectoryName is null)
            {
                continue;
            }

            // Skip __pycache__
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.FullName.EndsWith($"{Path.DirectorySeparatorChar}__pycache__", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(source.FullName, file.FullName);
            var target = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            file.CopyTo(target, overwrite: true);
        }
    }

    private static void MoveDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDir)!);
        if (Directory.Exists(destinationDir))
        {
            Directory.Delete(destinationDir, recursive: true);
        }

        Directory.Move(sourceDir, destinationDir);
    }

    private static async Task AtomicWriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("路径无效。");
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
