using System.Text.Json;
using CodexProfileLauncher.Core.Configuration;
using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Validation;

public sealed record ValidationIssue(string Code, string Message, string Details);

public sealed record ProfileValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static ProfileValidationResult Success { get; } = new([]);
}

public sealed class ProfilePathPolicy
{
    private const int DirectoryEntryPreviewLimit = 8;

    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ProfileValidationResult Validate(
        CodexProfile profile,
        IEnumerable<CodexProfile> allProfiles)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            issues.Add(new("PROFILE_NAME_REQUIRED", "请输入环境名称。", "环境名称不能为空。"));
        }
        else if (profile.Name.Trim().Length > 64)
        {
            issues.Add(new("PROFILE_NAME_TOO_LONG", "环境名称过长。", "环境名称最多 64 个字符。"));
        }

        ProfilePaths? paths = null;
        try
        {
            paths = ProfilePaths.FromRoot(profile.DataRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new("PROFILE_PATH_INVALID", "环境数据目录无效。", ex.Message));
        }

        if (paths is null)
        {
            return new(issues);
        }

        if (paths.DataRoot.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new(
                "PROFILE_NETWORK_PATH_UNSUPPORTED",
                "请选择本机磁盘目录。",
                "网络共享无法保证 Chromium 与 SQLite 的锁和凭据权限语义。"));
        }

        if (PathUtilities.IsFileSystemRoot(paths.DataRoot))
        {
            issues.Add(new("PROFILE_DRIVE_ROOT_BLOCKED", "不能使用磁盘根目录。", "请选择磁盘中的专用子目录。"));
        }

        foreach (var blocked in GetBlockedRoots())
        {
            if (PathUtilities.Overlaps(paths.DataRoot, blocked))
            {
                issues.Add(new(
                    "PROFILE_SYSTEM_PATH_BLOCKED",
                    "该目录会与系统或当前 Codex 数据冲突。",
                    $"请选择不与“{blocked}”重合或嵌套的目录。"));
            }
        }

        foreach (var other in allProfiles.Where(item => item.Id != profile.Id))
        {
            try
            {
                if (PathUtilities.Overlaps(paths.DataRoot, other.DataRoot))
                {
                    issues.Add(new(
                        "PROFILE_PATH_CONFLICT",
                        $"该目录与环境“{other.Name}”冲突。",
                        "不同环境的数据目录不能相同，也不能互相嵌套。"));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                issues.Add(new(
                    "PROFILE_EXISTING_PATH_INVALID",
                    $"环境“{other.Name}”的目录无法比较。",
                    ex.Message));
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory))
        {
            try
            {
                _ = PathUtilities.Normalize(profile.WorkingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                issues.Add(new("WORKING_DIRECTORY_INVALID", "默认工作目录无效。", ex.Message));
            }
        }

        return new(issues);
    }

    public static async Task<ProfileValidationResult> PrepareAsync(
        CodexProfile profile,
        IEnumerable<CodexProfile> allProfiles,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(profile, allProfiles);
        if (!validation.IsValid)
        {
            return validation;
        }

        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        var issues = new List<ValidationIssue>();

        try
        {
            var workingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                ? paths.DataRoot
                : PathUtilities.Normalize(profile.WorkingDirectory);

            RejectReparsePoint(paths.DataRoot, "环境数据目录");
            RejectReparsePoint(paths.CodexHome, "Codex 状态目录");
            RejectReparsePoint(paths.AppData, "应用数据目录");
            RejectReparsePoint(workingDirectory, "默认工作目录");
            RejectUnsupportedDataDrive(paths.DataRoot);

            await EnsureMarkerIsCompatibleAsync(profile, paths, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(paths.CodexHome);
            Directory.CreateDirectory(paths.AppData);
            Directory.CreateDirectory(workingDirectory);

            // Re-check every target after creation so a pre-existing child junction or
            // a redirection introduced between validation and creation cannot be trusted.
            RejectReparsePoint(paths.DataRoot, "环境数据目录");
            RejectReparsePoint(paths.CodexHome, "Codex 状态目录");
            RejectReparsePoint(paths.AppData, "应用数据目录");
            RejectReparsePoint(workingDirectory, "默认工作目录");

            await VerifyWritableAsync(paths.DataRoot, cancellationToken).ConfigureAwait(false);
            await VerifyWritableAsync(paths.CodexHome, cancellationToken).ConfigureAwait(false);
            await VerifyWritableAsync(paths.AppData, cancellationToken).ConfigureAwait(false);
        }
        catch (ProfilePathException ex)
        {
            issues.Add(new(ex.Code, ex.Message, ex.Details));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            issues.Add(new("PROFILE_PATH_UNWRITABLE", "环境目录不可写。", ex.Message));
        }

        return new(issues);
    }

    private static IEnumerable<string> GetBlockedRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultCodexHome = Path.Combine(userProfile, ".codex");
        var currentCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        var values = new[]
        {
            defaultCodexHome,
            currentCodexHome,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };

        return values
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(PathUtilities.Normalize);
    }

    private static void RejectReparsePoint(string path, string label)
    {
        var reparsePoint = PathUtilities.FindFirstReparsePoint(path);
        if (reparsePoint is not null)
        {
            throw new ProfilePathException(
                "PROFILE_REPARSE_POINT_UNSUPPORTED",
                $"{label}使用了链接或重定向。",
                $"检测到 reparse/junction：{reparsePoint}。为避免不同环境落到同一位置，请选择普通本机目录。");
        }
    }

    private static void RejectUnsupportedDataDrive(string dataRoot)
    {
        var root = Path.GetPathRoot(PathUtilities.Normalize(dataRoot));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ProfilePathException(
                "PROFILE_PATH_INVALID",
                "环境数据目录无效。",
                $"无法确定路径所在磁盘：{dataRoot}");
        }

        var drive = new DriveInfo(root);
        if (drive.DriveType is DriveType.Network or DriveType.Removable)
        {
            throw new ProfilePathException(
                "PROFILE_DRIVE_TYPE_UNSUPPORTED",
                "请选择固定本机磁盘。",
                "网络盘或移动盘无法保证凭据 ACL 与数据库锁的可靠性。");
        }
    }

    private static async Task EnsureMarkerIsCompatibleAsync(
        CodexProfile profile,
        ProfilePaths paths,
        CancellationToken cancellationToken)
    {
        if (File.Exists(paths.MarkerFile))
        {
            var existingText = await File.ReadAllTextAsync(paths.MarkerFile, cancellationToken).ConfigureAwait(false);
            ProfileMarker? marker;
            try
            {
                marker = JsonSerializer.Deserialize<ProfileMarker>(existingText, MarkerJsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ProfilePathException(
                    "PROFILE_MARKER_INVALID",
                    "环境目录的身份标记已损坏。",
                    ex.Message);
            }

            if (marker is null || marker.ProfileId != profile.Id)
            {
                throw new ProfilePathException(
                    "PROFILE_MARKER_MISMATCH",
                    "该目录属于另一个环境。",
                    "请选择新的空目录，或选择与当前环境身份一致的目录。");
            }

            return;
        }

        if (Directory.Exists(paths.DataRoot) &&
            DescribeBlockingDirectoryEntries(paths.DataRoot) is { } blockingDetails)
        {
            throw new ProfilePathException(
                "PROFILE_DIRECTORY_NOT_EMPTY",
                "所选目录不是空目录。",
                blockingDetails);
        }

        Directory.CreateDirectory(paths.DataRoot);
        var markerText = JsonSerializer.Serialize(
            new ProfileMarker(1, profile.Id, profile.CreatedUtc),
            MarkerJsonOptions);
        await AtomicFile.WriteTextAsync(paths.MarkerFile, markerText, cancellationToken).ConfigureAwait(false);
    }

    private static string? DescribeBlockingDirectoryEntries(string dataRoot)
    {
        var entries = Directory
            .EnumerateFileSystemEntries(dataRoot)
            .Take(DirectoryEntryPreviewLimit + 1)
            .ToArray();
        if (entries.Length == 0)
        {
            return null;
        }

        var descriptions = entries
            .Take(DirectoryEntryPreviewLimit)
            .Select(DescribeFileSystemEntry)
            .ToList();
        if (entries.Length > DirectoryEntryPreviewLimit)
        {
            descriptions.Add("- …（还有更多顶层条目未显示）");
        }

        return string.Join(
            Environment.NewLine,
            [
                "为防止覆盖或在删除环境时误删既有数据，精确环境目录必须为空。",
                $"检测到的顶层条目（最多显示 {DirectoryEntryPreviewLimit} 项）：",
                .. descriptions,
                "Hidden/System 条目可能不会出现在资源管理器的默认视图中。请清理冲突条目或另选存放位置；非空存放位置会使用独占子目录。",
            ]);
    }

    private static string DescribeFileSystemEntry(string entry)
    {
        var name = Path.GetFileName(entry)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        try
        {
            var attributes = File.GetAttributes(entry);
            var kind = attributes.HasFlag(FileAttributes.Directory) ? "目录" : "文件";
            return $"- {name}（{kind}；属性：{attributes}）";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"- {name}（属性读取失败：{ex.GetType().Name}）";
        }
    }

    private static async Task VerifyWritableAsync(string directory, CancellationToken cancellationToken)
    {
        var testFile = Path.Combine(directory, $".write-check-{Guid.NewGuid():N}.tmp");
        try
        {
            await using var stream = new FileStream(
                testFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
    }

    private sealed record ProfileMarker(int SchemaVersion, Guid ProfileId, DateTimeOffset CreatedUtc);
}

public sealed class ProfilePathException(string code, string message, string details) : Exception(message)
{
    public string Code { get; } = code;

    public string Details { get; } = details;
}
