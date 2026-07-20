using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Infrastructure;

/// <summary>
/// Creates an ordinary local-file-system copy of the immutable Store app payload.
/// Package full names are versioned identities, so older mirrors are never pruned implicitly.
/// </summary>
public sealed class CodexRuntimeMirrorManager
{
    internal const string CompletionMarkerFileName = ".codex-runtime-mirror.json";
    internal const long FreeSpaceReserveBytes = 64L * 1024 * 1024;
    private const int MarkerSchemaVersion = 2;
    private const long MaximumMarkerBytes = 64L * 1024 * 1024;
    private const string RuntimeMutableDictionaryDirectory = "Dictionaries";
    private const long MaximumRuntimeDictionaryFileBytes = 64L * 1024 * 1024;
    private const long MaximumRuntimeDictionaryDirectoryBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly string[] CriticalRelativePaths =
    [
        "ChatGPT.exe",
        "chrome.dll",
        "resources/app.asar",
        "resources/codex.exe",
    ];

    private readonly string _runtimeCacheDirectory;
    private readonly Func<string, long> _availableSpaceProvider;
    private readonly TimeSpan _lockTimeout;
    private readonly Action<string>? _beforeCopyFile;

    public CodexRuntimeMirrorManager(LauncherPaths paths)
        : this(paths?.RuntimeCacheDirectory ?? throw new ArgumentNullException(nameof(paths)))
    {
    }

    internal CodexRuntimeMirrorManager(
        string runtimeCacheDirectory,
        Func<string, long>? availableSpaceProvider = null,
        TimeSpan? lockTimeout = null,
        Action<string>? beforeCopyFile = null)
    {
        _runtimeCacheDirectory = PathUtilities.Normalize(runtimeCacheDirectory);
        _availableSpaceProvider = availableSpaceProvider ?? GetAvailableFreeSpace;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
        _beforeCopyFile = beforeCopyFile;
        if (_lockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }
    }

    public Task<CodexInstallation> EnsureMirrorAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => EnsureMirror(installation, cancellationToken), CancellationToken.None);
    }

    internal static string BuildSafePackageDirectoryName(string packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            throw new ArgumentException("包完整名称不能为空。", nameof(packageFullName));
        }

        var value = packageFullName.Trim();
        var readable = new StringBuilder(Math.Min(value.Length, 96));
        var replacing = false;
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                readable.Append(character);
                replacing = false;
            }
            else if (!replacing)
            {
                readable.Append('_');
                replacing = true;
            }

            if (readable.Length >= 96)
            {
                break;
            }
        }

        var prefix = readable.ToString().Trim(' ', '.', '_');
        if (prefix.Length == 0)
        {
            prefix = "package";
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
        return $"{prefix}-{hash}";
    }

    private CodexInstallation EnsureMirror(
        CodexInstallation installation,
        CancellationToken cancellationToken)
    {
        string? stagingDirectory = null;
        try
        {
            var source = CaptureSourceSnapshot(installation, cancellationToken);
            if (PathUtilities.Overlaps(source.RootDirectory, _runtimeCacheDirectory))
            {
                throw Error(
                    "RUNTIME_MIRROR_PATH_OVERLAP",
                    "Codex 运行副本目录与 Store 源目录重叠。",
                    $"Source={source.RootDirectory} | Cache={_runtimeCacheDirectory}");
            }

            var packageDirectory = Path.Combine(
                _runtimeCacheDirectory,
                BuildSafePackageDirectoryName(installation.PackageFullName));
            var finalAppDirectory = Path.Combine(packageDirectory, "app");
            EnsureNestedCachePath(packageDirectory);
            EnsureNestedCachePath(finalAppDirectory);

            Directory.CreateDirectory(_runtimeCacheDirectory);
            RejectReparsePoint(_runtimeCacheDirectory, "RUNTIME_MIRROR_CACHE_REPARSE_POINT");
            Directory.CreateDirectory(packageDirectory);
            RejectCachePathReparsePoints(packageDirectory);

            using var mirrorLock = AcquirePackageLock(packageDirectory, cancellationToken);
            RejectCachePathReparsePoints(packageDirectory);
            var maintenanceFailure = CleanupAbandonedArtifacts(packageDirectory);
            // A package full name identifies an immutable, versioned WindowsApps directory.
            // The pre-lock snapshot therefore remains authoritative while callers serialize cache publication.
            if (TryValidatePublishedMirror(
                    installation,
                    source,
                    finalAppDirectory,
                    cancellationToken,
                    out _))
            {
                return MirrorInstallation(installation, source, finalAppDirectory);
            }

            EnsureFreeSpace(packageDirectory, source.TotalBytes, maintenanceFailure);
            stagingDirectory = Path.Combine(
                packageDirectory,
                $".app.staging-{Environment.ProcessId}-{Guid.NewGuid():N}");
            EnsureNestedCachePath(stagingDirectory);
            Directory.CreateDirectory(stagingDirectory);
            RejectCachePathReparsePoints(stagingDirectory);

            CopyCompleteTree(source, stagingDirectory, cancellationToken);
            ValidateCopiedTree(source, stagingDirectory, cancellationToken);
            WriteCompletionMarkerLast(installation, source, stagingDirectory, cancellationToken);
            PublishStaging(stagingDirectory, finalAppDirectory);
            stagingDirectory = null;

            if (!TryValidatePublishedMirror(
                    installation,
                    source,
                    finalAppDirectory,
                    cancellationToken,
                    out var failure))
            {
                throw Error(
                    "RUNTIME_MIRROR_POST_PUBLISH_INVALID",
                    "Codex 运行副本发布后校验失败。",
                    $"Target={finalAppDirectory} | Reason={failure}");
            }

            return MirrorInstallation(installation, source, finalAppDirectory);
        }
        catch (OperationCanceledException)
        {
            var cleanup = TryDeleteStaging(stagingDirectory);
            if (cleanup is null)
            {
                throw;
            }

            throw Error(
                "RUNTIME_MIRROR_CANCELLED_CLEANUP_FAILED",
                "Codex 运行副本创建已取消，但临时目录清理失败。",
                cleanup);
        }
        catch (CodexRuntimeMirrorException exception)
        {
            var cleanup = TryDeleteStaging(stagingDirectory);
            if (cleanup is null)
            {
                throw;
            }

            throw Error(
                exception.Code,
                exception.Message,
                $"{exception.Details} | StagingCleanupFailed={cleanup}",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            var cleanup = TryDeleteStaging(stagingDirectory);
            throw Error(
                "RUNTIME_MIRROR_UNEXPECTED_IO_FAILURE",
                "创建 Codex 运行副本时发生文件系统错误。",
                $"Cache={_runtimeCacheDirectory} | Error={exception.Message}" +
                (cleanup is null ? string.Empty : $" | StagingCleanupFailed={cleanup}"),
                exception);
        }
    }

    private SourceSnapshot CaptureSourceSnapshot(
        CodexInstallation installation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installation.PackageFullName) ||
            string.IsNullOrWhiteSpace(installation.PackageFamilyName) ||
            installation.Version is null ||
            string.IsNullOrWhiteSpace(installation.ExecutablePath))
        {
            throw Error(
                "RUNTIME_MIRROR_INSTALLATION_INVALID",
                "Codex 安装信息不完整，无法创建运行副本。",
                "PackageFullName、PackageFamilyName、Version 与 ExecutablePath 必须完整。");
        }

        string executablePath;
        try
        {
            executablePath = PathUtilities.Normalize(installation.ExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw Error(
                "RUNTIME_MIRROR_SOURCE_PATH_INVALID",
                "Codex Store 程序路径无效。",
                $"Executable={installation.ExecutablePath} | Error={exception.Message}",
                exception);
        }

        if (!File.Exists(executablePath))
        {
            throw Error(
                "RUNTIME_MIRROR_SOURCE_EXECUTABLE_MISSING",
                "未找到 Codex Store 程序文件。",
                $"Executable={executablePath}");
        }

        var sourceRoot = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            throw Error(
                "RUNTIME_MIRROR_SOURCE_PATH_INVALID",
                "无法确定 Codex Store app 目录。",
                $"Executable={executablePath}");
        }

        sourceRoot = PathUtilities.Normalize(sourceRoot);
        var executableRelativePath = NormalizeRelativePath(
            Path.GetRelativePath(sourceRoot, executablePath));
        if (!executableRelativePath.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw Error(
                "RUNTIME_MIRROR_SOURCE_EXECUTABLE_UNEXPECTED",
                "Codex Store 程序入口不是预期的 ChatGPT.exe。",
                $"Executable={executablePath} | Relative={executableRelativePath}");
        }

        RejectReparsePoint(sourceRoot, "RUNTIME_MIRROR_SOURCE_REPARSE_POINT");
        var directories = new List<string>();
        var files = new List<SourceFile>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        long totalBytes = 0;

        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        throw Error(
                            "RUNTIME_MIRROR_SOURCE_REPARSE_POINT",
                            "Codex Store app 目录包含不受支持的重解析点。",
                            $"Entry={entry.FullName}");
                    }

                    var relativePath = NormalizeRelativePath(
                        Path.GetRelativePath(sourceRoot, entry.FullName));
                    if (entry is DirectoryInfo)
                    {
                        directories.Add(relativePath);
                        pending.Push(entry.FullName);
                    }
                    else if (entry is FileInfo file)
                    {
                        totalBytes = checked(totalBytes + file.Length);
                        files.Add(new SourceFile(
                            relativePath,
                            file.FullName,
                            file.Length,
                            file.LastWriteTimeUtc,
                            null));
                    }
                    else
                    {
                        throw Error(
                            "RUNTIME_MIRROR_SOURCE_ENTRY_UNSUPPORTED",
                            "Codex Store app 目录包含不受支持的文件系统条目。",
                            $"Entry={entry.FullName}");
                    }
                }
            }
        }
        catch (CodexRuntimeMirrorException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            throw Error(
                "RUNTIME_MIRROR_SOURCE_READ_FAILED",
                "无法完整读取 Codex Store app 目录。",
                $"Source={sourceRoot} | Error={exception.Message}",
                exception);
        }

        files.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        directories.Sort(StringComparer.OrdinalIgnoreCase);
        var lookup = files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var criticalPath in CriticalRelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lookup.TryGetValue(criticalPath, out var criticalFile))
            {
                throw Error(
                    "RUNTIME_MIRROR_CRITICAL_FILE_MISSING",
                    "Codex Store app 缺少创建运行副本所需的关键文件。",
                    $"Source={sourceRoot} | Missing={criticalPath}");
            }

            var withHash = criticalFile with
            {
                Sha256 = ComputeSha256(criticalFile.FullPath, cancellationToken),
            };
            files[files.IndexOf(criticalFile)] = withHash;
            lookup[criticalPath] = withHash;
        }

        return new SourceSnapshot(
            sourceRoot,
            executableRelativePath,
            directories,
            files,
            totalBytes);
    }
    private void CopyCompleteTree(
        SourceSnapshot source,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var relativeDirectory in source.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(ResolveNestedPath(stagingDirectory, relativeDirectory));
            }

            foreach (var file in source.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _beforeCopyFile?.Invoke(file.RelativePath);
                var destination = ResolveNestedPath(stagingDirectory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file.FullPath, destination, overwrite: false);
                File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Error(
                "RUNTIME_MIRROR_COPY_FAILED",
                "复制 Codex Store app 文件失败。",
                $"Source={source.RootDirectory} | Staging={stagingDirectory} | Error={exception.Message}",
                exception);
        }
    }

    private static void ValidateCopiedTree(
        SourceSnapshot source,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var actual = CaptureDestinationTree(source, destinationRoot, cancellationToken);
        if (!TreeShapeMatches(source, actual, out var failure))
        {
            throw Error(
                "RUNTIME_MIRROR_TREE_MISMATCH",
                "Codex 运行副本文件树不完整。",
                $"Target={destinationRoot} | Reason={failure}");
        }

        foreach (var critical in source.Files.Where(file => file.Sha256 is not null))
        {
            var destination = ResolveNestedPath(destinationRoot, critical.RelativePath);
            var actualHash = ComputeSha256(destination, cancellationToken);
            if (!actualHash.Equals(critical.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Error(
                    "RUNTIME_MIRROR_CRITICAL_HASH_MISMATCH",
                    "Codex 运行副本关键文件校验失败。",
                    $"File={critical.RelativePath} | Expected={critical.Sha256} | Actual={actualHash}");
            }
        }
    }

    private static void WriteCompletionMarkerLast(
        CodexInstallation installation,
        SourceSnapshot source,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var marker = new RuntimeMirrorMarker
        {
            SchemaVersion = MarkerSchemaVersion,
            PackageFullName = installation.PackageFullName,
            PackageFamilyName = installation.PackageFamilyName,
            Version = installation.Version.ToString(),
            SourceDirectory = source.RootDirectory,
            ExecutableRelativePath = source.ExecutableRelativePath,
            TotalBytes = source.TotalBytes,
            Directories = source.Directories.ToArray(),
            Files = source.Files.Select(file => new RuntimeMirrorFile
            {
                RelativePath = file.RelativePath,
                Length = file.Length,
                LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks,
                Sha256 = file.Sha256,
            }).ToArray(),
            CompletedUtc = DateTimeOffset.UtcNow,
        };

        var markerPath = Path.Combine(stagingDirectory, CompletionMarkerFileName);
        try
        {
            using var stream = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough);
            JsonSerializer.Serialize(stream, marker, MarkerJsonOptions);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw Error(
                "RUNTIME_MIRROR_MARKER_WRITE_FAILED",
                "无法写入 Codex 运行副本完成标记。",
                $"Marker={markerPath} | Error={exception.Message}",
                exception);
        }
    }

    private static bool TryValidatePublishedMirror(
        CodexInstallation installation,
        SourceSnapshot source,
        string finalAppDirectory,
        CancellationToken cancellationToken,
        out string failure)
    {
        failure = string.Empty;
        if (!Directory.Exists(finalAppDirectory))
        {
            failure = "缓存 app 目录不存在。";
            return false;
        }

        try
        {
            RejectReparsePoint(finalAppDirectory, "RUNTIME_MIRROR_CACHE_REPARSE_POINT");
            var markerPath = Path.Combine(finalAppDirectory, CompletionMarkerFileName);
            if (!File.Exists(markerPath))
            {
                failure = "完成标记不存在。";
                return false;
            }

            var markerLength = new FileInfo(markerPath).Length;
            if (markerLength <= 0 || markerLength > MaximumMarkerBytes)
            {
                failure = $"完成标记大小无效：{markerLength}。";
                return false;
            }

            RuntimeMirrorMarker? marker;
            using (var stream = new FileStream(
                       markerPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                marker = JsonSerializer.Deserialize<RuntimeMirrorMarker>(stream, MarkerJsonOptions);
            }

            if (marker is null ||
                marker.SchemaVersion != MarkerSchemaVersion ||
                string.IsNullOrWhiteSpace(marker.SourceDirectory) ||
                string.IsNullOrWhiteSpace(marker.ExecutableRelativePath) ||
                !string.Equals(marker.PackageFullName, installation.PackageFullName, StringComparison.Ordinal) ||
                !string.Equals(marker.PackageFamilyName, installation.PackageFamilyName, StringComparison.Ordinal) ||
                !string.Equals(marker.Version, installation.Version.ToString(), StringComparison.Ordinal) ||
                !PathsEqual(marker.SourceDirectory, source.RootDirectory) ||
                !string.Equals(
                    marker.ExecutableRelativePath,
                    source.ExecutableRelativePath,
                    StringComparison.OrdinalIgnoreCase) ||
                marker.TotalBytes != source.TotalBytes)
            {
                failure = "完成标记与当前 Store 包身份不一致。";
                return false;
            }

            if (!MarkerMatchesSource(marker, source, out failure))
            {
                return false;
            }

            var actual = CaptureDestinationTree(source, finalAppDirectory, cancellationToken);
            if (!TreeShapeMatches(source, actual, out failure))
            {
                return false;
            }

            foreach (var critical in source.Files.Where(file => file.Sha256 is not null))
            {
                var destination = ResolveNestedPath(finalAppDirectory, critical.RelativePath);
                if (!ComputeSha256(destination, cancellationToken)
                    .Equals(critical.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    failure = $"关键文件哈希不一致：{critical.RelativePath}。";
                    return false;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            CryptographicException or ArgumentException or NotSupportedException or
            OverflowException or CodexRuntimeMirrorException)
        {
            failure = exception is CodexRuntimeMirrorException mirrorException
                ? $"{mirrorException.Code}: {mirrorException.Details}"
                : exception.Message;
            return false;
        }
    }

    private static bool MarkerMatchesSource(
        RuntimeMirrorMarker marker,
        SourceSnapshot source,
        out string failure)
    {
        failure = string.Empty;
        if (marker.Directories is null || marker.Files is null ||
            marker.Directories.Length != source.Directories.Count ||
            marker.Files.Length != source.Files.Count ||
            marker.Directories.Any(string.IsNullOrWhiteSpace) ||
            marker.Files.Any(file =>
                file is null ||
                string.IsNullOrWhiteSpace(file.RelativePath) ||
                file.Length < 0 ||
                file.LastWriteTimeUtcTicks <= 0 ||
                (file.Sha256 is not null &&
                 (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)))))
        {
            failure = "完成标记中的文件树字段缺失、数量不一致或内容无效。";
            return false;
        }

        HashSet<string> markerDirectories;
        Dictionary<string, RuntimeMirrorFile> markerFiles;
        try
        {
            markerDirectories = new HashSet<string>(
                marker.Directories.Select(NormalizeRelativePath),
                StringComparer.OrdinalIgnoreCase);
            markerFiles = marker.Files
                .Select(file => file!)
                .ToDictionary(
                    file => NormalizeRelativePath(file.RelativePath),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or CodexRuntimeMirrorException)
        {
            failure = $"完成标记路径无效：{exception.Message}";
            return false;
        }

        if (!markerDirectories.SetEquals(source.Directories))
        {
            failure = "完成标记中的目录清单不一致。";
            return false;
        }

        foreach (var sourceFile in source.Files)
        {
            if (!markerFiles.TryGetValue(sourceFile.RelativePath, out var markerFile) ||
                markerFile.Length != sourceFile.Length ||
                markerFile.LastWriteTimeUtcTicks != sourceFile.LastWriteTimeUtc.Ticks ||
                !string.Equals(markerFile.Sha256, sourceFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                failure = $"完成标记中的文件记录不一致：{sourceFile.RelativePath}。";
                return false;
            }
        }

        return true;
    }

    private static DestinationSnapshot CaptureDestinationTree(
        SourceSnapshot source,
        string root,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        var files = new List<DestinationFile>();
        var sourceContainsDictionaryDirectory = source.Directories.Any(path =>
            path.Equals(RuntimeMutableDictionaryDirectory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(
                RuntimeMutableDictionaryDirectory + "/",
                StringComparison.OrdinalIgnoreCase));
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Error(
                        "RUNTIME_MIRROR_CACHE_REPARSE_POINT",
                        "Codex 运行副本包含不受支持的重解析点。",
                        $"Entry={entry.FullName}");
                }

                var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, entry.FullName));
                if (entry is DirectoryInfo)
                {
                    if (!sourceContainsDictionaryDirectory &&
                        relativePath.Equals(
                            RuntimeMutableDictionaryDirectory,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        ValidateRuntimeMutableDictionaryDirectory(
                            entry.FullName,
                            cancellationToken);
                        continue;
                    }

                    directories.Add(relativePath);
                    pending.Push(entry.FullName);
                }
                else if (entry is FileInfo file &&
                         !relativePath.Equals(
                             CompletionMarkerFileName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(new DestinationFile(
                        relativePath,
                        file.Length,
                        file.LastWriteTimeUtc.Ticks));
                }
            }
        }

        return new DestinationSnapshot(directories, files);
    }

    private static void ValidateRuntimeMutableDictionaryDirectory(
        string dictionaryRoot,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(dictionaryRoot);
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                throw Error(
                    "RUNTIME_MIRROR_MUTABLE_DICTIONARY_REPARSE_POINT",
                    "Codex 运行副本的字典数据目录包含重解析点。",
                    $"Entry={directory}");
            }

            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Error(
                        "RUNTIME_MIRROR_MUTABLE_DICTIONARY_REPARSE_POINT",
                        "Codex 运行副本的字典数据目录包含重解析点。",
                        $"Entry={entry.FullName}");
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory.FullName);
                    continue;
                }

                if (entry is not FileInfo file ||
                    !file.Extension.Equals(".bdic", StringComparison.OrdinalIgnoreCase) ||
                    file.Length < 0 ||
                    file.Length > MaximumRuntimeDictionaryFileBytes)
                {
                    throw Error(
                        "RUNTIME_MIRROR_MUTABLE_DICTIONARY_ENTRY_UNEXPECTED",
                        "Codex 运行副本的字典数据目录包含非字典文件。",
                        $"Entry={entry.FullName}");
                }

                totalBytes = checked(totalBytes + file.Length);
                if (totalBytes > MaximumRuntimeDictionaryDirectoryBytes)
                {
                    throw Error(
                        "RUNTIME_MIRROR_MUTABLE_DICTIONARY_TOO_LARGE",
                        "Codex 运行副本的字典数据目录超过允许大小。",
                        $"Directory={dictionaryRoot} | TotalBytes={totalBytes}");
                }
            }
        }
    }

    private static bool TreeShapeMatches(
        SourceSnapshot source,
        DestinationSnapshot actual,
        out string failure)
    {
        failure = string.Empty;
        var expectedDirectories = new HashSet<string>(
            source.Directories,
            StringComparer.OrdinalIgnoreCase);
        if (!expectedDirectories.SetEquals(actual.Directories))
        {
            failure = "目录清单与 Store 源目录不一致。";
            return false;
        }

        if (source.Files.Count != actual.Files.Count)
        {
            failure = $"文件数量不一致（源={source.Files.Count}，副本={actual.Files.Count}）。";
            return false;
        }

        Dictionary<string, DestinationFile> actualFiles;
        try
        {
            actualFiles = actual.Files.ToDictionary(
                file => file.RelativePath,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            failure = "副本包含大小写冲突的重复文件路径。";
            return false;
        }

        foreach (var expected in source.Files)
        {
            if (!actualFiles.TryGetValue(expected.RelativePath, out var actualFile) ||
                actualFile.Length != expected.Length ||
                actualFile.LastWriteTimeUtcTicks != expected.LastWriteTimeUtc.Ticks)
            {
                failure = $"文件缺失或元数据不一致：{expected.RelativePath}。";
                return false;
            }
        }

        return true;
    }
    private void EnsureFreeSpace(
        string packageDirectory,
        long sourceBytes,
        string? maintenanceFailure)
    {
        long availableBytes;
        try
        {
            availableBytes = _availableSpaceProvider(packageDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw Error(
                "RUNTIME_MIRROR_DISK_QUERY_FAILED",
                "无法读取 Codex 运行副本磁盘的可用空间。",
                $"Cache={packageDirectory} | Error={exception.Message}",
                exception);
        }

        var requiredBytes = sourceBytes > long.MaxValue - FreeSpaceReserveBytes
            ? long.MaxValue
            : sourceBytes + FreeSpaceReserveBytes;
        if (availableBytes < requiredBytes)
        {
            throw Error(
                "RUNTIME_MIRROR_DISK_SPACE_INSUFFICIENT",
                "磁盘可用空间不足，无法创建 Codex 运行副本。",
                $"Cache={packageDirectory} | RequiredBytes={requiredBytes} | " +
                $"AvailableBytes={availableBytes} | PayloadBytes={sourceBytes}" +
                (maintenanceFailure is null
                    ? string.Empty
                    : $" | AbandonedCacheCleanupFailed={maintenanceFailure}"));
        }
    }

    private PackageLock AcquirePackageLock(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        var identity = Path.GetFullPath(packageDirectory).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var currentUserSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw Error(
                "RUNTIME_MIRROR_USER_IDENTITY_UNAVAILABLE",
                "无法读取当前 Windows 用户身份，不能安全锁定运行副本。",
                $"Cache={packageDirectory}");
        var mutexName =
            $@"Global\CodexProfileLauncher.RuntimeMirror.v1.{currentUserSid}.{hash}";
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            throw Error(
                "RUNTIME_MIRROR_LOCK_CREATE_FAILED",
                "无法创建 Codex 运行副本跨进程锁。",
                $"Cache={packageDirectory} | Error={exception.Message}",
                exception);
        }

        var deadline = DateTime.UtcNow + _lockTimeout;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw Error(
                        "RUNTIME_MIRROR_LOCK_TIMEOUT",
                        "等待其他启动器完成 Codex 运行副本时超时。",
                        $"Cache={packageDirectory} | TimeoutMs={(long)_lockTimeout.TotalMilliseconds}");
                }

                try
                {
                    if (mutex.WaitOne(remaining < TimeSpan.FromMilliseconds(100)
                            ? remaining
                            : TimeSpan.FromMilliseconds(100)))
                    {
                        return new PackageLock(mutex);
                    }
                }
                catch (AbandonedMutexException)
                {
                    return new PackageLock(mutex);
                }
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private string? CleanupAbandonedArtifacts(string packageDirectory)
    {
        var failures = new List<string>();
        try
        {
            RejectCachePathReparsePoints(packageDirectory);
            foreach (var directory in Directory.EnumerateDirectories(
                         packageDirectory,
                         ".app.*-*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if (!IsManagedTemporaryDirectoryName(name))
                {
                    continue;
                }

                var failure = TryDeleteCacheDirectory(directory);
                if (failure is not null)
                {
                    failures.Add(failure);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or CodexRuntimeMirrorException)
        {
            failures.Add($"Package={packageDirectory} | Error={exception.Message}");
        }

        return failures.Count == 0
            ? null
            : string.Join(" || ", failures.Take(4));
    }

    private static bool IsManagedTemporaryDirectoryName(string name)
    {
        const string stagingPrefix = ".app.staging-";
        const string replacedPrefix = ".app.replaced-";
        var prefix = name.StartsWith(stagingPrefix, StringComparison.Ordinal)
            ? stagingPrefix
            : name.StartsWith(replacedPrefix, StringComparison.Ordinal)
                ? replacedPrefix
                : null;
        if (prefix is null)
        {
            return false;
        }

        var identity = name[prefix.Length..];
        var separator = identity.IndexOf('-');
        return separator > 0 &&
               int.TryParse(identity[..separator], out var processId) &&
               processId > 0 &&
               Guid.TryParseExact(identity[(separator + 1)..], "N", out _);
    }

    private void PublishStaging(string stagingDirectory, string finalAppDirectory)
    {
        RejectCachePathReparsePoints(stagingDirectory);
        if (File.Exists(finalAppDirectory))
        {
            throw Error(
                "RUNTIME_MIRROR_TARGET_BLOCKED",
                "Codex 运行副本目标被同名文件占用。",
                $"Target={finalAppDirectory}");
        }

        string? displacedDirectory = null;
        try
        {
            if (Directory.Exists(finalAppDirectory))
            {
                displacedDirectory = Path.Combine(
                    Path.GetDirectoryName(finalAppDirectory)!,
                    $".app.replaced-{Environment.ProcessId}-{Guid.NewGuid():N}");
                EnsureNestedCachePath(displacedDirectory);
                RejectCachePathReparsePoints(finalAppDirectory);
                Directory.Move(finalAppDirectory, displacedDirectory);
                RejectCachePathReparsePoints(displacedDirectory);
            }

            RejectCachePathReparsePoints(stagingDirectory);
            Directory.Move(stagingDirectory, finalAppDirectory);
            RejectCachePathReparsePoints(finalAppDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            string? restoreFailure = null;
            if (displacedDirectory is not null &&
                Directory.Exists(displacedDirectory) &&
                !Directory.Exists(finalAppDirectory))
            {
                try
                {
                    Directory.Move(displacedDirectory, finalAppDirectory);
                    displacedDirectory = null;
                }
                catch (Exception restoreException) when (
                    restoreException is IOException or UnauthorizedAccessException)
                {
                    restoreFailure = restoreException.Message;
                }
            }

            throw Error(
                "RUNTIME_MIRROR_PUBLISH_FAILED",
                "无法原子发布 Codex 运行副本。",
                $"Staging={stagingDirectory} | Target={finalAppDirectory} | Error={exception.Message}" +
                (restoreFailure is null ? string.Empty : $" | RestoreFailed={restoreFailure}"),
                exception);
        }

        if (displacedDirectory is not null)
        {
            // The new mirror is already atomically visible. Failure to remove a damaged predecessor
            // is non-fatal and never causes an older package version to be deleted.
            _ = TryDeleteCacheDirectory(displacedDirectory);
        }
    }

    private static CodexInstallation MirrorInstallation(
        CodexInstallation installation,
        SourceSnapshot source,
        string finalAppDirectory) => installation with
    {
        ExecutablePath = ResolveNestedPath(finalAppDirectory, source.ExecutableRelativePath),
    };

    private string? TryDeleteStaging(string? stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Directory.Exists(stagingDirectory))
        {
            return null;
        }

        return TryDeleteCacheDirectory(stagingDirectory);
    }

    private string? TryDeleteCacheDirectory(string directory)
    {
        try
        {
            EnsureNestedCachePath(directory);
            if (!Directory.Exists(directory))
            {
                return null;
            }

            RejectCachePathReparsePoints(directory);
            var pending = new Stack<string>();
            var directories = new List<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var currentAttributes = File.GetAttributes(current);
                if (currentAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return $"RefusingToDeleteReparsePoint={current}";
                }

                directories.Add(current);
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return $"RefusingToDeleteReparsePoint={entry}";
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        File.SetAttributes(entry, FileAttributes.Normal);
                        File.Delete(entry);
                    }
                }
            }

            for (var index = directories.Count - 1; index >= 0; index--)
            {
                Directory.Delete(directories[index], recursive: false);
            }

            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or CodexRuntimeMirrorException)
        {
            return $"Directory={directory} | Error={exception.Message}";
        }
    }

    private void EnsureNestedCachePath(string candidate)
    {
        if (!PathUtilities.IsSameOrNested(candidate, _runtimeCacheDirectory) ||
            PathsEqual(candidate, _runtimeCacheDirectory))
        {
            throw Error(
                "RUNTIME_MIRROR_CACHE_BOUNDARY_VIOLATION",
                "Codex 运行副本路径越出缓存根目录。",
                $"Cache={_runtimeCacheDirectory} | Candidate={candidate}");
        }
    }

    private void RejectCachePathReparsePoints(string candidate)
    {
        EnsureNestedCachePath(candidate);
        RejectReparsePoint(_runtimeCacheDirectory, "RUNTIME_MIRROR_CACHE_REPARSE_POINT");

        var relative = Path.GetRelativePath(_runtimeCacheDirectory, candidate);
        var current = _runtimeCacheDirectory;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectReparsePoint(current, "RUNTIME_MIRROR_CACHE_REPARSE_POINT");
            }
        }
    }

    private static string ResolveNestedPath(string root, string relativePath)
    {
        var normalizedRelative = NormalizeRelativePath(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathUtilities.IsSameOrNested(candidate, root) || PathsEqual(candidate, root))
        {
            throw Error(
                "RUNTIME_MIRROR_RELATIVE_PATH_INVALID",
                "运行副本文件路径越出目标目录。",
                $"Root={root} | Relative={relativePath}");
        }

        return candidate;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw Error(
                "RUNTIME_MIRROR_RELATIVE_PATH_INVALID",
                "运行副本包含无效的相对路径。",
                $"Relative={relativePath}");
        }

        var normalized = relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Trim('/');
        if (normalized.Length == 0 ||
            normalized.Equals("..", StringComparison.Ordinal) ||
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            normalized.Contains("/../", StringComparison.Ordinal))
        {
            throw Error(
                "RUNTIME_MIRROR_RELATIVE_PATH_INVALID",
                "运行副本包含越界相对路径。",
                $"Relative={relativePath}");
        }

        return normalized;
    }

    private static void RejectReparsePoint(string path, string code)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw Error(
                    code,
                    "Codex 运行副本流程不接受重解析点目录。",
                    $"Path={path}");
            }
        }
        catch (CodexRuntimeMirrorException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw Error(
                code,
                "无法验证 Codex 运行副本目录边界。",
                $"Path={path} | Error={exception.Message}",
                exception);
        }
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static long GetAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException($"无法确定缓存路径所在磁盘：{path}");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static bool PathsEqual(string first, string second) =>
        PathUtilities.Normalize(first).Equals(
            PathUtilities.Normalize(second),
            StringComparison.OrdinalIgnoreCase);

    private static CodexRuntimeMirrorException Error(
        string code,
        string message,
        string details,
        Exception? innerException = null) =>
        new(code, message, details, innerException);

    private sealed class PackageLock : IDisposable
    {
        private Mutex? _mutex;

        public PackageLock(Mutex mutex)
        {
            _mutex = mutex;
        }

        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is null)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    private sealed record SourceSnapshot(
        string RootDirectory,
        string ExecutableRelativePath,
        IReadOnlyList<string> Directories,
        IReadOnlyList<SourceFile> Files,
        long TotalBytes);

    private sealed record SourceFile(
        string RelativePath,
        string FullPath,
        long Length,
        DateTime LastWriteTimeUtc,
        string? Sha256);

    private sealed record DestinationSnapshot(
        IReadOnlyList<string> Directories,
        IReadOnlyList<DestinationFile> Files);

    private sealed record DestinationFile(
        string RelativePath,
        long Length,
        long LastWriteTimeUtcTicks);

    private sealed class RuntimeMirrorMarker
    {
        public int SchemaVersion { get; set; }
        public string PackageFullName { get; set; } = string.Empty;
        public string PackageFamilyName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string SourceDirectory { get; set; } = string.Empty;
        public string ExecutableRelativePath { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public string[]? Directories { get; set; }
        public RuntimeMirrorFile?[]? Files { get; set; }
        public DateTimeOffset CompletedUtc { get; set; }
    }

    private sealed class RuntimeMirrorFile
    {
        public string RelativePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public string? Sha256 { get; set; }
    }
}

public sealed class CodexRuntimeMirrorException : Exception
{
    public CodexRuntimeMirrorException(
        string code,
        string message,
        string details,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }
    public string Details { get; }
}