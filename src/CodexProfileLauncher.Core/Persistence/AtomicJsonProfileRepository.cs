using System.Text.Json;
using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Persistence;

public interface IProfileRepository
{
    Task<LauncherState> LoadAsync(CancellationToken cancellationToken = default);

    Task<LauncherState> SaveAsync(
        LauncherState state,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed class AtomicJsonProfileRepository : IProfileRepository
{
    private readonly string _stateFile;
    private readonly string _backupFile;
    private readonly string _lockFile;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AtomicJsonProfileRepository(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _stateFile = Path.Combine(stateDirectory, "profiles.json");
        _backupFile = _stateFile + ".bak";
        _lockFile = Path.Combine(stateDirectory, "profiles.lock");
    }

    public async Task<LauncherState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var operationLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LauncherState> SaveAsync(
        LauncherState state,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var operationLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);

        var current = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision)
        {
            throw new ProfileStoreException(
                "STORE_REVISION_CONFLICT",
                "环境列表已被另一进程修改。",
                $"预期版本 {expectedRevision}，当前版本 {current.Revision}。请重新加载后再试。");
        }

        if (state.SchemaVersion != LauncherState.CurrentSchemaVersion)
        {
            throw new ProfileStoreException(
                "STORE_SCHEMA_UNSUPPORTED",
                "启动器状态版本不受支持。",
                $"当前实现只支持 schemaVersion={LauncherState.CurrentSchemaVersion}。");
        }

        ValidateStateSemantics(state, "待保存状态");

        var stateToPersist = new LauncherState
        {
            SchemaVersion = state.SchemaVersion,
            Revision = expectedRevision + 1,
            SelectedProfileId = state.SelectedProfileId,
            Profiles = state.Profiles,
        };
        ValidateStateSemantics(stateToPersist, "待写入状态");
        var tempFile = Path.Combine(
            Path.GetDirectoryName(_stateFile)!,
            $".profiles.{Guid.NewGuid():N}.tmp");

        try
        {
            await WriteJsonFileAsync(tempFile, stateToPersist, cancellationToken).ConfigureAwait(false);
            _ = await ReadJsonFileAsync(tempFile, cancellationToken).ConfigureAwait(false);

            if (File.Exists(_stateFile))
            {
                File.Replace(tempFile, _stateFile, _backupFile, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempFile, _stateFile);
            }

            var verified = await ReadJsonFileAsync(_stateFile, cancellationToken).ConfigureAwait(false);
            if (verified.Revision != stateToPersist.Revision)
            {
                throw new ProfileStoreException(
                    "STORE_VERIFY_FAILED",
                    "环境列表写入后校验失败。",
                    "磁盘中的 revision 与预期值不一致。");
            }

            return verified;
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private async Task<LauncherState> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFile))
        {
            return new LauncherState();
        }

        try
        {
            return await ReadJsonFileAsync(_stateFile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            var backupAvailable = false;
            if (File.Exists(_backupFile))
            {
                try
                {
                    _ = await ReadJsonFileAsync(_backupFile, cancellationToken).ConfigureAwait(false);
                    backupAvailable = true;
                }
                catch (Exception backupException) when (
                    backupException is JsonException or IOException or ProfileStoreException)
                {
                    backupAvailable = false;
                }
            }

            throw new ProfileStoreException(
                "STORE_CORRUPT",
                "环境列表文件已损坏。",
                backupAvailable
                    ? $"主文件无法读取，但存在可用备份：{_backupFile}。原始错误：{ex.Message}"
                    : $"主文件和备份都不可用。原始错误：{ex.Message}",
                ex);
        }
    }

    private async Task<LauncherState> ReadJsonFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var state = await JsonSerializer.DeserializeAsync<LauncherState>(
            stream,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("JSON 内容为空。");

        if (state.SchemaVersion > LauncherState.CurrentSchemaVersion)
        {
            throw new ProfileStoreException(
                "STORE_SCHEMA_NEWER",
                "环境列表由更高版本的启动器创建。",
                $"文件 schemaVersion={state.SchemaVersion}，当前只支持 {LauncherState.CurrentSchemaVersion}。");
        }

        if (state.SchemaVersion != LauncherState.CurrentSchemaVersion)
        {
            throw new ProfileStoreException(
                "STORE_SCHEMA_UNSUPPORTED",
                "环境列表版本不受支持。",
                $"文件 schemaVersion={state.SchemaVersion}，当前只支持 {LauncherState.CurrentSchemaVersion}。");
        }

        ValidateStateSemantics(state, path);
        return state;
    }

    private static void ValidateStateSemantics(LauncherState state, string source)
    {
        if (state.Revision < 0)
        {
            throw SemanticError(source, "revision 不能为负数。");
        }

        if (state.Profiles is null)
        {
            throw SemanticError(source, "profiles 不能为空。");
        }

        var ids = new HashSet<Guid>();
        var roots = new List<(Guid ProfileId, string Name, string Root)>();
        foreach (var profile in state.Profiles)
        {
            if (profile is null)
            {
                throw SemanticError(source, "profiles 中包含 null 项。");
            }

            if (profile.Id == Guid.Empty)
            {
                throw SemanticError(source, $"环境“{profile.Name}”的 UUID 不能为空。");
            }

            if (!ids.Add(profile.Id))
            {
                throw SemanticError(source, $"环境 UUID 重复：{profile.Id}。");
            }

            string normalizedRoot;
            try
            {
                normalizedRoot = PathUtilities.Normalize(profile.DataRoot);
                if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory))
                {
                    _ = PathUtilities.Normalize(profile.WorkingDirectory);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw SemanticError(source, $"环境“{profile.Name}”包含无效路径：{ex.Message}", ex);
            }

            roots.Add((profile.Id, profile.Name, normalizedRoot));
            ValidateReceipt(profile, normalizedRoot, source);
        }

        for (var firstIndex = 0; firstIndex < roots.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < roots.Count; secondIndex++)
            {
                var first = roots[firstIndex];
                var second = roots[secondIndex];
                if (PathUtilities.Overlaps(first.Root, second.Root))
                {
                    throw SemanticError(
                        source,
                        $"环境“{first.Name}”与“{second.Name}”的数据目录重合或嵌套：{first.Root} / {second.Root}。");
                }
            }
        }

        if (state.SelectedProfileId is { } selectedProfileId && !ids.Contains(selectedProfileId))
        {
            throw SemanticError(source, $"selectedProfileId 指向不存在的环境：{selectedProfileId}。");
        }
    }

    private static void ValidateReceipt(CodexProfile profile, string normalizedRoot, string source)
    {
        var receipt = profile.ActiveInstance;
        if (receipt is null)
        {
            return;
        }

        if (receipt.SchemaVersion != 1)
        {
            throw SemanticError(source, $"环境“{profile.Name}”的运行记录 schemaVersion 不受支持。");
        }

        if (receipt.ProfileId != profile.Id)
        {
            throw SemanticError(source, $"环境“{profile.Name}”的运行记录属于另一个 profile。");
        }

        if (receipt.LaunchId == Guid.Empty)
        {
            throw SemanticError(source, $"环境“{profile.Name}”的 launchId 不能为空。");
        }

        ValidateOwnership(profile, receipt, source);

        if (!receipt.IsLaunchPending &&
            (receipt.RootProcessId <= 0 || receipt.ProcessStartUtcTicks <= 0))
        {
            throw SemanticError(source, $"环境“{profile.Name}”的运行记录缺少有效 PID 或进程创建时间。");
        }

        if (receipt.IsLaunchPending &&
            (receipt.RootProcessId < 0 || receipt.ProcessStartUtcTicks < 0))
        {
            throw SemanticError(source, $"环境“{profile.Name}”的待启动记录包含负数 PID 或创建时间。");
        }

        if (receipt.IsLaunchPending && receipt.IsIsolationVerified)
        {
            throw SemanticError(source, $"环境“{profile.Name}”的待启动记录不能标记为已验证隔离。");
        }

        var expectedPaths = ProfilePaths.FromRoot(normalizedRoot);
        string actualCodexHome;
        string actualAppData;
        try
        {
            actualCodexHome = PathUtilities.Normalize(receipt.CodexHomePath);
            actualAppData = PathUtilities.Normalize(receipt.AppDataPath);
            if (!string.IsNullOrWhiteSpace(receipt.ExecutablePath))
            {
                _ = PathUtilities.Normalize(receipt.ExecutablePath);
            }
            else if (!receipt.IsLaunchPending)
            {
                throw new ArgumentException("ExecutablePath 不能为空。");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw SemanticError(source, $"环境“{profile.Name}”的运行记录包含无效路径：{ex.Message}", ex);
        }

        if (!actualCodexHome.Equals(expectedPaths.CodexHome, StringComparison.OrdinalIgnoreCase) ||
            !actualAppData.Equals(expectedPaths.AppData, StringComparison.OrdinalIgnoreCase))
        {
            throw SemanticError(
                source,
                $"环境“{profile.Name}”的运行记录路径与 profile 不一致。" +
                $" 期望 CODEX_HOME={expectedPaths.CodexHome}, app-data={expectedPaths.AppData}。" +
                $" 实际 CODEX_HOME={actualCodexHome}, app-data={actualAppData}。");
        }
    }

    private static void ValidateOwnership(
        CodexProfile profile,
        RunningInstanceReceipt receipt,
        string source)
    {
        if (string.IsNullOrEmpty(receipt.OwnershipMode) ||
            receipt.OwnershipMode.Equals(ProcessOwnershipModes.LegacyProcessTree, StringComparison.Ordinal))
        {
            if (receipt.OwnershipVersion is not (0 or 1) ||
                !string.IsNullOrEmpty(receipt.JobObjectName) ||
                !string.IsNullOrEmpty(receipt.ReadyEventName) ||
                receipt.WindowsSessionId != -1 ||
                receipt.BrokerProcessId != 0 ||
                receipt.BrokerProcessStartUtcTicks != 0 ||
                !string.IsNullOrEmpty(receipt.LaunchPhase))
            {
                throw SemanticError(source, $"环境“{profile.Name}”的 legacy 运行记录包含 Job Object 字段。");
            }

            return;
        }

        if (!ProcessOwnershipModes.IsWindowsJob(receipt))
        {
            throw SemanticError(source, $"环境“{profile.Name}”的进程所有权模式或版本不受支持。");
        }

        if (string.IsNullOrWhiteSpace(receipt.JobObjectName) ||
            !receipt.JobObjectName.StartsWith(@"Global\CodexProfileLauncher.Job.v1.", StringComparison.Ordinal) ||
            !receipt.JobObjectName.EndsWith(
                $".{profile.Id:N}.{receipt.LaunchId:N}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw SemanticError(source, $"环境“{profile.Name}”的 Job Object 名称与 profile 不一致。");
        }

        if (string.IsNullOrWhiteSpace(receipt.ReadyEventName) ||
            !receipt.ReadyEventName.StartsWith(@"Global\CodexProfileLauncher.JobReady.v1.", StringComparison.Ordinal) ||
            !receipt.ReadyEventName.EndsWith(receipt.LaunchId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            throw SemanticError(source, $"环境“{profile.Name}”的 broker ready event 与 launchId 不一致。");
        }

        if (receipt.WindowsSessionId < 0)
        {
            throw SemanticError(source, $"环境“{profile.Name}”的 Job Object 运行记录缺少 Windows session ID。");
        }

        switch (receipt.LaunchPhase)
        {
            case JobLaunchPhases.PendingIntent
                when receipt.IsLaunchPending &&
                     receipt.RootProcessId == 0 &&
                     receipt.ProcessStartUtcTicks == 0 &&
                     ((receipt.BrokerProcessId == 0 && receipt.BrokerProcessStartUtcTicks == 0) ||
                      (receipt.BrokerProcessId > 0 && receipt.BrokerProcessStartUtcTicks > 0)):
                return;
            case JobLaunchPhases.Resumed
                when !receipt.IsLaunchPending &&
                     receipt.RootProcessId > 0 &&
                     receipt.ProcessStartUtcTicks > 0 &&
                     receipt.BrokerProcessId > 0 &&
                     receipt.BrokerProcessStartUtcTicks > 0:
                return;
            default:
                throw SemanticError(source, $"环境“{profile.Name}”的 Job Object 启动阶段与进程身份不一致。");
        }
    }

    private static ProfileStoreException SemanticError(
        string source,
        string details,
        Exception? innerException = null) =>
        new(
            "STORE_SEMANTIC_INVALID",
            "环境列表包含不一致或不安全的数据。",
            $"{source}: {details}",
            innerException);

    private async Task WriteJsonFileAsync(
        string path,
        LauncherState state,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new ProfileStoreException(
                    "STORE_LOCK_TIMEOUT",
                    "环境列表正在被另一进程使用。",
                    "等待文件锁 5 秒后仍未成功。",
                    ex);
            }
        }
    }
}

public sealed class ProfileStoreException : Exception
{
    public ProfileStoreException(string code, string message, string details, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public string Details { get; }
}
