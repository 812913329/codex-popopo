using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CodexProfileLauncher.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace CodexProfileLauncher.Infrastructure;

public sealed record WindowsJobNames(
    string JobObjectName,
    string ReadyEventName,
    string CancelEventName);

public sealed record WindowsJobProcessIdentity(
    int ProcessId,
    long ProcessStartUtcTicks,
    string ExecutablePath,
    int WindowsSessionId);

internal sealed record WindowsCompatibilityProcessLaunch(
    Process Process,
    WindowsJobProcessIdentity Identity,
    bool? IsInAnyJob,
    string Details);

/// <summary>
/// A point-in-time view of a named Job Object. Exists=false means only that
/// the name is not openable from this caller; it is never proof that an older
/// process tree has stopped.
/// </summary>
public sealed record WindowsJobInspection(
    bool Exists,
    bool KillOnJobClose,
    IReadOnlyList<WindowsJobProcessIdentity> Members,
    IReadOnlyList<string> InspectionErrors,
    int CurrentWindowsSessionId)
{
    public bool IsEmpty => Exists && Members.Count == 0 && InspectionErrors.Count == 0;

    public IReadOnlyList<int> ProcessIds => Members.Select(member => member.ProcessId).ToArray();
}

public sealed record WindowsNamedSignalInspection(
    bool Exists,
    bool IsSignaled,
    string? Error);

public sealed record WindowsJobOwnershipInspection(
    WindowsJobInspection Job,
    WindowsNamedSignalInspection ReadyEvent,
    WindowsNamedSignalInspection CancelEvent);

public sealed record WindowsJobBrokerIdentity(
    int ProcessId,
    long ProcessStartUtcTicks,
    string ExecutablePath,
    int WindowsSessionId,
    string JobObjectName,
    string ReadyEventName,
    string CancelEventName);

public enum WindowsJobBrokerRecoveryState
{
    Found,
    NotFound,
    Unknown,
}

public sealed record WindowsJobBrokerRecovery(
    WindowsJobBrokerRecoveryState State,
    WindowsJobBrokerIdentity? Broker,
    WindowsJobOwnershipInspection Ownership,
    IReadOnlyList<string> InspectionErrors);

public enum WindowsJobBrokerIdentityState
{
    VerifiedLive,
    DefinitelyAbsent,
    InspectionError,
}

public sealed record WindowsJobBrokerIdentityInspection(
    WindowsJobBrokerIdentityState State,
    WindowsJobBrokerIdentity? Broker,
    string Details);

public enum ReceiptJobOperationState
{
    Succeeded,
    NotConfirmed,
}

public sealed record ReceiptJobOperationResult(
    ReceiptJobOperationState State,
    string Code,
    string Details,
    IReadOnlyList<int> VerifiedMemberProcessIds)
{
    public bool Succeeded => State == ReceiptJobOperationState.Succeeded;
}

public enum PendingUnreadyReclaimState
{
    Reclaimed,
    NotConfirmed,
}

public sealed record PendingUnreadyReclaimResult(
    PendingUnreadyReclaimState State,
    string Code,
    string Details)
{
    public bool Reclaimed => State == PendingUnreadyReclaimState.Reclaimed;
}

public sealed partial class WindowsJobObjectManager
{
    internal const uint JobObjectAssignProcess = 0x0001;
    internal const uint JobObjectQuery = 0x0004;
    internal const uint JobObjectTerminate = 0x0008;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const int JobObjectBasicProcessIdList = 3;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorAlreadyExists = 183;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorMoreData = 234;
    internal const uint BrokerFailureExitCode = 0xC0DE0001;

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupinfoPresent = 0x00080000;
    private const nuint ProcThreadAttributeParentProcess = 0x00020000;
    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessCreateProcess = 0x0080;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserInformationClass = 1;
    private const int ProcessCommandLineInformation = 60;
    private const uint ResumeFailed = uint.MaxValue;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = uint.MaxValue;
    private const int MaximumCommandLineCharacters = 32_767;
    private const int MaximumImagePathCharacters = 32_768;
    private const uint DuplicateCloseSource = 0x00000001;
    private const uint DuplicateSameAccess = 0x00000002;
    private static readonly TimeSpan DefaultBrokerReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StableSampleDelay = TimeSpan.FromMilliseconds(200);
    private const int StableEmptySampleCount = 3;

    private readonly string _brokerExecutablePath;
    private readonly IReadOnlyDictionary<string, string?> _brokerEnvironmentOverrides;
    private readonly string _currentUserSid;
    private readonly int _currentWindowsSessionId;

    public WindowsJobObjectManager(
        string? brokerExecutablePath = null,
        IReadOnlyDictionary<string, string?>? brokerEnvironmentOverrides = null)
    {
        _brokerExecutablePath = Path.GetFullPath(
            brokerExecutablePath
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前启动器可执行文件路径。"));
        if (!File.Exists(_brokerExecutablePath))
        {
            throw new FileNotFoundException("找不到 Windows Job broker 可执行文件。", _brokerExecutablePath);
        }

        _brokerEnvironmentOverrides = brokerEnvironmentOverrides is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(
                brokerEnvironmentOverrides,
                StringComparer.OrdinalIgnoreCase);
        foreach (var name in _brokerEnvironmentOverrides.Keys)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Contains('\0'))
            {
                throw new ArgumentException(
                    $"broker 环境变量名称无效：{name}",
                    nameof(brokerEnvironmentOverrides));
            }
        }

        _currentUserSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");
        _currentWindowsSessionId = GetSessionId(Environment.ProcessId);
    }

    public int CurrentWindowsSessionId => _currentWindowsSessionId;

    public string BrokerExecutablePath => _brokerExecutablePath;

    internal WindowsCompatibilityProcessLaunch StartCompatibilityProcess(
        ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ValidateTargetStartInfo(startInfo);
        var applicationPath = Path.GetFullPath(startInfo.FileName);
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException("找不到兼容模式要启动的程序。", applicationPath);
        }

        Process? process = null;
        SafeProcessHandle? processHandle = null;
        var exactCreatedProcess = false;
        var completed = false;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start 未返回兼容模式进程。");
            // Process.Start returns the native handle for this exact generation.
            // Keep using that owned handle so PID reuse cannot redirect cleanup.
            processHandle = process.SafeHandle;
            if (processHandle.IsInvalid)
            {
                throw new WindowsJobObjectException(
                    "COMPATIBILITY_PROCESS_HANDLE_INVALID",
                    "兼容模式已创建进程，但没有取得可核验的原生句柄。",
                    $"PID={process.Id}。");
            }

            exactCreatedProcess = true;
            var identity = CaptureProcessIdentity(processHandle);
            if (!PathsEqual(identity.ExecutablePath, applicationPath))
            {
                throw new WindowsJobObjectException(
                    "COMPATIBILITY_PROCESS_IMAGE_MISMATCH",
                    "兼容模式创建的进程映像与请求路径不一致。",
                    $"PID={process.Id}, 请求={applicationPath}，实际={identity.ExecutablePath}。");
            }

            var waitResult = WaitForSingleObject(processHandle, 0);
            if (waitResult == WaitFailed)
            {
                throw CreateWin32Exception(
                    "COMPATIBILITY_PROCESS_WAIT_FAILED",
                    "无法核验兼容模式进程是否仍在运行。",
                    Marshal.GetLastPInvokeError(),
                    $"PID={process.Id}。");
            }

            if (waitResult != WaitTimeout)
            {
                throw new WindowsJobObjectException(
                    "COMPATIBILITY_PROCESS_EXITED_DURING_VERIFY",
                    "兼容模式进程在身份核验期间已退出。",
                    $"PID={process.Id}。");
            }

            if (!TryReadProcessOwnerSid(processHandle, out var ownerSid) ||
                !ownerSid.Equals(_currentUserSid, StringComparison.Ordinal))
            {
                throw new WindowsJobObjectException(
                    "COMPATIBILITY_PROCESS_OWNER_MISMATCH",
                    "兼容模式进程不属于当前 Windows 用户。",
                    $"PID={process.Id}, SID={ownerSid}。");
            }

            var sessionId = GetSessionId(process.Id);
            if (sessionId != _currentWindowsSessionId)
            {
                throw new WindowsJobObjectException(
                    "COMPATIBILITY_PROCESS_SESSION_MISMATCH",
                    "兼容模式进程不在当前 Windows session。",
                    $"PID={process.Id}, expected={_currentWindowsSessionId}, actual={sessionId}。");
            }

            bool? isInAnyJob = null;
            var jobQueryDetails = "IsProcessInJob 查询不可用";
            if (IsProcessInAnyJob(processHandle, IntPtr.Zero, out var queriedInAnyJob))
            {
                isInAnyJob = queriedInAnyJob;
                jobQueryDetails = $"IsInAnyJob={queriedInAnyJob}";
            }
            else
            {
                jobQueryDetails += $"，Win32={Marshal.GetLastPInvokeError()}";
            }

            var result = new WindowsCompatibilityProcessLaunch(
                process,
                new(
                    process.Id,
                    identity.ProcessStartUtcTicks,
                    identity.ExecutablePath,
                    sessionId),
                isInAnyJob,
                $"PID={process.Id}, Session={sessionId}, {jobQueryDetails}；" +
                "兼容模式保留数据隔离核验，但不承诺专用 Job 托管或独立存活。");
            process = null;
            completed = true;
            return result;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new WindowsJobObjectException(
                "COMPATIBILITY_PROCESS_CREATE_FAILED",
                "兼容启动模式无法启动 Codex。",
                $"{ex.GetType().Name}: HRESULT=0x{ex.HResult:X8}, {ex.Message}");
        }
        finally
        {
            if (!completed && exactCreatedProcess && processHandle is { IsInvalid: false })
            {
                _ = TerminateProcess(processHandle, BrokerFailureExitCode);
                _ = WaitForSingleObject(processHandle, 5_000);
            }

            // processHandle is owned by the Process wrapper returned by Process.Start.
            process?.Dispose();
        }
    }

    public WindowsJobNames CreateNames(Guid profileId, Guid launchId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile UUID 不能为空。", nameof(profileId));
        }

        if (launchId == Guid.Empty)
        {
            throw new ArgumentException("Launch UUID 不能为空。", nameof(launchId));
        }

        return new(
            $@"Global\CodexProfileLauncher.Job.v1.{_currentUserSid}.{profileId:N}.{launchId:N}",
            $@"Global\CodexProfileLauncher.JobReady.v1.{_currentUserSid}.{launchId:N}",
            $@"Global\CodexProfileLauncher.JobCancel.v1.{_currentUserSid}.{launchId:N}");
    }

    public string CreateJobName(Guid profileId, Guid launchId) =>
        CreateNames(profileId, launchId).JobObjectName;

    public string CreateReadyEventName(Guid profileId, Guid launchId) =>
        CreateNames(profileId, launchId).ReadyEventName;

    public JobBrokerConnection StartBroker(
        WindowsJobNames names,
        TimeSpan? readyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ValidateNames(names, _currentUserSid);
        var timeout = readyTimeout ?? DefaultBrokerReadyTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readyTimeout));
        }

        EventWaitHandle? readyEvent = null;
        EventWaitHandle? cancelEvent = null;
        Process? brokerProcess = null;
        try
        {
            readyEvent = CreateUniqueManualResetEvent(names.ReadyEventName);
            cancelEvent = CreateUniqueManualResetEvent(names.CancelEventName);
            using var parent = Process.GetCurrentProcess();
            var parentStartTicks = parent.StartTime.ToUniversalTime().Ticks;
            var startInfo = new ProcessStartInfo
            {
                FileName = _brokerExecutablePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_brokerExecutablePath) ?? AppContext.BaseDirectory,
                CreateNoWindow = true,
            };
            foreach (var (name, value) in _brokerEnvironmentOverrides)
            {
                startInfo.Environment[name] = value;
            }
            WindowsJobBroker.AppendBrokerArguments(
                startInfo,
                new(
                    names.JobObjectName,
                    names.ReadyEventName,
                    names.CancelEventName,
                    parent.Id,
                    parentStartTicks));

            brokerProcess = StartDetachedBrokerProcess(startInfo, readyEvent);
            var brokerIdentity = CaptureBrokerIdentity(
                brokerProcess.Id,
                names,
                requireCommandLine: true);

            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                // Require both ready signal and an openable Job name. A doomed
                // breakaway attempt may race and signal ready before we kill it;
                // waiting only on the event would then open a vanished Job.
                if (readyEvent.WaitOne(0))
                {
                    using var probeJob = OpenJob(
                        names.JobObjectName,
                        JobObjectQuery,
                        allowMissing: true);
                    if (probeJob is not null)
                    {
                        break;
                    }

                    // Spurious/stale ready without Job — clear and keep waiting
                    // for the live detached broker.
                    _ = readyEvent.Reset();
                }

                brokerProcess.Refresh();
                if (brokerProcess.HasExited)
                {
                    throw new WindowsJobObjectException(
                        "JOB_BROKER_EXITED_DURING_SETUP",
                        "Windows Job broker 在 ready 前退出。",
                        $"PID={brokerProcess.Id}, ExitCode={brokerProcess.ExitCode}。");
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    throw new WindowsJobObjectException(
                        "JOB_BROKER_READY_TIMEOUT",
                        "等待 Windows Job broker 就绪超时。",
                        $"PID={brokerProcess.Id}, Job={names.JobObjectName}。");
                }

                _ = readyEvent.WaitOne(50);
            }

            brokerProcess.Refresh();
            if (brokerProcess.HasExited)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_EXITED_AFTER_READY",
                    "Windows Job broker 在 ready 验证期间退出。",
                    $"PID={brokerProcess.Id}, ExitCode={brokerProcess.ExitCode}。");
            }

            using (var jobHandle = OpenJob(
                       names.JobObjectName,
                       JobObjectAssignProcess | JobObjectQuery | JobObjectTerminate,
                       allowMissing: false)!)
            {
                var limitFlags = QueryLimitFlags(jobHandle);
                if ((limitFlags & JobObjectLimitKillOnJobClose) == 0)
                {
                    throw new WindowsJobObjectException(
                        "JOB_KILL_ON_CLOSE_MISSING",
                        "broker 的 Job Object 未启用 KILL_ON_JOB_CLOSE。",
                        $"Job={names.JobObjectName}, LimitFlags=0x{limitFlags:X8}。");
                }

                var processIds = QueryProcessIds(jobHandle);
                if (processIds.Count != 0)
                {
                    throw new WindowsJobObjectException(
                        "JOB_NOT_EMPTY_AT_READY",
                        "fresh Job Object 在 ready 时已包含进程。",
                        $"Job={names.JobObjectName}, PIDs={string.Join(',', processIds)}。");
                }

                using var brokerHandle = OpenProcess(
                    ProcessQueryLimitedInformation,
                    false,
                    brokerIdentity.ProcessId);
                if (brokerHandle.IsInvalid ||
                    !IsProcessInAnyJob(brokerHandle, IntPtr.Zero, out var brokerIsInAnyJob))
                {
                    throw CreateWin32Exception(
                        "BROKER_OUTER_JOB_QUERY_FAILED",
                        "无法核验 broker 是否脱离所有外层 Job。",
                        Marshal.GetLastPInvokeError());
                }

                if (brokerIsInAnyJob)
                {
                    throw new WindowsJobObjectException(
                        "BROKER_IN_OUTER_JOB",
                        "broker 仍属于外层 Job，拒绝继续创建 Codex root。",
                        "当前宿主或祖先 Job 未允许完整 breakaway；请换用不施加该限制的桌面入口。");
                }

                if (!IsProcessInJob(brokerHandle, jobHandle, out var brokerIsInnerMember))
                {
                    throw CreateWin32Exception(
                        "BROKER_JOB_SEPARATION_QUERY_FAILED",
                        "无法核验 broker 与 inner Job 的隔离关系。",
                        Marshal.GetLastPInvokeError());
                }

                if (brokerIsInnerMember)
                {
                    throw new WindowsJobObjectException(
                        "BROKER_INSIDE_INNER_JOB",
                        "broker 不能成为其持有的 inner Job 成员。",
                        $"BrokerPID={brokerIdentity.ProcessId}, Job={names.JobObjectName}。");
                }
            }

            var result = new JobBrokerConnection(
                this,
                names,
                brokerProcess,
                brokerIdentity,
                readyEvent,
                cancelEvent);
            brokerProcess = null;
            readyEvent = null;
            cancelEvent = null;
            return result;
        }
        catch
        {
            _ = cancelEvent?.Set();
            TryKillExactBroker(brokerProcess);
            throw;
        }
        finally
        {
            brokerProcess?.Dispose();
            readyEvent?.Dispose();
            cancelEvent?.Dispose();
        }
    }

    public ResumedJobProcessTransaction CreateAssignedAndResume(
        ProcessStartInfo startInfo,
        JobBrokerConnection broker)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(broker);
        broker.ClaimCreate(this);
        ValidateTargetStartInfo(startInfo);

        var applicationPath = Path.GetFullPath(startInfo.FileName);
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException("找不到要在 Job Object 中启动的程序。", applicationPath);
        }

        var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Path.GetDirectoryName(applicationPath) ?? Environment.CurrentDirectory
            : Path.GetFullPath(startInfo.WorkingDirectory);
        SafeProcessHandle? processHandle = null;
        SafeKernelObjectHandle? threadHandle = null;
        SafeJobHandle? jobHandle = null;
        Process? managedProcess = null;
        System.IO.Pipes.NamedPipeClientStream? controlPipe = null;
        var receivedSuspendedMember = false;
        var resumeAcknowledged = false;
        try
        {
            jobHandle = OpenJob(
                broker.Names.JobObjectName,
                JobObjectAssignProcess | JobObjectQuery | JobObjectTerminate,
                allowMissing: false)!;
            var flags = QueryLimitFlags(jobHandle);
            if ((flags & JobObjectLimitKillOnJobClose) == 0)
            {
                throw new WindowsJobObjectException(
                    "JOB_KILL_ON_CLOSE_MISSING",
                    "拒绝向未启用 KILL_ON_JOB_CLOSE 的 Job Object 创建进程。",
                    $"Job={broker.Names.JobObjectName}, LimitFlags=0x{flags:X8}。");
            }

            if (!broker.IsReadySignaled || QueryProcessIds(jobHandle).Count != 0)
            {
                throw new WindowsJobObjectException(
                    "JOB_NOT_FRESH_FOR_CREATE",
                    "Job Object 未处于 ready 且空的 fresh 状态。",
                    $"Job={broker.Names.JobObjectName}。");
            }

            VerifyDetachedBrokerForRoot(broker);
            controlPipe = WindowsJobBrokerProtocol.CreateClient(broker.ControlPipeName);
            try
            {
                controlPipe.Connect(5_000);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_PIPE_CONNECT_FAILED",
                    "无法连接已验证 broker 的原子创建通道。",
                    ex.Message);
            }

            if (!WindowsJobBrokerProtocol.IsExpectedServer(
                    controlPipe,
                    broker.BrokerProcessId,
                    out var serverDetails))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_PIPE_SERVER_MISMATCH",
                    "原子创建通道并非已验证的 exact broker。",
                    serverDetails);
            }

            VerifyDetachedBrokerForRoot(broker);

            var request = new BrokerCreateProcessRequest(
                WindowsJobBrokerProtocol.Version,
                applicationPath,
                workingDirectory,
                startInfo.ArgumentList.ToArray(),
                startInfo.Environment.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase),
                startInfo.CreateNoWindow);
            BrokerCreateProcessResponse response;
            using (var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                WindowsJobBrokerProtocol
                    .WriteAsync(controlPipe, request, requestTimeout.Token)
                    .GetAwaiter()
                    .GetResult();
                response = WindowsJobBrokerProtocol
                    .ReadAsync<BrokerCreateProcessResponse>(controlPipe, requestTimeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }

            if (response.ProtocolVersion != WindowsJobBrokerProtocol.Version)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_PROTOCOL_MISMATCH",
                    "broker 原子创建协议版本不一致。",
                    $"Expected={WindowsJobBrokerProtocol.Version}, Actual={response.ProtocolVersion}。");
            }

            if (!response.Succeeded)
            {
                throw new WindowsJobObjectException(
                    string.IsNullOrWhiteSpace(response.Code)
                        ? "JOB_BROKER_CREATE_FAILED"
                        : response.Code,
                    string.IsNullOrWhiteSpace(response.Message)
                        ? "broker 无法原子创建 Codex root。"
                        : response.Message,
                    response.Details);
            }

            // From a success response onward the pinned Job is the local
            // authoritative rollback path, even if the response itself is
            // malformed or one duplicated handle later fails validation.
            receivedSuspendedMember = true;

            if (response.ProcessId <= 0 ||
                response.ThreadId <= 0 ||
                response.ProcessStartUtcTicks <= 0 ||
                response.WindowsSessionId < 0 ||
                response.LauncherProcessHandle is 0 or -1 ||
                response.LauncherThreadHandle is 0 or -1 ||
                string.IsNullOrWhiteSpace(response.ExecutablePath))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_RESPONSE_INVALID",
                    "broker 返回的挂起进程身份或句柄无效。",
                    $"PID={response.ProcessId}, TID={response.ThreadId}, " +
                    $"ProcessHandle=0x{response.LauncherProcessHandle:X}, " +
                    $"ThreadHandle=0x{response.LauncherThreadHandle:X}。");
            }

            processHandle = new SafeProcessHandle(
                new IntPtr(response.LauncherProcessHandle),
                ownsHandle: true);
            threadHandle = new SafeKernelObjectHandle(
                new IntPtr(response.LauncherThreadHandle),
                ownsHandle: true);
            var actualProcessId = GetProcessId(processHandle);
            var actualThreadId = GetThreadId(threadHandle);
            var threadOwnerProcessId = GetProcessIdOfThread(threadHandle);
            if (actualProcessId != checked((uint)response.ProcessId) ||
                actualThreadId != checked((uint)response.ThreadId) ||
                threadOwnerProcessId != checked((uint)response.ProcessId))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_HANDLE_IDENTITY_MISMATCH",
                    "broker 交付的原生句柄与声明的进程身份不一致。",
                    $"ExpectedPID/TID={response.ProcessId}/{response.ThreadId}, " +
                    $"ActualPID/TID/ThreadOwner={actualProcessId}/{actualThreadId}/" +
                    $"{threadOwnerProcessId}。");
            }

            if (!IsProcessInJob(processHandle, jobHandle, out var isInJob))
            {
                throw CreateWin32Exception(
                    "JOB_MEMBERSHIP_QUERY_FAILED",
                    "无法核验 broker 创建进程的 Job Object 成员关系。",
                    Marshal.GetLastPInvokeError());
            }

            if (!isInJob)
            {
                throw new WindowsJobObjectException(
                    "JOB_MEMBERSHIP_REJECTED",
                    "broker 创建的新进程没有原子进入指定 Job Object。",
                    $"PID={response.ProcessId}, Job={broker.Names.JobObjectName}。");
            }

            var suspendedMembers = QueryProcessIds(jobHandle);
            if (suspendedMembers.Count != 1 || suspendedMembers[0] != response.ProcessId)
            {
                throw new WindowsJobObjectException(
                    "JOB_NOT_SOLE_ROOT_BEFORE_RESUME",
                    "挂起 root 在恢复前不是 fresh Job 的唯一成员。",
                    $"ExpectedPID={response.ProcessId}, Members={string.Join(',', suspendedMembers)}。");
            }

            var nativeIdentity = CaptureProcessIdentity(processHandle);
            if (nativeIdentity.ProcessStartUtcTicks != response.ProcessStartUtcTicks ||
                !PathsEqual(nativeIdentity.ExecutablePath, applicationPath) ||
                !PathsEqual(response.ExecutablePath, applicationPath) ||
                GetSessionId(response.ProcessId) != response.WindowsSessionId)
            {
                throw new WindowsJobObjectException(
                    "PROCESS_IDENTITY_MISMATCH",
                    "broker 创建的挂起进程身份与请求不一致。",
                    $"请求={applicationPath}，实际={nativeIdentity.ExecutablePath}，" +
                    $"PID={response.ProcessId}。");
            }

            VerifyDetachedBrokerForRoot(broker);
            managedProcess = Process.GetProcessById(response.ProcessId);
            var previousSuspendCount = ResumeThread(threadHandle);
            if (previousSuspendCount == ResumeFailed)
            {
                throw CreateWin32Exception(
                    "THREAD_RESUME_FAILED",
                    "无法恢复 broker 原子创建的主线程。",
                    Marshal.GetLastPInvokeError());
            }

            if (previousSuspendCount != 1)
            {
                throw new WindowsJobObjectException(
                    "THREAD_SUSPEND_COUNT_UNEXPECTED",
                    "broker 创建的主线程并非恰好一层挂起。",
                    $"TID={response.ThreadId}, previousSuspendCount={previousSuspendCount}。已回滚整个 Job。");
            }

            using (var acknowledgementTimeout =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var commit = new BrokerCreateProcessControl(
                    WindowsJobBrokerProtocol.Version,
                    WindowsJobBrokerProtocol.ResumeAction);
                WindowsJobBrokerProtocol
                    .WriteAsync(controlPipe, commit, acknowledgementTimeout.Token)
                    .GetAwaiter()
                    .GetResult();
                var acknowledgement = WindowsJobBrokerProtocol
                    .ReadAsync<BrokerCreateProcessControl>(
                        controlPipe,
                        acknowledgementTimeout.Token)
                    .GetAwaiter()
                    .GetResult();
                if (acknowledgement.ProtocolVersion != WindowsJobBrokerProtocol.Version ||
                    !acknowledgement.Action.Equals(
                        WindowsJobBrokerProtocol.ResumeAction,
                        StringComparison.Ordinal))
                {
                    throw new WindowsJobObjectException(
                        "JOB_BROKER_COMMIT_ACK_INVALID",
                        "broker 未确认已恢复 root 的所有权交接。",
                        $"Protocol={acknowledgement.ProtocolVersion}, " +
                        $"Action={acknowledgement.Action}。");
                }
            }

            resumeAcknowledged = true;
            threadHandle.Dispose();
            threadHandle = null;
            var transaction = new ResumedJobProcessTransaction(
                jobHandle,
                processHandle,
                managedProcess,
                controlPipe,
                broker,
                broker.Names.JobObjectName,
                response.ProcessId,
                nativeIdentity.ProcessStartUtcTicks,
                nativeIdentity.ExecutablePath,
                response.WindowsSessionId);
            jobHandle = null;
            processHandle = null;
            managedProcess = null;
            controlPipe = null;
            return transaction;
        }
        catch
        {
            if (controlPipe?.IsConnected == true)
            {
                try
                {
                    using var abortTimeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    WindowsJobBrokerProtocol
                        .WriteAsync(
                            controlPipe,
                            new BrokerCreateProcessControl(
                                WindowsJobBrokerProtocol.Version,
                                WindowsJobBrokerProtocol.AbortAction),
                            abortTimeout.Token)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex) when (
                    ex is IOException or InvalidDataException or OperationCanceledException)
                {
                    // The pinned Job handle below remains the authoritative rollback path.
                }
            }

            if ((receivedSuspendedMember || resumeAcknowledged) &&
                jobHandle is { IsInvalid: false })
            {
                _ = TerminateJobObject(jobHandle, BrokerFailureExitCode);
            }

            throw;
        }
        finally
        {
            controlPipe?.Dispose();
            managedProcess?.Dispose();
            threadHandle?.Dispose();
            processHandle?.Dispose();
            jobHandle?.Dispose();
        }
    }

    public WindowsJobInspection Inspect(string jobObjectName)
    {
        ValidateJobName(jobObjectName, _currentUserSid);
        using var jobHandle = OpenJob(jobObjectName, JobObjectQuery, allowMissing: true);
        return jobHandle is null
            ? MissingInspection()
            : Inspect(jobHandle);
    }

    public WindowsNamedSignalInspection InspectReadyEvent(string readyEventName) =>
        InspectEvent(readyEventName, isReady: true);

    public WindowsNamedSignalInspection InspectCancelEvent(string cancelEventName) =>
        InspectEvent(cancelEventName, isReady: false);

    public WindowsJobOwnershipInspection InspectOwnership(WindowsJobNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        ValidateNames(names, _currentUserSid);
        return new(
            Inspect(names.JobObjectName),
            InspectReadyEvent(names.ReadyEventName),
            InspectCancelEvent(names.CancelEventName));
    }

    public WindowsJobBrokerRecovery RecoverBroker(WindowsJobNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        ValidateNames(names, _currentUserSid);
        var ownership = InspectOwnership(names);
        var errors = new List<string>();
        if (ownership.Job.InspectionErrors.Count != 0)
        {
            errors.AddRange(ownership.Job.InspectionErrors);
        }

        if (ownership.ReadyEvent.Error is not null)
        {
            errors.Add(ownership.ReadyEvent.Error);
        }

        if (ownership.CancelEvent.Error is not null)
        {
            errors.Add(ownership.CancelEvent.Error);
        }

        var candidates = FindBrokerCandidates(names, errors);
        if (candidates.Count == 0 && errors.Count == 0)
        {
            return new(
                WindowsJobBrokerRecoveryState.NotFound,
                null,
                ownership,
                []);
        }

        if (!ownership.Job.Exists ||
            !ownership.ReadyEvent.Exists ||
            !ownership.ReadyEvent.IsSignaled ||
            !ownership.CancelEvent.Exists ||
            errors.Count != 0 ||
            candidates.Count != 1)
        {
            if (candidates.Count != 1)
            {
                errors.Add($"exact broker 候选数量={candidates.Count}；必须恰好为 1。");
            }

            return new(
                WindowsJobBrokerRecoveryState.Unknown,
                null,
                ownership,
                errors.Distinct(StringComparer.Ordinal).ToArray());
        }

        return new(
            WindowsJobBrokerRecoveryState.Found,
            candidates[0].Identity,
            ownership,
            []);
    }

    public WindowsJobBrokerIdentityInspection InspectBrokerIdentity(
        RunningInstanceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!ProcessOwnershipModes.IsWindowsJob(receipt) ||
            string.IsNullOrWhiteSpace(receipt.JobObjectName) ||
            string.IsNullOrWhiteSpace(receipt.ReadyEventName))
        {
            return new(
                WindowsJobBrokerIdentityState.InspectionError,
                null,
                "运行记录缺少 Windows Job broker 命名身份。");
        }

        WindowsJobNames names;
        try
        {
            names = NamesFromPersisted(receipt.JobObjectName, receipt.ReadyEventName);
            ValidateReceiptIds(receipt, names);
        }
        catch (ArgumentException ex)
        {
            return new(WindowsJobBrokerIdentityState.InspectionError, null, ex.Message);
        }

        if (receipt.BrokerProcessId <= 0 || receipt.BrokerProcessStartUtcTicks <= 0)
        {
            var recovery = RecoverBroker(names);
            return recovery.State switch
            {
                WindowsJobBrokerRecoveryState.Found => new(
                    WindowsJobBrokerIdentityState.VerifiedLive,
                    recovery.Broker,
                    "按 Global 命名对象找到了唯一 exact broker。"),
                WindowsJobBrokerRecoveryState.NotFound => new(
                    WindowsJobBrokerIdentityState.DefinitelyAbsent,
                    null,
                    "完整扫描未发现 exact broker 候选。"),
                _ => new(
                    WindowsJobBrokerIdentityState.InspectionError,
                    null,
                    string.Join(" ", recovery.InspectionErrors)),
            };
        }

        try
        {
            var identity = CaptureBrokerIdentity(receipt.BrokerProcessId, names, requireCommandLine: true);
            if (identity.ProcessStartUtcTicks != receipt.BrokerProcessStartUtcTicks)
            {
                return new(
                    WindowsJobBrokerIdentityState.DefinitelyAbsent,
                    null,
                    "PID 存在，但 broker 创建时间不匹配；exact generation 已不存在。");
            }

            if (identity.WindowsSessionId != receipt.WindowsSessionId)
            {
                return new(
                    WindowsJobBrokerIdentityState.DefinitelyAbsent,
                    null,
                    $"broker session 不匹配：receipt={receipt.WindowsSessionId}, actual={identity.WindowsSessionId}。");
            }

            return new(
                WindowsJobBrokerIdentityState.VerifiedLive,
                identity,
                "broker PID/创建时间/路径/用户/session/命令行精确匹配。");
        }
        catch (WindowsJobObjectException ex) when (
            ex.Code.Equals("BROKER_PROCESS_OPEN_FAILED", StringComparison.Ordinal) &&
            ex.Details.StartsWith("Win32=87 ", StringComparison.Ordinal))
        {
            return new(
                WindowsJobBrokerIdentityState.DefinitelyAbsent,
                null,
                "broker PID 已不存在。");
        }
        catch (WindowsJobObjectException ex) when (
            ex.Code is "BROKER_IMAGE_MISMATCH" or "BROKER_OWNER_MISMATCH" or "BROKER_COMMAND_LINE_MISMATCH")
        {
            return new(
                WindowsJobBrokerIdentityState.DefinitelyAbsent,
                null,
                $"PID 已被其它 generation/进程占用：{ex.Message} {ex.Details}");
        }
        catch (Exception ex) when (
            ex is WindowsJobObjectException or Win32Exception or ArgumentException or InvalidOperationException)
        {
            return new(
                WindowsJobBrokerIdentityState.InspectionError,
                null,
                ex is WindowsJobObjectException jobError
                    ? $"{jobError.Message} {jobError.Details}"
                    : ex.Message);
        }
    }

    public bool VerifyBrokerIdentity(RunningInstanceReceipt receipt, out string details)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!ProcessOwnershipModes.IsWindowsJob(receipt) ||
            receipt.BrokerProcessId <= 0 ||
            receipt.BrokerProcessStartUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(receipt.JobObjectName) ||
            string.IsNullOrWhiteSpace(receipt.ReadyEventName))
        {
            details = "运行记录缺少 Windows Job broker 精确身份。";
            return false;
        }

        try
        {
            var names = NamesFromPersisted(receipt.JobObjectName, receipt.ReadyEventName);
            var inspectedIdentity = InspectBrokerIdentity(receipt);
            if (inspectedIdentity.State != WindowsJobBrokerIdentityState.VerifiedLive ||
                inspectedIdentity.Broker is null)
            {
                details = inspectedIdentity.Details;
                return false;
            }

            var identity = inspectedIdentity.Broker;

            var ownership = InspectOwnership(names);
            if (!ownership.Job.Exists ||
                !ownership.Job.KillOnJobClose ||
                !ownership.ReadyEvent.Exists ||
                !ownership.ReadyEvent.IsSignaled ||
                !ownership.CancelEvent.Exists)
            {
                details = "broker 命名对象不完整、ready 未置位或 Job 缺少 KILL_ON_CLOSE。";
                return false;
            }

            using var jobHandle = OpenJob(names.JobObjectName, JobObjectQuery, allowMissing: false)!;
            using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, identity.ProcessId);
            if (processHandle.IsInvalid ||
                !IsProcessInJob(processHandle, jobHandle, out var isInJob))
            {
                details = "无法核验 broker 是否位于 inner Job 外部。";
                return false;
            }

            if (isInJob)
            {
                details = "broker 错误地属于 inner Job。";
                return false;
            }

            details = $"broker PID={identity.ProcessId} 的 PID/创建时间/路径/用户/session/命令行与 Job keeper 关系均匹配。";
            return true;
        }
        catch (Exception ex) when (ex is WindowsJobObjectException or ArgumentException or Win32Exception)
        {
            details = ex is WindowsJobObjectException jobError
                ? $"{jobError.Message} {jobError.Details}"
                : ex.Message;
            return false;
        }
    }

    public bool VerifyMembership(RunningInstanceReceipt receipt, out string details)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!ProcessOwnershipModes.IsWindowsJob(receipt) || receipt.RootProcessId <= 0)
        {
            details = "运行记录不包含可核验的 Windows Job 根进程身份。";
            return false;
        }

        try
        {
            ValidateJobName(receipt.JobObjectName, _currentUserSid);
            using var jobHandle = OpenJob(receipt.JobObjectName, JobObjectQuery, allowMissing: false)!;
            using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, receipt.RootProcessId);
            if (processHandle.IsInvalid)
            {
                details = $"无法打开根进程 PID={receipt.RootProcessId}。";
                return false;
            }

            var actual = CaptureProcessIdentity(processHandle);
            if (actual.ProcessStartUtcTicks != receipt.ProcessStartUtcTicks ||
                !PathsEqual(actual.ExecutablePath, receipt.ExecutablePath))
            {
                details = "根进程创建时间或可执行路径不匹配。";
                return false;
            }

            if (GetSessionId(receipt.RootProcessId) != receipt.WindowsSessionId)
            {
                details = "根进程 Windows session 与运行记录不匹配。";
                return false;
            }

            if (!IsProcessInJob(processHandle, jobHandle, out var isInJob))
            {
                details = $"无法查询 Job 成员关系：{new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
                return false;
            }

            details = isInJob
                ? $"PID={receipt.RootProcessId} 的路径、创建时间和 Job 成员关系精确匹配。"
                : $"PID={receipt.RootProcessId} 不属于 {receipt.JobObjectName}。";
            return isInJob;
        }
        catch (Exception ex) when (ex is WindowsJobObjectException or ArgumentException or Win32Exception)
        {
            details = ex is WindowsJobObjectException jobError
                ? $"{jobError.Message} {jobError.Details}"
                : ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Pins one Job generation, verifies the persisted receipt and exact
    /// broker against that same handle, then terminates and drains that handle.
    /// No name-based reopen occurs between authorization and termination.
    /// </summary>
    public async Task<ReceiptJobOperationResult> TerminateVerifiedReceiptAndWaitForStableEmptyAsync(
        RunningInstanceReceipt receipt,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        try
        {
            using var binding = PinAndVerifyReceipt(receipt, allowRecoveredBroker: true);
            if (!TerminateJobObject(binding.JobHandle, BrokerFailureExitCode))
            {
                return ReceiptOperationNotConfirmed(
                    "JOB_TERMINATE_FAILED",
                    CreateWin32Exception(
                        "JOB_TERMINATE_FAILED",
                        "无法终止已绑定的 Windows Job Object。",
                        Marshal.GetLastPInvokeError()).Details,
                    binding.MemberProcessIds);
            }

            var empty = await WaitForStableEmptyAsync(
                binding.JobHandle,
                timeout,
                cancellationToken).ConfigureAwait(false);
            return empty
                ? new(
                    ReceiptJobOperationState.Succeeded,
                    "VERIFIED_JOB_TERMINATED_EMPTY",
                    "同一 pinned Job handle 已完成 receipt/broker 核验、TerminateJobObject 和稳定空等待。",
                    binding.MemberProcessIds)
                : ReceiptOperationNotConfirmed(
                    "JOB_STABLE_EMPTY_TIMEOUT",
                    "TerminateJobObject 已发出，但同一 pinned Job handle 未在时限内达到稳定空。",
                    binding.MemberProcessIds);
        }
        catch (ReceiptJobNotConfirmedException ex)
        {
            return ReceiptOperationNotConfirmed(ex.Code, ex.Message, ex.MemberProcessIds);
        }
        catch (Exception ex) when (
            ex is WindowsJobObjectException or Win32Exception or ArgumentException or InvalidOperationException)
        {
            return ReceiptOperationNotConfirmed(
                "RECEIPT_PINNED_INSPECTION_ERROR",
                FormatInspectionException(ex));
        }
    }

    /// <summary>
    /// Pins and verifies one receipt generation, then observes stable emptiness
    /// on that handle. A missing/exited broker is deliberately NotConfirmed so
    /// the caller can use its separate missing-generation drain policy.
    /// </summary>
    public async Task<ReceiptJobOperationResult> ConfirmVerifiedReceiptStableEmptyAsync(
        RunningInstanceReceipt receipt,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        try
        {
            using var binding = PinAndVerifyReceipt(receipt, allowRecoveredBroker: false);
            var empty = await WaitForStableEmptyAsync(
                binding.JobHandle,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (WaitForSingleObject(binding.BrokerHandle, 0) != WaitTimeout)
            {
                return ReceiptOperationNotConfirmed(
                    "VERIFIED_BROKER_EXITED_DURING_EMPTY_WAIT",
                    "exact broker 在稳定空确认完成前已退出；由 missing-generation drain 接管。",
                    binding.MemberProcessIds);
            }

            return empty
                ? new(
                    ReceiptJobOperationState.Succeeded,
                    "VERIFIED_JOB_STABLE_EMPTY",
                    "receipt 与 exact broker 已绑定到同一 pinned Job generation，且该 handle 连续稳定为空。",
                    binding.MemberProcessIds)
                : ReceiptOperationNotConfirmed(
                    "JOB_STABLE_EMPTY_TIMEOUT",
                    "同一 pinned Job handle 未在时限内连续稳定为空。",
                    binding.MemberProcessIds);
        }
        catch (ReceiptJobNotConfirmedException ex)
        {
            return ReceiptOperationNotConfirmed(ex.Code, ex.Message, ex.MemberProcessIds);
        }
        catch (Exception ex) when (
            ex is WindowsJobObjectException or Win32Exception or ArgumentException or InvalidOperationException)
        {
            return ReceiptOperationNotConfirmed(
                "RECEIPT_PINNED_INSPECTION_ERROR",
                FormatInspectionException(ex));
        }
    }

    /// <summary>
    /// Reclaims an expired pending setup only when every unready invariant is
    /// proven while holding the Job generation: unready signal, empty KILL job,
    /// unique exact broker, and definitely absent exact parent.
    /// </summary>
    public async Task<PendingUnreadyReclaimResult> ReclaimExpiredPendingUnreadyAsync(
        RunningInstanceReceipt receipt,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (!ProcessOwnershipModes.IsWindowsJob(receipt) ||
            !receipt.LaunchPhase.Equals(JobLaunchPhases.PendingIntent, StringComparison.Ordinal) ||
            !receipt.IsLaunchPending ||
            receipt.RootProcessId != 0 ||
            receipt.ProcessStartUtcTicks != 0)
        {
            return PendingReclaimNotConfirmed(
                "PENDING_RECEIPT_SHAPE_INVALID",
                "仅允许回收 root 尚未持久化的 pending-intent receipt。");
        }

        WindowsJobNames names;
        try
        {
            names = NamesFromPersisted(receipt.JobObjectName, receipt.ReadyEventName);
            ValidateReceiptIds(receipt, names);
        }
        catch (ArgumentException ex)
        {
            return PendingReclaimNotConfirmed("PENDING_NAMES_INVALID", ex.Message);
        }

        SafeJobHandle? jobHandle = null;
        try
        {
            jobHandle = OpenJob(
                names.JobObjectName,
                JobObjectQuery | JobObjectTerminate,
                allowMissing: true);
            if (jobHandle is null)
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_JOB_NOT_OPENABLE",
                    "Global Job 名称不可打开；这不证明旧 generation 已安全收口。");
            }

            if ((QueryLimitFlags(jobHandle) & JobObjectLimitKillOnJobClose) == 0)
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_JOB_KILL_FLAG_MISSING",
                    "unready Job 未启用 KILL_ON_JOB_CLOSE。");
            }

            var jobSnapshot = Inspect(jobHandle);
            if (!jobSnapshot.IsEmpty)
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_JOB_NOT_PROVEN_EMPTY",
                    $"unready Job 并非可确认空：members={jobSnapshot.Members.Count}, errors={jobSnapshot.InspectionErrors.Count}。");
            }

            using var readyEvent = OpenRequiredEvent(names.ReadyEventName, "PENDING_READY_EVENT_MISSING");
            if (readyEvent.WaitOne(0))
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_ALREADY_READY",
                    "ready 已置位，不能走 expired-unready 回收路径。");
            }

            using var cancelEvent = OpenRequiredEvent(names.CancelEventName, "PENDING_CANCEL_EVENT_MISSING");
            var candidateErrors = new List<string>();
            var candidates = FindBrokerCandidates(names, candidateErrors);
            if (candidateErrors.Count != 0 || candidates.Count != 1)
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_BROKER_NOT_UNIQUE",
                    candidateErrors.Count == 0
                        ? $"exact broker 候选数量={candidates.Count}；必须恰好为1。"
                        : string.Join(" ", candidateErrors));
            }

            var candidate = candidates[0];
            var hasPersistedBrokerId = receipt.BrokerProcessId > 0;
            var hasPersistedBrokerStart = receipt.BrokerProcessStartUtcTicks > 0;
            if (hasPersistedBrokerId != hasPersistedBrokerStart)
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_BROKER_IDENTITY_PARTIAL",
                    "receipt broker PID/start 必须同时为0或同时为正数。");
            }

            if (hasPersistedBrokerId &&
                (candidate.Identity.ProcessId != receipt.BrokerProcessId ||
                 candidate.Identity.ProcessStartUtcTicks != receipt.BrokerProcessStartUtcTicks))
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_BROKER_GENERATION_MISMATCH",
                    "唯一同名 unready broker 与 receipt 已持久化 generation 不匹配；拒绝终止 replacement。");
            }

            using var brokerHandle = OpenProcess(
                ProcessQueryLimitedInformation | ProcessTerminate | Synchronize,
                false,
                candidate.Identity.ProcessId);
            if (brokerHandle.IsInvalid)
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_BROKER_OPEN_FAILED",
                    $"无法 pin exact broker process handle：Win32={Marshal.GetLastPInvokeError()}。");
            }

            WindowsJobBrokerRequest request;
            try
            {
                request = VerifyBrokerHandle(
                    brokerHandle,
                    candidate.Identity.ProcessId,
                    names,
                    candidate.Identity.ProcessStartUtcTicks,
                    receipt.WindowsSessionId);
            }
            catch (ReceiptJobNotConfirmedException ex)
            {
                return PendingReclaimNotConfirmed(ex.Code, ex.Message);
            }

            var parentState = InspectExactProcessGeneration(
                request.ParentProcessId,
                request.ParentProcessStartUtcTicks);
            if (parentState.State != ExactProcessGenerationState.DefinitelyAbsent)
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_PARENT_NOT_DEFINITELY_ABSENT",
                    parentState.Details);
            }

            if (!TerminateProcess(brokerHandle, BrokerFailureExitCode))
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_BROKER_TERMINATE_FAILED",
                    $"TerminateProcess Win32={Marshal.GetLastPInvokeError()}。");
            }

            var brokerExited = await WaitForProcessHandleExitAsync(
                brokerHandle,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (!brokerExited)
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_BROKER_EXIT_TIMEOUT",
                    "exact broker 在时限内未确认退出。");
            }

            if (!await WaitForStableEmptyAsync(
                    jobHandle,
                    timeout,
                    cancellationToken).ConfigureAwait(false))
            {
                return PendingReclaimNotConfirmed(
                    "PENDING_JOB_EMPTY_TIMEOUT",
                    "broker 退出后 pinned Job 未在时限内连续稳定为空。");
            }
        }
        catch (WindowsJobObjectException ex)
        {
            return PendingReclaimNotConfirmed(ex.Code, $"{ex.Message} {ex.Details}");
        }
        catch (ReceiptJobNotConfirmedException ex)
        {
            return PendingReclaimNotConfirmed(ex.Code, ex.Message);
        }
        catch (Exception ex) when (
            ex is Win32Exception or ArgumentException or InvalidOperationException)
        {
            return PendingReclaimNotConfirmed(
                "PENDING_INSPECTION_ERROR",
                FormatInspectionException(ex));
        }
        finally
        {
            jobHandle?.Dispose();
        }

        using var reopened = OpenJob(
            names.JobObjectName,
            JobObjectQuery,
            allowMissing: true);
        if (reopened is not null)
        {
            return PendingReclaimNotConfirmed(
                "PENDING_JOB_NAME_STILL_EXISTS",
                "broker 与 pinned handle 关闭后 Job 名称仍存在；拒绝把它解释为已回收。");
        }

        return new(
            PendingUnreadyReclaimState.Reclaimed,
            "PENDING_UNREADY_RECLAIMED",
            "已在 pinned Job generation 内核验 unready/empty/unique broker/dead parent，终止 exact broker 并确认 Job 名称消失。");
    }

    public async Task<bool> TerminateAndWaitForStableEmptyAsync(
        string jobObjectName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateJobName(jobObjectName, _currentUserSid);
        using var jobHandle = OpenJob(
            jobObjectName,
            JobObjectQuery | JobObjectTerminate,
            allowMissing: false)!;
        if (!TerminateJobObject(jobHandle, BrokerFailureExitCode))
        {
            throw CreateWin32Exception(
                "JOB_TERMINATE_FAILED",
                "无法终止 Windows Job Object。",
                Marshal.GetLastPInvokeError(),
                $"Job={jobObjectName}。");
        }

        return await WaitForStableEmptyAsync(jobHandle, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> WaitForStableEmptyAsync(
        string jobObjectName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateJobName(jobObjectName, _currentUserSid);
        using var jobHandle = OpenJob(jobObjectName, JobObjectQuery, allowMissing: false)!;
        return await WaitForStableEmptyAsync(jobHandle, timeout, cancellationToken).ConfigureAwait(false);
    }

    internal static SafeJobHandle CreateFreshBrokerJob(string jobObjectName)
    {
        var jobHandle = CreateJobObject(IntPtr.Zero, jobObjectName);
        var createError = Marshal.GetLastPInvokeError();
        if (jobHandle.IsInvalid)
        {
            jobHandle.Dispose();
            throw CreateWin32Exception(
                "JOB_CREATE_FAILED",
                "broker 无法创建 Windows Job Object。",
                createError,
                $"Job={jobObjectName}。");
        }

        if (createError == ErrorAlreadyExists)
        {
            jobHandle.Dispose();
            throw new WindowsJobObjectException(
                "JOB_ALREADY_EXISTS",
                "拒绝复用已存在的 Job Object 名称。",
                $"Job={jobObjectName}。");
        }

        return jobHandle;
    }

    internal static void ConfigureAndVerifyKillOnClose(SafeJobHandle jobHandle)
    {
        var information = new JobObjectExtendedLimitInformationNative
        {
            BasicLimitInformation = new JobObjectBasicLimitInformationNative
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var size = checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>());
        if (!SetInformationJobObject(
                jobHandle,
                JobObjectExtendedLimitInformation,
                ref information,
                size))
        {
            throw CreateWin32Exception(
                "JOB_SET_KILL_ON_CLOSE_FAILED",
                "broker 无法设置 KILL_ON_JOB_CLOSE。",
                Marshal.GetLastPInvokeError());
        }

        var verified = QueryLimitFlags(jobHandle);
        if ((verified & JobObjectLimitKillOnJobClose) == 0)
        {
            throw new WindowsJobObjectException(
                "JOB_KILL_ON_CLOSE_VERIFY_FAILED",
                "KILL_ON_JOB_CLOSE 写入后反查未生效。",
                $"LimitFlags=0x{verified:X8}。");
        }
    }

    internal static IReadOnlyList<int> QueryProcessIds(SafeJobHandle jobHandle)
    {
        for (var capacity = 16; capacity <= 65_536; capacity *= 2)
        {
            var bufferLength = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                if (!QueryInformationJobObject(
                        jobHandle,
                        JobObjectBasicProcessIdList,
                        buffer,
                        checked((uint)bufferLength),
                        out _))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorMoreData)
                    {
                        continue;
                    }

                    throw CreateWin32Exception(
                        "JOB_QUERY_FAILED",
                        "无法查询 Job Object 成员进程。",
                        error);
                }

                var assigned = checked((uint)Marshal.ReadInt32(buffer, 0));
                var listed = checked((uint)Marshal.ReadInt32(buffer, 4));
                if (listed > capacity || assigned > listed)
                {
                    continue;
                }

                var processIds = new int[listed];
                for (var index = 0; index < listed; index++)
                {
                    processIds[index] = checked((int)Marshal.ReadIntPtr(
                        buffer,
                        checked(8 + index * IntPtr.Size)).ToInt64());
                }

                return processIds.Distinct().Order().ToArray();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new WindowsJobObjectException(
            "JOB_MEMBER_LIMIT_EXCEEDED",
            "Job Object 成员超过安全查询上限。",
            "成员数量超过 65,536；未把不完整快照当作完整结果。");
    }

    internal static bool IsExactProcessAlive(int processId, long processStartUtcTicks)
    {
        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid)
        {
            return false;
        }

        try
        {
            return CaptureProcessIdentity(processHandle).ProcessStartUtcTicks == processStartUtcTicks;
        }
        catch (WindowsJobObjectException)
        {
            return false;
        }
    }

    internal static void ValidateNames(WindowsJobNames names, string currentUserSid)
    {
        var (profileId, launchId) = ValidateJobName(names.JobObjectName, currentUserSid);
        _ = profileId;
        ValidateEventName(
            names.ReadyEventName,
            $@"Global\CodexProfileLauncher.JobReady.v1.{currentUserSid}.",
            launchId,
            nameof(names.ReadyEventName));
        ValidateEventName(
            names.CancelEventName,
            $@"Global\CodexProfileLauncher.JobCancel.v1.{currentUserSid}.",
            launchId,
            nameof(names.CancelEventName));
    }

    internal static string CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");

    /// <summary>
    /// Test/helper entry: start a child process fully outside any outer Job
    /// (breakaway, explorer-parent, then local-WMI fallback).
    /// </summary>
    internal Process StartDetachedProcessOutsideJobs(ProcessStartInfo startInfo)
    {
        using var readyPlaceholder = new ManualResetEvent(false);
        return StartDetachedBrokerProcess(startInfo, readyPlaceholder);
    }

    /// <summary>
    /// Test/helper entry for the final local-WMI breakaway strategy.
    /// </summary>
    internal Process StartDetachedProcessOutsideJobsViaWmi(ProcessStartInfo startInfo)
    {
        ValidateTargetStartInfo(startInfo);
        var applicationPath = Path.GetFullPath(startInfo.FileName);
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException("找不到 Windows Job broker 可执行文件。", applicationPath);
        }

        var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Path.GetDirectoryName(applicationPath) ?? Environment.CurrentDirectory
            : Path.GetFullPath(startInfo.WorkingDirectory);
        return CreateDetachedBrokerProcessViaWmi(
            startInfo,
            applicationPath,
            workingDirectory);
    }

    private Process StartDetachedBrokerProcess(ProcessStartInfo startInfo, EventWaitHandle readyEvent)
    {
        ArgumentNullException.ThrowIfNull(readyEvent);
        ValidateTargetStartInfo(startInfo);
        var applicationPath = Path.GetFullPath(startInfo.FileName);
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException("找不到 Windows Job broker 可执行文件。", applicationPath);
        }

        var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Path.GetDirectoryName(applicationPath) ?? Environment.CurrentDirectory
            : Path.GetFullPath(startInfo.WorkingDirectory);

        // Strategy 1: CREATE_BREAKAWAY_FROM_JOB (works for desktop launches and
        // BREAKAWAY_OK outer Jobs). On non-breakaway hosts this fails or races
        // (doomed process may signal ready then die) — always Reset ready first.
        Exception? breakawayFailure = null;
        try
        {
            return CreateDetachedBrokerProcessCore(
                startInfo,
                applicationPath,
                workingDirectory,
                parentProcess: null,
                strategyName: "breakaway");
        }
        catch (WindowsJobObjectException ex) when (
            ex.Code is "JOB_BROKER_BREAKAWAY_CREATE_FAILED"
                or "JOB_BROKER_BREAKAWAY_INCOMPLETE"
                or "JOB_BROKER_EXITED_DURING_BREAKAWAY_VERIFY")
        {
            breakawayFailure = ex;
            _ = readyEvent.Reset();
        }

        // Strategy 2: re-parent to a same-session explorer.exe. Processes created
        // with PROC_THREAD_ATTRIBUTE_PARENT_PROCESS inherit the *parent process*
        // job membership (typically none), which escapes non-breakaway outer Jobs
        // commonly imposed by chat apps, browsers, archive tools, and cloud drives.
        WindowsJobObjectException? explorerFailure = null;
        using var explorerParent = TryOpenSameSessionExplorerParent();
        if (explorerParent is null)
        {
            explorerFailure = new WindowsJobObjectException(
                "JOB_BROKER_EXPLORER_PARENT_UNAVAILABLE",
                "无法定位可用的同会话 explorer 作为备用父进程。",
                $"Session={_currentWindowsSessionId}。");
        }
        else
        {
            try
            {
                _ = readyEvent.Reset();
                return CreateDetachedBrokerProcessCore(
                    startInfo,
                    applicationPath,
                    workingDirectory,
                    parentProcess: explorerParent,
                    strategyName: "explorer-parent");
            }
            catch (WindowsJobObjectException ex)
            {
                explorerFailure = ex;
            }
        }

        // Strategy 3: ask the local WMI provider to create the process. Unlike
        // CreateProcess children, Win32_Process.Create children do not inherit
        // the launcher's Job; the native verification below still requires the
        // exact broker to be outside every Job before it can become a keeper.
        try
        {
            _ = readyEvent.Reset();
            return CreateDetachedBrokerProcessViaWmi(
                startInfo,
                applicationPath,
                workingDirectory);
        }
        catch (WindowsJobObjectException wmiFailure)
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_BREAKAWAY_INCOMPLETE",
                "broker 未完全脱离外层 Windows Job（breakaway、explorer 父进程与本机 WMI 回退均失败）。",
                ComposeDetachedFailureDetails(breakawayFailure, explorerFailure, wmiFailure));
        }
    }

    private static string ComposeDetachedFailureDetails(
        Exception? breakawayFailure,
        Exception? explorerAttempt,
        Exception? wmiAttempt)
    {
        var parts = new List<string>
        {
            $"LauncherPID={Environment.ProcessId}",
        };
        if (breakawayFailure is WindowsJobObjectException breakaway)
        {
            parts.Add($"breakaway={breakaway.Code}:{breakaway.Details}");
        }
        else if (breakawayFailure is not null)
        {
            parts.Add($"breakaway={breakawayFailure.GetType().Name}:{breakawayFailure.Message}");
        }

        if (explorerAttempt is WindowsJobObjectException explorer)
        {
            parts.Add($"explorer-parent={explorer.Code}:{explorer.Details}");
        }
        else if (explorerAttempt is null)
        {
            parts.Add("explorer-parent=unavailable");
        }

        if (wmiAttempt is WindowsJobObjectException wmi)
        {
            parts.Add($"wmi={wmi.Code}:{wmi.Details}");
        }
        else if (wmiAttempt is not null)
        {
            parts.Add($"wmi={wmiAttempt.GetType().Name}:{wmiAttempt.Message}");
        }

        if (wmiAttempt is WindowsJobObjectException
            {
                Code: "JOB_BROKER_WMI_BREAKAWAY_INCOMPLETE",
            })
        {
            parts.Add(
                "说明：WMI 已成功创建 broker，但系统或安全策略仍将其置于 Windows Job；" +
                "严格独立存活模式不可用，调用方可显式选择兼容启动模式。");
        }
        else
        {
            parts.Add(
                "建议：若 wmi 显示 WMI_CALL_FAILED 或 WMI_CREATE_FAILED，请检查 Windows Management Instrumentation 服务、权限与系统策略；" +
                "并确认启动器已完整解压到本地目录。");
        }
        return string.Join(" | ", parts);
    }

    private SafeProcessHandle? TryOpenSameSessionExplorerParent()
    {
        Process[] explorers;
        try
        {
            explorers = Process.GetProcessesByName("explorer");
        }
        catch
        {
            return null;
        }

        SafeProcessHandle? fallback = null;
        try
        {
            foreach (var explorer in explorers)
            {
                try
                {
                    if (explorer.SessionId != _currentWindowsSessionId)
                    {
                        continue;
                    }

                    var handle = OpenProcess(
                        ProcessCreateProcess | ProcessQueryLimitedInformation | Synchronize,
                        false,
                        explorer.Id);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    // Prefer an explorer that is not itself trapped in a Job.
                    if (IsProcessInAnyJob(handle, IntPtr.Zero, out var inJob) && !inJob)
                    {
                        fallback?.Dispose();
                        fallback = null;
                        return handle;
                    }

                    fallback?.Dispose();
                    fallback = handle;
                }
                catch
                {
                    // try next explorer
                }
            }

            return fallback;
        }
        catch
        {
            fallback?.Dispose();
            throw;
        }
        finally
        {
            foreach (var explorer in explorers)
            {
                explorer.Dispose();
            }
        }
    }

    private Process CreateDetachedBrokerProcessViaWmi(
        ProcessStartInfo startInfo,
        string applicationPath,
        string workingDirectory)
    {
        WindowsJobObjectException? firstSetupFailure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return CreateDetachedBrokerProcessViaWmiCore(
                    startInfo,
                    applicationPath,
                    workingDirectory);
            }
            catch (WindowsJobObjectException ex) when (
                ex.Code.Equals("JOB_BROKER_WMI_CALL_FAILED", StringComparison.Ordinal) &&
                !ex.Details.Contains("CreateInvoked=True;", StringComparison.Ordinal))
            {
                if (attempt == 1)
                {
                    firstSetupFailure = ex;
                    Thread.Sleep(50);
                    continue;
                }

                throw new WindowsJobObjectException(
                    ex.Code,
                    ex.Message,
                    $"Attempt1={firstSetupFailure!.Details} | Attempt2={ex.Details}");
            }
        }

        throw new UnreachableException();
    }

    private Process CreateDetachedBrokerProcessViaWmiCore(
        ProcessStartInfo startInfo,
        string applicationPath,
        string workingDirectory)
    {
        SafeProcessHandle? processHandle = null;
        Process? managedProcess = null;
        var exactCreatedProcess = false;
        var completed = false;
        var processCreateInvoked = false;
        var processId = 0;
        const uint wmiCreateFlags = CreateBreakawayFromJob;
        var stage = "resolve-locator";
        try
        {
            stage = "connect-root-cimv2";
            var managementScope = new ManagementScope(@"\\.\root\cimv2");
            managementScope.Connect();
            using var processClass = new ManagementClass(
                managementScope,
                new ManagementPath("Win32_Process"),
                options: null);
            using var startupClass = new ManagementClass(
                managementScope,
                new ManagementPath("Win32_ProcessStartup"),
                options: null);
            stage = "build-startup-information";
            using var startupInformation = startupClass.CreateInstance()
                ?? throw new InvalidOperationException(
                    "WMI Win32_ProcessStartup 未返回启动信息实例。");
            startupInformation["CreateFlags"] = wmiCreateFlags;
            stage = "build-create-input";
            using var createInput = processClass.GetMethodParameters("Create")
                ?? throw new InvalidOperationException(
                    "WMI Win32_Process.Create 未返回输入参数定义。");
            createInput["CommandLine"] = BuildCommandLine(startInfo, applicationPath);
            createInput["CurrentDirectory"] = workingDirectory;
            createInput["ProcessStartupInformation"] = startupInformation;
            stage = "create-process";
            processCreateInvoked = true;
            using var createOutput = processClass.InvokeMethod("Create", createInput, options: null)
                ?? throw new InvalidOperationException(
                    "WMI Win32_Process.Create 未返回输出参数。");
            stage = "read-create-result";
            var returnValue = createOutput["ReturnValue"];
            var processIdValue = createOutput["ProcessId"];
            var returnCode = Convert.ToUInt32(returnValue, CultureInfo.InvariantCulture);
            processId = processIdValue is null
                ? 0
                : checked((int)Convert.ToUInt32(processIdValue, CultureInfo.InvariantCulture));
            if (returnCode != 0 || processId <= 0)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_WMI_CREATE_FAILED",
                    "本机 WMI 无法创建 detached broker。",
                    $"ReturnValue={returnCode}, PID={processId}, " +
                    $"CreateFlags=0x{wmiCreateFlags:X8}。");
            }

            processHandle = OpenProcess(
                ProcessQueryLimitedInformation | ProcessTerminate | Synchronize,
                false,
                processId);
            if (processHandle.IsInvalid)
            {
                throw CreateWin32Exception(
                    "JOB_BROKER_WMI_PROCESS_OPEN_FAILED",
                    "WMI 已返回 broker PID，但无法固定该进程。",
                    Marshal.GetLastPInvokeError(),
                    $"PID={processId}。");
            }

            var identity = CaptureProcessIdentity(processHandle);
            if (!PathsEqual(identity.ExecutablePath, applicationPath))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_WMI_IMAGE_MISMATCH",
                    "WMI 创建的 broker 映像与请求路径不一致。",
                    $"PID={processId}, 请求={applicationPath}，实际={identity.ExecutablePath}。");
            }

            exactCreatedProcess = true;
            if (WaitForSingleObject(processHandle, 0) != WaitTimeout)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_EXITED_DURING_WMI_VERIFY",
                    "broker 在 WMI breakaway 验证期间已退出。",
                    $"PID={processId}。");
            }

            if (!TryReadProcessOwnerSid(processHandle, out var ownerSid) ||
                !ownerSid.Equals(_currentUserSid, StringComparison.Ordinal))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_WMI_OWNER_MISMATCH",
                    "WMI 创建的 broker 不属于当前 Windows 用户。",
                    $"PID={processId}, SID={ownerSid}。");
            }

            var sessionId = GetSessionId(processId);
            if (sessionId != _currentWindowsSessionId)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_WMI_SESSION_MISMATCH",
                    "WMI 创建的 broker 不在当前 Windows session。",
                    $"PID={processId}, expected={_currentWindowsSessionId}, actual={sessionId}。");
            }

            if (!IsProcessInAnyJob(processHandle, IntPtr.Zero, out var isInAnyJob))
            {
                throw CreateWin32Exception(
                    "JOB_BROKER_WMI_JOB_QUERY_FAILED",
                    "无法核验 WMI broker 是否已脱离所有外层 Job。",
                    Marshal.GetLastPInvokeError(),
                    $"PID={processId}。");
            }

            if (isInAnyJob)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_WMI_BREAKAWAY_INCOMPLETE",
                    "WMI broker 仍属于外层 Windows Job。",
                    $"PID={processId}, CreateFlags=0x{wmiCreateFlags:X8}。");
            }

            managedProcess = Process.GetProcessById(processId);
            var result = managedProcess;
            managedProcess = null;
            completed = true;
            return result;
        }
        catch (Exception ex) when (
            ex is ManagementException or COMException or UnauthorizedAccessException or
                InvalidOperationException or FormatException or OverflowException)
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_WMI_CALL_FAILED",
                "调用本机 WMI detached broker 回退失败。",
                $"Stage={stage}; CreateInvoked={processCreateInvoked}; " +
                $"CreateFlags=0x{wmiCreateFlags:X8}; " +
                $"{ex.GetType().Name}: HRESULT=0x{ex.HResult:X8}, {ex.Message}");
        }
        finally
        {
            if (!completed && exactCreatedProcess && processHandle is { IsInvalid: false })
            {
                _ = TerminateProcess(processHandle, BrokerFailureExitCode);
                _ = WaitForSingleObject(processHandle, 5_000);
            }

            managedProcess?.Dispose();
            processHandle?.Dispose();
        }
    }

    private Process CreateDetachedBrokerProcessCore(
        ProcessStartInfo startInfo,
        string applicationPath,
        string workingDirectory,
        SafeProcessHandle? parentProcess,
        string strategyName)
    {
        var commandLine = (BuildCommandLine(startInfo, applicationPath) + '\0').ToCharArray();
        var environment = BuildUnicodeEnvironment(startInfo);
        var processInformation = default(ProcessInformationNative);
        SafeProcessHandle? processHandle = null;
        SafeKernelObjectHandle? threadHandle = null;
        Process? managedProcess = null;
        IntPtr attributeList = IntPtr.Zero;
        var parentHandleValue = parentProcess?.DangerousGetHandle() ?? IntPtr.Zero;
        try
        {
            unsafe
            {
                fixed (char* commandLinePointer = commandLine)
                fixed (char* environmentPointer = environment)
                {
                    var startupInformation = new StartupInformationExNative
                    {
                        StartupInfo = new StartupInformationNative
                        {
                            Size = parentProcess is null
                                ? checked((uint)Marshal.SizeOf<StartupInformationNative>())
                                : checked((uint)Marshal.SizeOf<StartupInformationExNative>()),
                        },
                    };

                    uint creationFlags = CreateUnicodeEnvironment |
                        (startInfo.CreateNoWindow ? CreateNoWindow : 0);

                    if (parentProcess is null)
                    {
                        creationFlags |= CreateBreakawayFromJob;
                    }
                    else
                    {
                        creationFlags |= ExtendedStartupinfoPresent;
                        attributeList = AllocateParentProcessAttributeListCombined(parentHandleValue);
                        startupInformation.AttributeList = attributeList;
                    }

                    if (!CreateProcess(
                            applicationPath,
                            commandLinePointer,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            inheritHandles: false,
                            creationFlags,
                            environmentPointer,
                            workingDirectory,
                            ref startupInformation,
                            out processInformation))
                    {
                        var win32 = Marshal.GetLastPInvokeError();
                        throw CreateWin32Exception(
                            parentProcess is null
                                ? "JOB_BROKER_BREAKAWAY_CREATE_FAILED"
                                : "JOB_BROKER_EXPLORER_PARENT_CREATE_FAILED",
                            parentProcess is null
                                ? "无法从当前 Windows Job 安全脱离并启动 broker。"
                                : "无法以同会话 explorer 为父进程启动 broker。",
                            win32,
                            $"Strategy={strategyName}, LauncherPID={Environment.ProcessId}。");
                    }
                }
            }

            processHandle = new SafeProcessHandle(processInformation.ProcessHandle, ownsHandle: true);
            processInformation.ProcessHandle = IntPtr.Zero;
            threadHandle = new SafeKernelObjectHandle(processInformation.ThreadHandle, ownsHandle: true);
            processInformation.ThreadHandle = IntPtr.Zero;

            if (!IsProcessInAnyJob(processHandle, IntPtr.Zero, out var isInAnyJob))
            {
                throw CreateWin32Exception(
                    "JOB_BROKER_BREAKAWAY_QUERY_FAILED",
                    "无法核验 broker 是否已脱离所有外层 Job。",
                    Marshal.GetLastPInvokeError(),
                    $"Strategy={strategyName}, PID={processInformation.ProcessId}。");
            }

            if (isInAnyJob)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_BREAKAWAY_INCOMPLETE",
                    "broker 未完全脱离外层 Windows Job，已终止该精确 broker。",
                    $"Strategy={strategyName}, PID={processInformation.ProcessId}。");
            }

            if (WaitForSingleObject(processHandle, 0) != WaitTimeout)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_EXITED_DURING_BREAKAWAY_VERIFY",
                    "broker 在 breakaway 验证期间已退出。",
                    $"Strategy={strategyName}, PID={processInformation.ProcessId}。");
            }

            var identity = CaptureProcessIdentity(processHandle);
            if (!PathsEqual(identity.ExecutablePath, applicationPath))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_IMAGE_MISMATCH",
                    "创建的 broker 映像与请求路径不一致。",
                    $"Strategy={strategyName}, 请求={applicationPath}，实际={identity.ExecutablePath}。");
            }

            managedProcess = Process.GetProcessById(checked((int)processInformation.ProcessId));
            var result = managedProcess;
            managedProcess = null;
            return result;
        }
        catch
        {
            if (processHandle is { IsInvalid: false })
            {
                _ = TerminateProcess(processHandle, BrokerFailureExitCode);
                _ = WaitForSingleObject(processHandle, 5_000);
            }

            throw;
        }
        finally
        {
            managedProcess?.Dispose();
            threadHandle?.Dispose();
            processHandle?.Dispose();
            FreeParentProcessAttributeList(attributeList);

            if (processInformation.ThreadHandle != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.ThreadHandle);
            }

            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.ProcessHandle);
            }
        }
    }

    /// <summary>
    /// Allocates attribute list + durable parent HANDLE storage in one block.
    /// Layout: [IntPtr parentHandle][attribute list bytes...]
    /// Returned pointer is the attribute list start; free the base (handle-size before).
    /// </summary>
    private static IntPtr AllocateParentProcessAttributeListCombined(IntPtr parentProcessHandle)
    {
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == 0)
        {
            throw CreateWin32Exception(
                "JOB_BROKER_ATTR_LIST_SIZE_FAILED",
                "无法计算 CreateProcess 属性列表大小。",
                Marshal.GetLastPInvokeError());
        }

        var total = IntPtr.Size + checked((int)size);
        var basePtr = Marshal.AllocHGlobal(total);
        try
        {
            Marshal.WriteIntPtr(basePtr, parentProcessHandle);
            var attributeList = IntPtr.Add(basePtr, IntPtr.Size);
            nuint sizeCopy = size;
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref sizeCopy))
            {
                throw CreateWin32Exception(
                    "JOB_BROKER_ATTR_LIST_INIT_FAILED",
                    "无法初始化 CreateProcess 属性列表。",
                    Marshal.GetLastPInvokeError());
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeParentProcess,
                    basePtr,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                DeleteProcThreadAttributeList(attributeList);
                throw CreateWin32Exception(
                    "JOB_BROKER_ATTR_PARENT_SET_FAILED",
                    "无法设置 explorer 父进程属性。",
                    Marshal.GetLastPInvokeError());
            }

            // Return attribute list pointer; FreeParentProcessAttributeList recovers base.
            return attributeList;
        }
        catch
        {
            Marshal.FreeHGlobal(basePtr);
            throw;
        }
    }

    private static void FreeParentProcessAttributeList(IntPtr attributeList)
    {
        if (attributeList == IntPtr.Zero)
        {
            return;
        }

        DeleteProcThreadAttributeList(attributeList);
        var basePtr = IntPtr.Subtract(attributeList, IntPtr.Size);
        Marshal.FreeHGlobal(basePtr);
    }

    private void VerifyDetachedBrokerForRoot(JobBrokerConnection broker)
    {
        broker.ThrowIfUnavailable(this);
        using var brokerHandle = OpenProcess(
            ProcessQueryLimitedInformation | Synchronize,
            false,
            broker.BrokerProcessId);
        if (brokerHandle.IsInvalid)
        {
            throw CreateWin32Exception(
                "JOB_BROKER_VERIFY_OPEN_FAILED",
                "无法固定 broker 进程以复核隔离状态。",
                Marshal.GetLastPInvokeError(),
                $"PID={broker.BrokerProcessId}。");
        }

        var identity = CaptureProcessIdentity(brokerHandle);
        if (identity.ProcessStartUtcTicks != broker.BrokerProcessStartUtcTicks ||
            !PathsEqual(identity.ExecutablePath, broker.BrokerIdentity.ExecutablePath) ||
            WaitForSingleObject(brokerHandle, 0) != WaitTimeout)
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_VERIFY_IDENTITY_CHANGED",
                "broker 精确进程身份已变化或已经退出。",
                $"PID={broker.BrokerProcessId}。");
        }

        if (!IsProcessInAnyJob(brokerHandle, IntPtr.Zero, out var isInAnyJob))
        {
            throw CreateWin32Exception(
                "JOB_BROKER_VERIFY_JOB_QUERY_FAILED",
                "无法复核 broker 是否仍脱离所有外层 Job。",
                Marshal.GetLastPInvokeError(),
                $"PID={broker.BrokerProcessId}。");
        }

        if (isInAnyJob)
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_VERIFY_IN_OUTER_JOB",
                "broker 已被加入外层 Job，拒绝继续创建 Codex root。",
                $"PID={broker.BrokerProcessId}。");
        }
    }

    private PinnedReceiptBinding PinAndVerifyReceipt(
        RunningInstanceReceipt receipt,
        bool allowRecoveredBroker)
    {
        if (!ProcessOwnershipModes.IsWindowsJob(receipt) ||
            string.IsNullOrWhiteSpace(receipt.JobObjectName) ||
            string.IsNullOrWhiteSpace(receipt.ReadyEventName) ||
            receipt.WindowsSessionId < 0)
        {
            throw ReceiptNotConfirmed(
                "RECEIPT_OWNERSHIP_SHAPE_INVALID",
                "receipt 缺少可绑定的 Windows Job ownership/session 字段。");
        }

        WindowsJobNames names;
        try
        {
            names = NamesFromPersisted(receipt.JobObjectName, receipt.ReadyEventName);
            ValidateReceiptIds(receipt, names);
        }
        catch (ArgumentException ex)
        {
            throw ReceiptNotConfirmed("RECEIPT_NAMES_INVALID", ex.Message);
        }

        SafeJobHandle? jobHandle = null;
        SafeProcessHandle? brokerHandle = null;
        EventWaitHandle? readyEvent = null;
        EventWaitHandle? cancelEvent = null;
        try
        {
            jobHandle = OpenJob(
                names.JobObjectName,
                JobObjectQuery | JobObjectTerminate,
                allowMissing: true);
            if (jobHandle is null)
            {
                throw ReceiptNotConfirmed(
                    "RECEIPT_JOB_NOT_OPENABLE",
                    "Global Job 名称不可打开；必须交由 missing-generation drain 判定。");
            }

            if ((QueryLimitFlags(jobHandle) & JobObjectLimitKillOnJobClose) == 0)
            {
                throw ReceiptNotConfirmed(
                    "RECEIPT_JOB_KILL_FLAG_MISSING",
                    "pinned Job 未启用 KILL_ON_JOB_CLOSE。");
            }

            var snapshot = Inspect(jobHandle);
            if (snapshot.InspectionErrors.Count != 0)
            {
                throw ReceiptNotConfirmed(
                    "RECEIPT_JOB_MEMBER_INSPECTION_FAILED",
                    string.Join(" ", snapshot.InspectionErrors),
                    snapshot.ProcessIds);
            }

            if (snapshot.Members.Any(member =>
                    member.WindowsSessionId != receipt.WindowsSessionId))
            {
                throw ReceiptNotConfirmed(
                    "RECEIPT_MEMBER_SESSION_MISMATCH",
                    "pinned Job 中存在与 receipt Windows session 不一致的成员。",
                    snapshot.ProcessIds);
            }

            if (receipt.RootProcessId > 0)
            {
                var samePidMember = snapshot.Members.FirstOrDefault(member =>
                    member.ProcessId == receipt.RootProcessId);
                if (samePidMember is not null &&
                    (samePidMember.ProcessStartUtcTicks != receipt.ProcessStartUtcTicks ||
                     !PathsEqual(samePidMember.ExecutablePath, receipt.ExecutablePath)))
                {
                    throw ReceiptNotConfirmed(
                        "RECEIPT_ROOT_GENERATION_MISMATCH",
                        "pinned Job 中同 PID 成员与 receipt root generation 不匹配。",
                        snapshot.ProcessIds);
                }
            }

            readyEvent = OpenRequiredEvent(names.ReadyEventName, "RECEIPT_READY_EVENT_MISSING");
            if (!readyEvent.WaitOne(0))
            {
                throw ReceiptNotConfirmed(
                    "RECEIPT_READY_NOT_SIGNALED",
                    "ready event 尚未置位，不能授权 normal receipt 操作。",
                    snapshot.ProcessIds);
            }

            cancelEvent = OpenRequiredEvent(names.CancelEventName, "RECEIPT_CANCEL_EVENT_MISSING");
            WindowsJobBrokerIdentity expectedBroker;
            if (receipt.BrokerProcessId > 0 && receipt.BrokerProcessStartUtcTicks > 0)
            {
                expectedBroker = new(
                    receipt.BrokerProcessId,
                    receipt.BrokerProcessStartUtcTicks,
                    _brokerExecutablePath,
                    receipt.WindowsSessionId,
                    names.JobObjectName,
                    names.ReadyEventName,
                    names.CancelEventName);
            }
            else
            {
                if (!allowRecoveredBroker)
                {
                    throw ReceiptNotConfirmed(
                        "RECEIPT_BROKER_IDENTITY_MISSING",
                        "该入口要求 receipt 已持久化 exact broker PID/start。");
                }

                var errors = new List<string>();
                var candidates = FindBrokerCandidates(names, errors);
                if (errors.Count != 0 || candidates.Count != 1)
                {
                    throw ReceiptNotConfirmed(
                        "RECEIPT_BROKER_RECOVERY_NOT_UNIQUE",
                        errors.Count == 0
                            ? $"exact broker 候选数量={candidates.Count}；必须恰好为1。"
                            : string.Join(" ", errors),
                        snapshot.ProcessIds);
                }

                expectedBroker = candidates[0].Identity;
            }

            brokerHandle = OpenProcess(
                ProcessQueryLimitedInformation | Synchronize,
                false,
                expectedBroker.ProcessId);
            if (brokerHandle.IsInvalid)
            {
                throw ReceiptNotConfirmed(
                    "RECEIPT_BROKER_NOT_OPENABLE",
                    $"无法 pin exact broker process handle：Win32={Marshal.GetLastPInvokeError()}。",
                    snapshot.ProcessIds);
            }

            _ = VerifyBrokerHandle(
                brokerHandle,
                expectedBroker.ProcessId,
                names,
                expectedBroker.ProcessStartUtcTicks,
                receipt.WindowsSessionId);
            var binding = new PinnedReceiptBinding(
                jobHandle,
                brokerHandle,
                readyEvent,
                cancelEvent,
                snapshot.ProcessIds);
            jobHandle = null;
            brokerHandle = null;
            readyEvent = null;
            cancelEvent = null;
            return binding;
        }
        catch
        {
            jobHandle?.Dispose();
            brokerHandle?.Dispose();
            readyEvent?.Dispose();
            cancelEvent?.Dispose();
            throw;
        }
    }

    private WindowsJobBrokerRequest VerifyBrokerHandle(
        SafeProcessHandle brokerHandle,
        int processId,
        WindowsJobNames names,
        long expectedStartUtcTicks,
        int expectedSessionId)
    {
        var waitState = WaitForSingleObject(brokerHandle, 0);
        if (waitState != WaitTimeout)
        {
            throw ReceiptNotConfirmed(
                waitState == WaitObject0
                    ? "PINNED_BROKER_ALREADY_EXITED"
                    : "PINNED_BROKER_WAIT_FAILED",
                waitState == WaitObject0
                    ? "exact broker 已退出。"
                    : $"无法读取 exact broker wait state：value=0x{waitState:X8}, Win32={Marshal.GetLastPInvokeError()}。");
        }

        var actual = CaptureProcessIdentity(brokerHandle);
        if (actual.ProcessStartUtcTicks != expectedStartUtcTicks ||
            !IsCompatibleBrokerImage(actual.ExecutablePath))
        {
            throw ReceiptNotConfirmed(
                "PINNED_BROKER_GENERATION_MISMATCH",
                "pinned broker 的创建时间或可执行路径不匹配。");
        }

        if (!TryReadProcessOwnerSid(brokerHandle, out var ownerSid) ||
            !ownerSid.Equals(_currentUserSid, StringComparison.Ordinal))
        {
            throw ReceiptNotConfirmed(
                "PINNED_BROKER_OWNER_MISMATCH",
                "pinned broker 不属于当前 Windows 用户。");
        }

        var actualSessionId = GetSessionId(processId);
        if (actualSessionId != expectedSessionId)
        {
            throw ReceiptNotConfirmed(
                "PINNED_BROKER_SESSION_MISMATCH",
                $"broker session 不匹配：expected={expectedSessionId}, actual={actualSessionId}。");
        }

        var arguments = WindowsProcessInspector.ParseCommandLine(ReadCommandLine(brokerHandle));
        if (arguments.Count == 0 ||
            !WindowsJobBroker.TryParseRequest(arguments.Skip(1).ToArray(), out var request) ||
            request is null ||
            !request.JobObjectName.Equals(names.JobObjectName, StringComparison.Ordinal) ||
            !request.ReadyEventName.Equals(names.ReadyEventName, StringComparison.Ordinal) ||
            !request.CancelEventName.Equals(names.CancelEventName, StringComparison.Ordinal))
        {
            throw ReceiptNotConfirmed(
                "PINNED_BROKER_COMMAND_LINE_MISMATCH",
                "pinned broker 命令行与 receipt 命名对象不匹配。");
        }

        if (!IsProcessInAnyJob(brokerHandle, IntPtr.Zero, out var brokerIsInAnyJob))
        {
            throw ReceiptNotConfirmed(
                "PINNED_BROKER_JOB_QUERY_FAILED",
                $"IsProcessInJob Win32={Marshal.GetLastPInvokeError()}。");
        }

        if (brokerIsInAnyJob)
        {
            throw ReceiptNotConfirmed(
                "PINNED_BROKER_IN_ANY_JOB",
                "exact broker 属于外层或 inner Job，不能作为独立 keeper 授权 receipt 操作。");
        }

        return request;
    }

    private static EventWaitHandle OpenRequiredEvent(string name, string missingCode)
    {
        try
        {
            return EventWaitHandle.OpenExisting(name);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            throw ReceiptNotConfirmed(missingCode, $"命名 event 不存在：{name}。");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw ReceiptNotConfirmed(
                missingCode,
                $"命名 event 无法认证打开：{name}，{ex.Message}");
        }
    }

    private static ExactProcessGenerationInspection InspectExactProcessGeneration(
        int processId,
        long expectedStartUtcTicks)
    {
        using var processHandle = OpenProcess(
            ProcessQueryLimitedInformation | Synchronize,
            false,
            processId);
        if (processHandle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == 87
                ? new(
                    ExactProcessGenerationState.DefinitelyAbsent,
                    "exact parent PID 不存在。")
                : new(
                    ExactProcessGenerationState.InspectionError,
                    $"无法打开 exact parent：PID={processId}, Win32={error}。");
        }

        try
        {
            var actual = CaptureProcessIdentity(processHandle);
            if (actual.ProcessStartUtcTicks != expectedStartUtcTicks)
            {
                return new(
                    ExactProcessGenerationState.DefinitelyAbsent,
                    "parent PID 已被其它 generation 使用；expected generation 已不存在。");
            }

            var waitState = WaitForSingleObject(processHandle, 0);
            return waitState switch
            {
                WaitObject0 => new(
                    ExactProcessGenerationState.DefinitelyAbsent,
                    "exact parent generation 已退出。"),
                WaitTimeout => new(
                    ExactProcessGenerationState.VerifiedLive,
                    "exact parent generation 仍存活。"),
                _ => new(
                    ExactProcessGenerationState.InspectionError,
                    $"exact parent wait state 无法确认：value=0x{waitState:X8}, Win32={Marshal.GetLastPInvokeError()}。"),
            };
        }
        catch (WindowsJobObjectException ex)
        {
            return new(
                ExactProcessGenerationState.InspectionError,
                $"exact parent identity 检查失败：{ex.Message} {ex.Details}");
        }
    }

    private static async Task<bool> WaitForProcessHandleExitAsync(
        SafeProcessHandle processHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waitState = WaitForSingleObject(processHandle, 0);
            if (waitState == WaitObject0)
            {
                return true;
            }

            if (waitState != WaitTimeout)
            {
                return false;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return WaitForSingleObject(processHandle, 0) == WaitObject0;
    }

    private static ReceiptJobOperationResult ReceiptOperationNotConfirmed(
        string code,
        string details,
        IReadOnlyList<int>? processIds = null) =>
        new(
            ReceiptJobOperationState.NotConfirmed,
            code,
            details,
            processIds ?? []);

    private static PendingUnreadyReclaimResult PendingReclaimNotConfirmed(
        string code,
        string details) =>
        new(PendingUnreadyReclaimState.NotConfirmed, code, details);

    private static string FormatInspectionException(Exception exception) =>
        exception is WindowsJobObjectException jobError
            ? $"{jobError.Message} {jobError.Details}"
            : exception.Message;

    private static ReceiptJobNotConfirmedException ReceiptNotConfirmed(
        string code,
        string details,
        IReadOnlyList<int>? memberProcessIds = null) =>
        new(code, details, memberProcessIds ?? []);

    private WindowsJobInspection Inspect(SafeJobHandle jobHandle)
    {
        var errors = new List<string>();
        var members = new List<WindowsJobProcessIdentity>();
        var processIds = QueryProcessIds(jobHandle);
        foreach (var processId in processIds)
        {
            using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle.IsInvalid)
            {
                errors.Add($"PID={processId}: OpenProcess Win32={Marshal.GetLastPInvokeError()}。");
                continue;
            }

            if (!IsProcessInJob(processHandle, jobHandle, out var isInJob))
            {
                errors.Add($"PID={processId}: IsProcessInJob Win32={Marshal.GetLastPInvokeError()}。");
                continue;
            }

            if (!isInJob)
            {
                errors.Add($"PID={processId}: 快照后已不属于目标 Job。");
                continue;
            }

            try
            {
                var identity = CaptureProcessIdentity(processHandle);
                members.Add(new(
                    processId,
                    identity.ProcessStartUtcTicks,
                    identity.ExecutablePath,
                    GetSessionId(processId)));
            }
            catch (WindowsJobObjectException ex)
            {
                errors.Add($"PID={processId}: {ex.Message} {ex.Details}");
            }
        }

        return new(
            true,
            (QueryLimitFlags(jobHandle) & JobObjectLimitKillOnJobClose) != 0,
            members.OrderBy(member => member.ProcessId).ToArray(),
            errors,
            _currentWindowsSessionId);
    }

    private WindowsJobInspection MissingInspection() =>
        new(false, false, [], [], _currentWindowsSessionId);

    private WindowsNamedSignalInspection InspectEvent(string name, bool isReady)
    {
        if (isReady)
        {
            ValidateReadyEventName(name, _currentUserSid);
        }
        else
        {
            ValidateCancelEventName(name, _currentUserSid);
        }

        try
        {
            using var signal = EventWaitHandle.OpenExisting(name);
            return new(true, signal.WaitOne(0), null);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return new(false, false, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(false, false, $"Event={name}: {ex.Message}");
        }
    }

    private List<BrokerCandidate> FindBrokerCandidates(
        WindowsJobNames names,
        List<string> errors)
    {
        var candidates = new List<BrokerCandidate>();
        var processName = Path.GetFileNameWithoutExtension(_brokerExecutablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var identity = CaptureBrokerIdentity(process.Id, names, requireCommandLine: false);
                    if (!IsCompatibleBrokerImage(identity.ExecutablePath))
                    {
                        continue;
                    }

                    var commandLine = ReadCommandLine(process.Id);
                    var arguments = WindowsProcessInspector.ParseCommandLine(commandLine);
                    if (arguments.Count == 0 ||
                        !WindowsJobBroker.TryParseRequest(arguments.Skip(1).ToArray(), out var request) ||
                        request is null)
                    {
                        continue;
                    }

                    if (request.JobObjectName.Equals(names.JobObjectName, StringComparison.Ordinal) &&
                        request.ReadyEventName.Equals(names.ReadyEventName, StringComparison.Ordinal) &&
                        request.CancelEventName.Equals(names.CancelEventName, StringComparison.Ordinal))
                    {
                        candidates.Add(new(identity, request));
                    }
                }
                catch (WindowsJobObjectException ex) when (
                    ex.Code is "BROKER_IMAGE_MISMATCH" or "BROKER_OWNER_MISMATCH")
                {
                    // A same-basename process from another installation/user
                    // is a definite non-candidate, not an inspection failure
                    // for this broker name.
                    continue;
                }
                catch (Exception ex) when (
                    ex is WindowsJobObjectException or Win32Exception or ArgumentException or InvalidOperationException)
                {
                    errors.Add($"PID={process.Id}: broker 候选检查失败：{ex.Message}");
                }
            }
        }

        return candidates;
    }

    private WindowsJobBrokerIdentity CaptureBrokerIdentity(
        int processId,
        WindowsJobNames names,
        bool requireCommandLine)
    {
        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid)
        {
            throw CreateWin32Exception(
                "BROKER_PROCESS_OPEN_FAILED",
                "无法打开 broker 进程。",
                Marshal.GetLastPInvokeError(),
                $"PID={processId}。");
        }

        var nativeIdentity = CaptureProcessIdentity(processHandle);
        if (!IsCompatibleBrokerImage(nativeIdentity.ExecutablePath))
        {
            throw new WindowsJobObjectException(
                "BROKER_IMAGE_MISMATCH",
                "broker 可执行路径不匹配。",
                $"期望同名启动器（当前={_brokerExecutablePath}），实际={nativeIdentity.ExecutablePath}。");
        }

        if (!TryReadProcessOwnerSid(processHandle, out var ownerSid) ||
            !ownerSid.Equals(_currentUserSid, StringComparison.Ordinal))
        {
            throw new WindowsJobObjectException(
                "BROKER_OWNER_MISMATCH",
                "broker 不属于当前 Windows 用户。",
                $"PID={processId}, SID={ownerSid}。");
        }

        var sessionId = GetSessionId(processId);
        if (requireCommandLine)
        {
            var commandLine = ReadCommandLine(processId);
            var arguments = WindowsProcessInspector.ParseCommandLine(commandLine);
            if (arguments.Count == 0 ||
                !WindowsJobBroker.TryParseRequest(arguments.Skip(1).ToArray(), out var request) ||
                request is null ||
                !request.JobObjectName.Equals(names.JobObjectName, StringComparison.Ordinal) ||
                !request.ReadyEventName.Equals(names.ReadyEventName, StringComparison.Ordinal) ||
                !request.CancelEventName.Equals(names.CancelEventName, StringComparison.Ordinal))
            {
                throw new WindowsJobObjectException(
                    "BROKER_COMMAND_LINE_MISMATCH",
                    "broker 命令行与预期命名对象不匹配。",
                    $"PID={processId}。");
            }
        }

        return new(
            processId,
            nativeIdentity.ProcessStartUtcTicks,
            nativeIdentity.ExecutablePath,
            sessionId,
            names.JobObjectName,
            names.ReadyEventName,
            names.CancelEventName);
    }

    private static string ReadCommandLine(int processId)
    {
        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return ReadCommandLine(processHandle);
    }

    private static string ReadCommandLine(SafeProcessHandle processHandle)
    {
        _ = NtQueryInformationProcess(
            processHandle,
            ProcessCommandLineInformation,
            IntPtr.Zero,
            0,
            out var requiredLength);
        if (requiredLength <= Marshal.SizeOf<UnicodeStringNative>())
        {
            throw new Win32Exception("NtQueryInformationProcess 未返回命令行缓冲区大小。");
        }

        var buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            var status = NtQueryInformationProcess(
                processHandle,
                ProcessCommandLineInformation,
                buffer,
                requiredLength,
                out _);
            if (status != 0)
            {
                throw new Win32Exception($"NtQueryInformationProcess NTSTATUS=0x{status:X8}。");
            }

            var value = Marshal.PtrToStructure<UnicodeStringNative>(buffer);
            if (value.Length == 0 || value.Buffer == IntPtr.Zero)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadProcessOwnerSid(SafeProcessHandle processHandle, out string ownerSid)
    {
        ownerSid = string.Empty;
        if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            return false;
        }

        using (tokenHandle)
        {
            _ = GetTokenInformation(
                tokenHandle,
                TokenUserInformationClass,
                IntPtr.Zero,
                0,
                out var requiredLength);
            if (requiredLength <= 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(
                        tokenHandle,
                        TokenUserInformationClass,
                        buffer,
                        requiredLength,
                        out _))
                {
                    return false;
                }

                var tokenUser = Marshal.PtrToStructure<TokenUserNative>(buffer);
                ownerSid = new SecurityIdentifier(tokenUser.User.Sid).Value;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static async Task<bool> WaitForStableEmptyAsync(
        SafeJobHandle jobHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        var stableEmptySamples = 0;
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stableEmptySamples = QueryProcessIds(jobHandle).Count == 0
                ? stableEmptySamples + 1
                : 0;
            if (stableEmptySamples >= StableEmptySampleCount)
            {
                return true;
            }

            await Task.Delay(StableSampleDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static uint QueryLimitFlags(SafeJobHandle jobHandle)
    {
        var information = new JobObjectExtendedLimitInformationNative();
        var size = checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>());
        if (!QueryInformationJobObject(
                jobHandle,
                JobObjectExtendedLimitInformation,
                ref information,
                size,
                out _))
        {
            throw CreateWin32Exception(
                "JOB_LIMIT_QUERY_FAILED",
                "无法查询 Job Object 扩展限制。",
                Marshal.GetLastPInvokeError());
        }

        return information.BasicLimitInformation.LimitFlags;
    }

    private static SafeJobHandle? OpenJob(string name, uint access, bool allowMissing)
    {
        var jobHandle = OpenJobObject(access, false, name);
        if (!jobHandle.IsInvalid)
        {
            return jobHandle;
        }

        var error = Marshal.GetLastPInvokeError();
        jobHandle.Dispose();
        if (allowMissing && error == ErrorFileNotFound)
        {
            return null;
        }

        throw CreateWin32Exception(
            error == ErrorAccessDenied ? "JOB_OPEN_ACCESS_DENIED" : "JOB_OPEN_FAILED",
            "无法按名称打开 Windows Job Object。",
            error,
            $"Job={name}。名称不可打开绝不等价于旧成员已停止。");
    }

    private static WindowsJobNames NamesFromPersisted(
        string jobObjectName,
        string readyEventName)
    {
        var sid = CurrentUserSid();
        var (_, launchId) = ValidateJobName(jobObjectName, sid);
        ValidateReadyEventName(readyEventName, sid, launchId);
        return new(
            jobObjectName,
            readyEventName,
            $@"Global\CodexProfileLauncher.JobCancel.v1.{sid}.{launchId:N}");
    }

    internal static string CreateControlPipeName(string jobObjectName)
    {
        var sid = CurrentUserSid();
        var (_, launchId) = ValidateJobName(jobObjectName, sid);
        return WindowsJobBrokerProtocol.CreatePipeName(sid, launchId);
    }

    private static void ValidateReceiptIds(
        RunningInstanceReceipt receipt,
        WindowsJobNames names)
    {
        var (profileId, launchId) = ValidateJobName(names.JobObjectName, CurrentUserSid());
        if (receipt.ProfileId != profileId || receipt.LaunchId != launchId)
        {
            throw new ArgumentException(
                "receipt ProfileId/LaunchId 与 Global Job 名称 generation 不匹配。",
                nameof(receipt));
        }
    }

    private static (Guid ProfileId, Guid LaunchId) ValidateJobName(
        string name,
        string currentUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var prefix = $@"Global\CodexProfileLauncher.Job.v1.{currentUserSid}.";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Job Object 名称必须使用当前 SID 的 Global CodexProfileLauncher v1 格式。",
                nameof(name));
        }

        var components = name[prefix.Length..].Split('.', StringSplitOptions.None);
        if (components.Length != 2 ||
            !Guid.TryParseExact(components[0], "N", out var profileId) ||
            !Guid.TryParseExact(components[1], "N", out var launchId) ||
            profileId == Guid.Empty ||
            launchId == Guid.Empty)
        {
            throw new ArgumentException("Job Object 名称中的 profile/launch UUID 无效。", nameof(name));
        }

        return (profileId, launchId);
    }

    private static void ValidateReadyEventName(
        string name,
        string currentUserSid,
        Guid? expectedLaunchId = null) =>
        ValidateEventName(
            name,
            $@"Global\CodexProfileLauncher.JobReady.v1.{currentUserSid}.",
            expectedLaunchId,
            nameof(name));

    private static void ValidateCancelEventName(
        string name,
        string currentUserSid,
        Guid? expectedLaunchId = null) =>
        ValidateEventName(
            name,
            $@"Global\CodexProfileLauncher.JobCancel.v1.{currentUserSid}.",
            expectedLaunchId,
            nameof(name));

    private static void ValidateEventName(
        string name,
        string prefix,
        Guid? expectedLaunchId,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out var launchId) ||
            launchId == Guid.Empty ||
            expectedLaunchId is not null && launchId != expectedLaunchId.Value)
        {
            throw new ArgumentException(
                "Event 名称必须使用当前 SID/launch UUID 的 Global CodexProfileLauncher v1 格式。",
                parameterName);
        }
    }

    private static EventWaitHandle CreateUniqueManualResetEvent(string name)
    {
        var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name, out var createdNew);
        if (createdNew)
        {
            return signal;
        }

        signal.Dispose();
        throw new WindowsJobObjectException(
            "JOB_SIGNAL_COLLISION",
            "Windows Job broker 信号名称发生冲突。",
            $"Event={name}。");
    }

    private static void ValidateTargetStartInfo(ProcessStartInfo startInfo)
    {
        if (startInfo.UseShellExecute)
        {
            throw new ArgumentException("broker 挂起启动要求 UseShellExecute=false。", nameof(startInfo));
        }

        if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            throw new ArgumentException("broker 挂起启动只接受结构化 ArgumentList。", nameof(startInfo));
        }

        if (startInfo.RedirectStandardError ||
            startInfo.RedirectStandardInput ||
            startInfo.RedirectStandardOutput)
        {
            throw new ArgumentException("当前原子 Job 启动不支持标准流重定向。", nameof(startInfo));
        }
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo, string applicationPath)
    {
        var arguments = new List<string>(startInfo.ArgumentList.Count + 1) { applicationPath };
        arguments.AddRange(startInfo.ArgumentList);
        var commandLine = string.Join(' ', arguments.Select(QuoteCommandLineArgument));
        if (commandLine.Length >= MaximumCommandLineCharacters)
        {
            throw new ArgumentException("Windows 命令行超过 32,767 字符限制。", nameof(startInfo));
        }

        return commandLine;
    }

    internal static string QuoteCommandLineArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 &&
            !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new System.Text.StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
            }
            else if (character == '"')
            {
                result.Append('\\', checked(backslashes * 2 + 1));
                result.Append('"');
                backslashes = 0;
            }
            else
            {
                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(character);
            }
        }

        result.Append('\\', checked(backslashes * 2));
        result.Append('"');
        return result.ToString();
    }

    private static char[] BuildUnicodeEnvironment(ProcessStartInfo startInfo)
    {
        var entries = startInfo.Environment
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                if (entry.Key.Length == 0 ||
                    entry.Key.Contains('=') ||
                    entry.Key.Contains('\0') ||
                    entry.Value?.Contains('\0') == true)
                {
                    throw new ArgumentException($"环境变量名称或值无效：{entry.Key}", nameof(startInfo));
                }

                return $"{entry.Key}={entry.Value ?? string.Empty}\0";
            });
        return (string.Concat(entries) + '\0').ToCharArray();
    }

    private static NativeProcessIdentity CaptureProcessIdentity(SafeProcessHandle processHandle)
    {
        if (!GetProcessTimes(processHandle, out var creationTime, out _, out _, out _))
        {
            throw CreateWin32Exception(
                "PROCESS_START_TIME_QUERY_FAILED",
                "无法读取进程创建时间。",
                Marshal.GetLastPInvokeError());
        }

        var fileTime = ((long)creationTime.HighDateTime << 32) | creationTime.LowDateTime;
        var pathBuffer = new char[MaximumImagePathCharacters];
        var pathLength = checked((uint)pathBuffer.Length);
        unsafe
        {
            fixed (char* pathPointer = pathBuffer)
            {
                if (!QueryFullProcessImageName(processHandle, 0, pathPointer, ref pathLength))
                {
                    throw CreateWin32Exception(
                        "PROCESS_IMAGE_QUERY_FAILED",
                        "无法读取进程可执行文件路径。",
                        Marshal.GetLastPInvokeError());
                }
            }
        }

        return new(
            DateTime.FromFileTimeUtc(fileTime).Ticks,
            new string(pathBuffer, 0, checked((int)pathLength)));
    }

    private static int GetSessionId(int processId)
    {
        if (!ProcessIdToSessionId(checked((uint)processId), out var sessionId))
        {
            throw CreateWin32Exception(
                "PROCESS_SESSION_QUERY_FAILED",
                "无法读取进程 Windows session。",
                Marshal.GetLastPInvokeError(),
                $"PID={processId}。");
        }

        return checked((int)sessionId);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts the current launcher image or another build of the same product
    /// basename. Exact Job ownership still requires matching --job-broker names
    /// and PID/start generation, so a different install path must not block
    /// close/reconcile of an already-running environment.
    /// </summary>
    private bool IsCompatibleBrokerImage(string actualExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(actualExecutablePath))
        {
            return false;
        }

        if (PathsEqual(actualExecutablePath, _brokerExecutablePath))
        {
            return true;
        }

        var expectedName = Path.GetFileName(_brokerExecutablePath);
        var actualName = Path.GetFileName(actualExecutablePath);
        return !string.IsNullOrEmpty(expectedName) &&
               expectedName.Equals(actualName, StringComparison.OrdinalIgnoreCase);
    }

    private void TryKillExactBroker(Process? brokerProcess)
    {
        if (brokerProcess is null)
        {
            return;
        }

        try
        {
            brokerProcess.Refresh();
            if (!brokerProcess.HasExited &&
                IsCompatibleBrokerImage(brokerProcess.MainModule?.FileName ?? string.Empty))
            {
                brokerProcess.Kill(entireProcessTree: false);
                _ = brokerProcess.WaitForExit(5_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException)
        {
            // Setup already failed. The broker also monitors the exact parent
            // and its fresh empty job, so failure remains bounded and visible
            // through the original exception.
        }
    }

    private static WindowsJobObjectException CreateWin32Exception(
        string code,
        string message,
        int error,
        string? context = null)
    {
        var details = $"Win32={error} ({new Win32Exception(error).Message})";
        if (!string.IsNullOrWhiteSpace(context))
        {
            details += $" {context}";
        }

        return new(code, message, details);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformationNative
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationExNative
    {
        public StartupInformationNative StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformationNative
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformationNative
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCountersNative
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformationNative BasicLimitInformation;
        public IoCountersNative IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        public readonly uint LowDateTime;
        public readonly uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeStringNative
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributesNative
    {
        public readonly IntPtr Sid;
        public readonly uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenUserNative
    {
        public readonly SidAndAttributesNative User;
    }

    private sealed record NativeProcessIdentity(long ProcessStartUtcTicks, string ExecutablePath);

    private sealed record BrokerCandidate(
        WindowsJobBrokerIdentity Identity,
        WindowsJobBrokerRequest Request);

    private enum ExactProcessGenerationState
    {
        VerifiedLive,
        DefinitelyAbsent,
        InspectionError,
    }

    private sealed record ExactProcessGenerationInspection(
        ExactProcessGenerationState State,
        string Details);

    private sealed class ReceiptJobNotConfirmedException : Exception
    {
        public ReceiptJobNotConfirmedException(
            string code,
            string message,
            IReadOnlyList<int> memberProcessIds) : base(message)
        {
            Code = code;
            MemberProcessIds = memberProcessIds;
        }

        public string Code { get; }

        public IReadOnlyList<int> MemberProcessIds { get; }
    }

    private sealed class PinnedReceiptBinding : IDisposable
    {
        public PinnedReceiptBinding(
            SafeJobHandle jobHandle,
            SafeProcessHandle brokerHandle,
            EventWaitHandle readyEvent,
            EventWaitHandle cancelEvent,
            IReadOnlyList<int> memberProcessIds)
        {
            JobHandle = jobHandle;
            BrokerHandle = brokerHandle;
            ReadyEvent = readyEvent;
            CancelEvent = cancelEvent;
            MemberProcessIds = memberProcessIds;
        }

        public SafeJobHandle JobHandle { get; }

        public SafeProcessHandle BrokerHandle { get; }

        public EventWaitHandle ReadyEvent { get; }

        public EventWaitHandle CancelEvent { get; }

        public IReadOnlyList<int> MemberProcessIds { get; }

        public void Dispose()
        {
            CancelEvent.Dispose();
            ReadyEvent.Dispose();
            BrokerHandle.Dispose();
            JobHandle.Dispose();
        }
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    internal sealed class SafeKernelObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelObjectHandle() : base(true) { }

        public SafeKernelObjectHandle(IntPtr value, bool ownsHandle) : base(ownsHandle) =>
            SetHandle(value);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    public sealed class JobBrokerConnection : IDisposable
    {
        private readonly WindowsJobObjectManager _owner;
        private Process? _brokerProcess;
        private EventWaitHandle? _readyEvent;
        private EventWaitHandle? _cancelEvent;
        private bool _memberResumed;
        private int _createClaimed;
        private bool _disposed;

        internal JobBrokerConnection(
            WindowsJobObjectManager owner,
            WindowsJobNames names,
            Process brokerProcess,
            WindowsJobBrokerIdentity brokerIdentity,
            EventWaitHandle readyEvent,
            EventWaitHandle cancelEvent)
        {
            _owner = owner;
            Names = names;
            ControlPipeName = CreateControlPipeName(names.JobObjectName);
            _brokerProcess = brokerProcess;
            BrokerIdentity = brokerIdentity;
            _readyEvent = readyEvent;
            _cancelEvent = cancelEvent;
        }

        public WindowsJobNames Names { get; }

        internal string ControlPipeName { get; }

        public WindowsJobBrokerIdentity BrokerIdentity { get; }

        public int BrokerProcessId => BrokerIdentity.ProcessId;

        public long BrokerProcessStartUtcTicks => BrokerIdentity.ProcessStartUtcTicks;

        public int WindowsSessionId => BrokerIdentity.WindowsSessionId;

        public bool IsReadySignaled => _readyEvent?.WaitOne(0) == true;

        internal void ThrowIfUnavailable(WindowsJobObjectManager manager)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(_owner, manager))
            {
                throw new ArgumentException("Job broker connection 属于另一 manager 实例。", nameof(manager));
            }

            _brokerProcess!.Refresh();
            if (_brokerProcess.HasExited ||
                _brokerProcess.StartTime.ToUniversalTime().Ticks != BrokerProcessStartUtcTicks)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_UNAVAILABLE",
                    "Windows Job broker 已退出或 PID 身份变化。",
                    $"PID={BrokerProcessId}。");
            }
        }

        internal void ClaimCreate(WindowsJobObjectManager manager)
        {
            ThrowIfUnavailable(manager);
            if (Interlocked.CompareExchange(ref _createClaimed, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "每个 fresh JobBrokerConnection 只能消费一次进程创建事务。");
            }
        }

        internal void MarkMemberResumed()
        {
            _memberResumed = true;
            // This signal means the setup window is closed. The broker still
            // continuously queries membership and never relies on the event
            // to authorize a member; the signal only prevents a very short
            // create/resume/terminate lifecycle from leaking an empty keeper
            // when both non-empty samples fall between broker polls.
            _ = _cancelEvent!.Set();
        }

        public async Task<bool> AbortEmptySetupAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_memberResumed)
            {
                throw new InvalidOperationException("成员已恢复；请使用 ResumedJobProcessTransaction.AbortAfterResumeAsync。");
            }

            _ = _cancelEvent!.Set();
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _brokerProcess!.Refresh();
                if (_brokerProcess.HasExited)
                {
                    return true;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_memberResumed)
            {
                _ = _cancelEvent?.Set();
            }

            _readyEvent?.Dispose();
            _cancelEvent?.Dispose();
            _brokerProcess?.Dispose();
            _readyEvent = null;
            _cancelEvent = null;
            _brokerProcess = null;
        }
    }

    public sealed class ResumedJobProcessTransaction : IDisposable
    {
        private SafeJobHandle? _jobHandle;
        private SafeProcessHandle? _nativeProcessHandle;
        private Process? _managedProcess;
        private System.IO.Pipes.NamedPipeClientStream? _controlPipe;
        private JobBrokerConnection? _broker;
        private bool _committed;
        private bool _aborted;
        private bool _disposed;

        internal ResumedJobProcessTransaction(
            SafeJobHandle jobHandle,
            SafeProcessHandle nativeProcessHandle,
            Process managedProcess,
            System.IO.Pipes.NamedPipeClientStream controlPipe,
            JobBrokerConnection broker,
            string jobObjectName,
            int processId,
            long processStartUtcTicks,
            string executablePath,
            int windowsSessionId)
        {
            _jobHandle = jobHandle;
            _nativeProcessHandle = nativeProcessHandle;
            _managedProcess = managedProcess;
            _controlPipe = controlPipe;
            _broker = broker;
            JobObjectName = jobObjectName;
            ProcessId = processId;
            ProcessStartUtcTicks = processStartUtcTicks;
            ExecutablePath = executablePath;
            WindowsSessionId = windowsSessionId;
        }

        public string JobObjectName { get; }
        public int ProcessId { get; }
        public long ProcessStartUtcTicks { get; }
        public string ExecutablePath { get; }
        public int WindowsSessionId { get; }

        public Process Commit() => CommitAsync().GetAwaiter().GetResult();

        public async Task<Process> CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_aborted)
            {
                throw new InvalidOperationException("已回滚的启动事务不能提交。");
            }

            if (_committed)
            {
                throw new InvalidOperationException("启动事务已经提交。");
            }

            using var acknowledgementTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acknowledgementTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await WindowsJobBrokerProtocol.WriteAsync(
                    _controlPipe!,
                    new BrokerCreateProcessControl(
                        WindowsJobBrokerProtocol.Version,
                        WindowsJobBrokerProtocol.CommitAction),
                    acknowledgementTimeout.Token)
                .ConfigureAwait(false);
            var acknowledgement = await WindowsJobBrokerProtocol
                .ReadAsync<BrokerCreateProcessControl>(
                    _controlPipe!,
                    acknowledgementTimeout.Token)
                .ConfigureAwait(false);
            if (acknowledgement.ProtocolVersion != WindowsJobBrokerProtocol.Version ||
                !acknowledgement.Action.Equals(
                    WindowsJobBrokerProtocol.CommitAction,
                    StringComparison.Ordinal))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_DURABLE_COMMIT_ACK_INVALID",
                    "broker 未确认 durable Resumed receipt 的最终提交。",
                    $"Protocol={acknowledgement.ProtocolVersion}, " +
                    $"Action={acknowledgement.Action}。");
            }

            _broker!.MarkMemberResumed();
            _committed = true;
            _controlPipe!.Dispose();
            _controlPipe = null;
            _broker = null;
            _jobHandle!.Dispose();
            _jobHandle = null;
            _nativeProcessHandle!.Dispose();
            _nativeProcessHandle = null;
            var process = _managedProcess!;
            _managedProcess = null;
            return process;
        }

        public async Task<bool> AbortAfterResumeAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed)
            {
                throw new InvalidOperationException("已提交启动事务不能通过回滚 handle 终止。");
            }

            if (_aborted)
            {
                return true;
            }

            await TrySendAbortAsync(cancellationToken).ConfigureAwait(false);

            if (!TerminateJobObject(_jobHandle!, BrokerFailureExitCode))
            {
                throw CreateWin32Exception(
                    "JOB_ABORT_AFTER_RESUME_FAILED",
                    "状态持久化失败后无法回滚已恢复进程。",
                    Marshal.GetLastPInvokeError(),
                    $"Job={JobObjectName}。");
            }

            var empty = await WaitForStableEmptyAsync(
                _jobHandle!,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (empty)
            {
                _aborted = true;
                ReleaseHandles();
            }

            return empty;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_committed && !_aborted && _jobHandle is { IsInvalid: false })
            {
                try
                {
                    using var abortTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                    TrySendAbortAsync(abortTimeout.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (
                    ex is IOException or InvalidDataException or OperationCanceledException)
                {
                    // The pinned Job handle below remains authoritative.
                }

                _ = TerminateJobObject(_jobHandle, BrokerFailureExitCode);
            }

            ReleaseHandles();
        }

        private void ReleaseHandles()
        {
            _controlPipe?.Dispose();
            _managedProcess?.Dispose();
            _nativeProcessHandle?.Dispose();
            _jobHandle?.Dispose();
            _managedProcess = null;
            _controlPipe = null;
            _broker = null;
            _nativeProcessHandle = null;
            _jobHandle = null;
        }

        private async Task TrySendAbortAsync(CancellationToken cancellationToken)
        {
            if (_controlPipe?.IsConnected != true)
            {
                return;
            }

            try
            {
                await WindowsJobBrokerProtocol.WriteAsync(
                        _controlPipe,
                        new BrokerCreateProcessControl(
                            WindowsJobBrokerProtocol.Version,
                            WindowsJobBrokerProtocol.AbortAction),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or InvalidDataException or OperationCanceledException)
            {
                // TerminateJobObject on the pinned handle remains authoritative.
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(
        string applicationName,
        char* commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        char* environment,
        string currentDirectory,
        ref StartupInformationExNative startupInformation,
        out ProcessInformationNative processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint valueSize,
        IntPtr previousValue,
        IntPtr returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObject(IntPtr jobAttributes, string name);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle OpenJobObject(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        SafeProcessHandle processHandle,
        SafeJobHandle jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInAnyJob(
        SafeProcessHandle processHandle,
        IntPtr jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        SafeJobHandle jobHandle,
        int informationClass,
        IntPtr information,
        uint length,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        SafeJobHandle jobHandle,
        int informationClass,
        ref JobObjectExtendedLimitInformationNative information,
        uint length,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle jobHandle,
        int informationClass,
        ref JobObjectExtendedLimitInformationNative information,
        uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeJobHandle jobHandle, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle processHandle, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(
        SafeProcessHandle processHandle,
        uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeKernelObjectHandle threadHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle processHandle,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        char* executablePath,
        ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);
}

public sealed class WindowsJobObjectException : Exception
{
    public WindowsJobObjectException(string code, string message, string details) : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public string Details { get; }

    public override string ToString() =>
        $"{base.ToString()}{Environment.NewLine}Code: {Code}{Environment.NewLine}Details: {Details}";
}
