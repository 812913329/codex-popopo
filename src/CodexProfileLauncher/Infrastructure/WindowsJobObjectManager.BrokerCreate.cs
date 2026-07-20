using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexProfileLauncher.Infrastructure;

public sealed partial class WindowsJobObjectManager
{
    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private const int HResultAccessDenied = unchecked((int)0x80070005);
    private const int NtStatusWin32AccessDenied = unchecked((int)0xC0070005);

    internal static string ClassifySuspendedCreateFailure(int nativeErrorCode) =>
        nativeErrorCode is ErrorAccessDenied or HResultAccessDenied or NtStatusWin32AccessDenied
            ? "PROCESS_CREATE_SUSPENDED_ACCESS_DENIED"
            : "PROCESS_CREATE_SUSPENDED_FAILED";

    internal static void TerminateBrokerJob(SafeJobHandle jobHandle)
    {
        ArgumentNullException.ThrowIfNull(jobHandle);
        if (!TerminateJobObject(jobHandle, BrokerFailureExitCode))
        {
            throw CreateWin32Exception(
                "JOB_BROKER_TERMINATE_FAILED",
                "broker 无法终止未提交的 Job 创建事务。",
                Marshal.GetLastPInvokeError());
        }
    }

    internal static BrokerSuspendedProcessTransfer CreateSuspendedForBroker(
        SafeJobHandle jobHandle,
        BrokerCreateProcessRequest request,
        int launcherProcessId,
        long launcherProcessStartUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(jobHandle);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != WindowsJobBrokerProtocol.Version)
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_PROTOCOL_MISMATCH",
                "broker 创建请求协议版本不一致。",
                $"Expected={WindowsJobBrokerProtocol.Version}, Actual={request.ProtocolVersion}。");
        }

        if (string.IsNullOrWhiteSpace(request.ApplicationPath) ||
            string.IsNullOrWhiteSpace(request.WorkingDirectory) ||
            request.Arguments is null ||
            request.Environment is null)
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_REQUEST_INVALID",
                "broker 创建请求缺少必要字段。",
                "ApplicationPath、WorkingDirectory、Arguments、Environment 均为必填字段。");
        }

        if (request.ApplicationPath.Contains('\0') ||
            request.WorkingDirectory.Contains('\0') ||
            request.Arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_REQUEST_INVALID",
                "broker 创建请求包含 NUL 字符。",
                "应用路径、工作目录和参数不得包含 NUL。环境变量由统一 builder 另行校验。");
        }

        var applicationPath = Path.GetFullPath(request.ApplicationPath);
        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException("找不到 broker 要启动的程序。", applicationPath);
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"broker 工作目录不存在：{workingDirectory}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = applicationPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = request.CreateNoWindow,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        var environmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Environment)
        {
            if (!environmentNames.Add(name))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_ENVIRONMENT_DUPLICATE",
                    "broker 创建请求包含大小写重复的环境变量。",
                    $"EnvironmentName={name}。");
            }

            startInfo.Environment.Add(name, value);
        }

        ValidateTargetStartInfo(startInfo);
        var commandLine = BuildCommandLine(startInfo, applicationPath);
        var environment = BuildUnicodeEnvironment(startInfo);
        var limitFlags = QueryLimitFlags(jobHandle);
        if ((limitFlags & JobObjectLimitKillOnJobClose) == 0)
        {
            throw new WindowsJobObjectException(
                "JOB_KILL_ON_CLOSE_MISSING",
                "broker 拒绝向未启用 KILL_ON_JOB_CLOSE 的 Job 创建 root。",
                $"LimitFlags=0x{limitFlags:X8}。");
        }

        if (QueryProcessIds(jobHandle).Count != 0)
        {
            throw new WindowsJobObjectException(
                "JOB_NOT_FRESH_FOR_CREATE",
                "broker 的 fresh Job 在创建 root 前已包含成员。",
                "单次创建事务不允许复用非空 Job。");
        }

        SafeProcessHandle? launcherHandle = null;
        SafeProcessHandle? processHandle = null;
        SafeKernelObjectHandle? threadHandle = null;
        var processInformation = default(ProcessInformationNative);
        var createdProcess = false;
        var createdMember = false;
        var remoteProcessHandle = IntPtr.Zero;
        var remoteThreadHandle = IntPtr.Zero;
        try
        {
            launcherHandle = OpenProcess(
                ProcessDuplicateHandle | ProcessQueryLimitedInformation | Synchronize,
                false,
                launcherProcessId);
            if (launcherHandle.IsInvalid)
            {
                throw CreateWin32Exception(
                    "JOB_BROKER_LAUNCHER_OPEN_FAILED",
                    "broker 无法固定请求方 launcher 身份。",
                    Marshal.GetLastPInvokeError(),
                    $"PID={launcherProcessId}。");
            }

            var launcherIdentity = CaptureProcessIdentity(launcherHandle);
            if (launcherIdentity.ProcessStartUtcTicks != launcherProcessStartUtcTicks ||
                WaitForSingleObject(launcherHandle, 0) != WaitTimeout ||
                GetSessionId(launcherProcessId) != GetSessionId(Environment.ProcessId))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_LAUNCHER_IDENTITY_MISMATCH",
                    "broker 请求方 launcher 身份已变化、退出或跨 Windows session。",
                    $"PID={launcherProcessId}, ExpectedStart={launcherProcessStartUtcTicks}, " +
                    $"ActualStart={launcherIdentity.ProcessStartUtcTicks}。");
            }

            var startupInformation = new StartupInformationExNative
            {
                StartupInfo = new StartupInformationNative
                {
                    Size = checked((uint)Marshal.SizeOf<StartupInformationNative>()),
                },
                AttributeList = IntPtr.Zero,
            };
            var creationFlags = CreateSuspended |
                                CreateUnicodeEnvironment;
            if (request.CreateNoWindow)
            {
                creationFlags |= CreateNoWindow;
            }

            unsafe
            {
                fixed (char* commandLinePointer = commandLine)
                fixed (char* environmentPointer = environment)
                {
                    if (!CreateProcess(
                            applicationPath,
                            commandLinePointer,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            creationFlags,
                            environmentPointer,
                            workingDirectory,
                            ref startupInformation,
                            out processInformation))
                    {
                        var nativeErrorCode = Marshal.GetLastPInvokeError();
                        throw CreateWin32Exception(
                            ClassifySuspendedCreateFailure(nativeErrorCode),
                            "broker 无法创建挂起 root。",
                            nativeErrorCode,
                            $"Application={applicationPath}。");
                    }
                }
            }

            processHandle = new SafeProcessHandle(processInformation.ProcessHandle, ownsHandle: true);
            processInformation.ProcessHandle = IntPtr.Zero;
            threadHandle = new SafeKernelObjectHandle(processInformation.ThreadHandle, ownsHandle: true);
            processInformation.ThreadHandle = IntPtr.Zero;
            createdProcess = true;
            if (!AssignProcessToJobObject(jobHandle, processHandle))
            {
                throw CreateWin32Exception(
                    "JOB_ASSIGN_SUSPENDED_FAILED",
                    "broker 无法在 root 主线程恢复前将其分配给 fresh Job。",
                    Marshal.GetLastPInvokeError(),
                    $"PID={processInformation.ProcessId}。");
            }

            createdMember = true;

            if (!IsProcessInJob(processHandle, jobHandle, out var isInJob))
            {
                throw CreateWin32Exception(
                    "JOB_MEMBERSHIP_QUERY_FAILED",
                    "broker 无法核验新 root 的 Job membership。",
                    Marshal.GetLastPInvokeError());
            }

            var processId = checked((int)processInformation.ProcessId);
            var threadId = checked((int)processInformation.ThreadId);
            var members = QueryProcessIds(jobHandle);
            if (!isInJob || members.Count != 1 || members[0] != processId)
            {
                throw new WindowsJobObjectException(
                    "JOB_MEMBERSHIP_REJECTED",
                    "broker 创建的新 root 未成为 fresh Job 的唯一成员。",
                    $"ExpectedPID={processId}, Members={string.Join(',', members)}。");
            }

            var nativeIdentity = CaptureProcessIdentity(processHandle);
            var windowsSessionId = GetSessionId(processId);
            if (!PathsEqual(nativeIdentity.ExecutablePath, applicationPath) ||
                windowsSessionId != GetSessionId(Environment.ProcessId))
            {
                throw new WindowsJobObjectException(
                    "PROCESS_IDENTITY_MISMATCH",
                    "broker 创建的挂起 root 身份与请求不一致。",
                    $"Requested={applicationPath}, Actual={nativeIdentity.ExecutablePath}, " +
                    $"PID={processId}, Session={windowsSessionId}。");
            }

            remoteProcessHandle = DuplicateIntoProcess(
                processHandle.DangerousGetHandle(),
                launcherHandle,
                ProcessQueryLimitedInformation | Synchronize,
                "JOB_BROKER_PROCESS_HANDLE_DUPLICATE_FAILED");
            try
            {
                remoteThreadHandle = DuplicateIntoProcess(
                    threadHandle.DangerousGetHandle(),
                    launcherHandle,
                    ThreadSuspendResume | ThreadQueryLimitedInformation | Synchronize,
                    "JOB_BROKER_THREAD_HANDLE_DUPLICATE_FAILED");
            }
            catch
            {
                CloseRemoteHandle(launcherHandle, remoteProcessHandle);
                remoteProcessHandle = IntPtr.Zero;
                throw;
            }

            var response = new BrokerCreateProcessResponse(
                WindowsJobBrokerProtocol.Version,
                Succeeded: true,
                Code: string.Empty,
                Message: string.Empty,
                Details: string.Empty,
                processId,
                threadId,
                nativeIdentity.ProcessStartUtcTicks,
                nativeIdentity.ExecutablePath,
                windowsSessionId,
                remoteProcessHandle.ToInt64(),
                remoteThreadHandle.ToInt64());
            var transfer = new BrokerSuspendedProcessTransfer(
                jobHandle,
                launcherHandle,
                processHandle,
                threadHandle,
                response,
                launcherProcessId,
                launcherProcessStartUtcTicks,
                remoteProcessHandle,
                remoteThreadHandle);
            launcherHandle = null;
            processHandle = null;
            threadHandle = null;
            remoteProcessHandle = IntPtr.Zero;
            remoteThreadHandle = IntPtr.Zero;
            return transfer;
        }
        catch
        {
            if (remoteThreadHandle != IntPtr.Zero && launcherHandle is { IsInvalid: false })
            {
                CloseRemoteHandle(launcherHandle, remoteThreadHandle);
            }

            if (remoteProcessHandle != IntPtr.Zero && launcherHandle is { IsInvalid: false })
            {
                CloseRemoteHandle(launcherHandle, remoteProcessHandle);
            }

            if (createdMember)
            {
                _ = TerminateJobObject(jobHandle, BrokerFailureExitCode);
            }
            else if (createdProcess && processHandle is { IsInvalid: false })
            {
                _ = TerminateProcess(processHandle, BrokerFailureExitCode);
            }

            throw;
        }
        finally
        {
            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.ProcessHandle);
            }

            if (processInformation.ThreadHandle != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.ThreadHandle);
            }

            threadHandle?.Dispose();
            processHandle?.Dispose();
            launcherHandle?.Dispose();
        }
    }

    private static unsafe IntPtr DuplicateIntoProcess(
        IntPtr sourceHandle,
        SafeProcessHandle targetProcess,
        uint desiredAccess,
        string errorCode)
    {
        var duplicatedHandle = IntPtr.Zero;
        if (!DuplicateHandle(
                GetCurrentProcess(),
                sourceHandle,
                targetProcess.DangerousGetHandle(),
                &duplicatedHandle,
                desiredAccess,
                false,
                0))
        {
            throw CreateWin32Exception(
                errorCode,
                "broker 无法向 exact launcher 复制受限原生句柄。",
                Marshal.GetLastPInvokeError());
        }

        return duplicatedHandle;
    }

    private static unsafe void CloseRemoteHandle(
        SafeProcessHandle launcherProcess,
        IntPtr remoteHandle)
    {
        if (remoteHandle == IntPtr.Zero || launcherProcess.IsInvalid)
        {
            return;
        }

        _ = DuplicateHandle(
            launcherProcess.DangerousGetHandle(),
            remoteHandle,
            IntPtr.Zero,
            null,
            0,
            false,
            DuplicateCloseSource);
    }

    internal sealed class BrokerSuspendedProcessTransfer : IDisposable
    {
        private readonly SafeJobHandle _jobHandle;
        private readonly SafeProcessHandle _launcherProcessHandle;
        private readonly SafeProcessHandle _processHandle;
        private readonly SafeKernelObjectHandle _threadHandle;
        private readonly int _launcherProcessId;
        private readonly long _launcherProcessStartUtcTicks;
        private IntPtr _remoteProcessHandle;
        private IntPtr _remoteThreadHandle;
        private bool _responseDelivered;
        private bool _disposed;

        internal BrokerSuspendedProcessTransfer(
            SafeJobHandle jobHandle,
            SafeProcessHandle launcherProcessHandle,
            SafeProcessHandle processHandle,
            SafeKernelObjectHandle threadHandle,
            BrokerCreateProcessResponse response,
            int launcherProcessId,
            long launcherProcessStartUtcTicks,
            IntPtr remoteProcessHandle,
            IntPtr remoteThreadHandle)
        {
            _jobHandle = jobHandle;
            _launcherProcessHandle = launcherProcessHandle;
            _processHandle = processHandle;
            _threadHandle = threadHandle;
            Response = response;
            _launcherProcessId = launcherProcessId;
            _launcherProcessStartUtcTicks = launcherProcessStartUtcTicks;
            _remoteProcessHandle = remoteProcessHandle;
            _remoteThreadHandle = remoteThreadHandle;
        }

        internal BrokerCreateProcessResponse Response { get; }

        internal void MarkResponseDelivered()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _responseDelivered = true;
            _remoteProcessHandle = IntPtr.Zero;
            _remoteThreadHandle = IntPtr.Zero;
        }

        internal void VerifyLiveMember()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (WaitForSingleObject(_launcherProcessHandle, 0) != WaitTimeout)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_LAUNCHER_EXITED",
                    "launcher 在创建事务提交前已经退出。",
                    $"PID={_launcherProcessId}。");
            }

            var launcherIdentity = CaptureProcessIdentity(_launcherProcessHandle);
            if (launcherIdentity.ProcessStartUtcTicks != _launcherProcessStartUtcTicks)
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_LAUNCHER_IDENTITY_MISMATCH",
                    "launcher generation 在创建事务期间发生变化。",
                    $"PID={_launcherProcessId}。");
            }

            if (WaitForSingleObject(_processHandle, 0) != WaitTimeout ||
                !IsProcessInJob(_processHandle, _jobHandle, out var isInJob) ||
                !isInJob ||
                !QueryProcessIds(_jobHandle).Contains(Response.ProcessId))
            {
                throw new WindowsJobObjectException(
                    "JOB_BROKER_ROOT_LOST_BEFORE_COMMIT",
                    "root 在创建事务提交前退出或离开指定 Job。",
                    $"PID={Response.ProcessId}。");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_responseDelivered)
            {
                CloseRemoteHandle(_launcherProcessHandle, _remoteThreadHandle);
                CloseRemoteHandle(_launcherProcessHandle, _remoteProcessHandle);
            }

            _threadHandle.Dispose();
            _processHandle.Dispose();
            _launcherProcessHandle.Dispose();
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(
        SafeJobHandle jobHandle,
        SafeProcessHandle processHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        IntPtr* targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(SafeProcessHandle processHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetThreadId(SafeKernelObjectHandle threadHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessIdOfThread(SafeKernelObjectHandle threadHandle);
}
