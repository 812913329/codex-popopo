using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexProfileLauncher.Infrastructure;

public sealed partial class WindowsJobObjectManager
{
    internal static string ClassifyDesktopParentCompatibilityCreateFailure(
        int nativeErrorCode) =>
        nativeErrorCode is ErrorAccessDenied or HResultAccessDenied or NtStatusWin32AccessDenied
            ? "DESKTOP_PARENT_COMPATIBILITY_PROCESS_ACCESS_DENIED"
            : "DESKTOP_PARENT_COMPATIBILITY_PROCESS_CREATE_FAILED";

    internal static bool ShouldUseDirectCompatibilityFallback(string code) =>
        code is "DESKTOP_PARENT_EXPLORER_UNAVAILABLE"
            or "DESKTOP_PARENT_ATTR_LIST_SIZE_FAILED"
            or "DESKTOP_PARENT_ATTR_LIST_INIT_FAILED"
            or "DESKTOP_PARENT_ATTR_PARENT_SET_FAILED"
            or "DESKTOP_PARENT_COMPATIBILITY_PROCESS_ACCESS_DENIED"
            or "DESKTOP_PARENT_COMPATIBILITY_PROCESS_CREATE_FAILED";

    internal static string ComposeDesktopParentCompatibilityFallbackDetails(
        WindowsJobObjectException desktopParentFailure,
        WindowsJobObjectException? directFailure)
    {
        ArgumentNullException.ThrowIfNull(desktopParentFailure);
        var details =
            $"DesktopParentAttempt={desktopParentFailure.Code}:{desktopParentFailure.Details}";
        return directFailure is null
            ? $"{details} | DirectFallback=Succeeded"
            : $"{details} | DirectFallback={directFailure.Code}:{directFailure.Details}";
    }

    /// <summary>
    /// Starts a local desktop mirror through the same-session shell token/job context.
    /// Unlike the strict broker path, this compatibility path deliberately does not
    /// require the child to be outside every Job. It still pins and verifies the exact
    /// process/thread generation before resuming it.
    /// </summary>
    internal WindowsCompatibilityProcessLaunch StartDesktopParentCompatibilityProcess(
        ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ValidateTargetStartInfo(startInfo);
        var applicationPath = Path.GetFullPath(startInfo.FileName);
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException("找不到桌面兼容模式要启动的程序。", applicationPath);
        }

        var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Path.GetDirectoryName(applicationPath) ?? Environment.CurrentDirectory
            : Path.GetFullPath(startInfo.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"桌面兼容模式工作目录不存在：{workingDirectory}");
        }

        WindowsJobObjectException? desktopParentFailure;
        using (var parent = TryOpenDesktopParentCandidate(out var parentLookupDetails))
        {
            if (parent is null)
            {
                desktopParentFailure = new WindowsJobObjectException(
                    "DESKTOP_PARENT_EXPLORER_UNAVAILABLE",
                    "无法定位可核验的同会话 explorer.exe 作为桌面兼容父进程。",
                    parentLookupDetails);
            }
            else
            {
                try
                {
                    return CreateDesktopParentCompatibilityProcessCore(
                        startInfo,
                        applicationPath,
                        workingDirectory,
                        parent);
                }
                catch (WindowsJobObjectException ex) when (
                    ShouldUseDirectCompatibilityFallback(ex.Code))
                {
                    desktopParentFailure = ex;
                }
            }
        }

        try
        {
            var direct = StartCompatibilityProcess(startInfo);
            return direct with
            {
                Details =
                    $"{ComposeDesktopParentCompatibilityFallbackDetails(desktopParentFailure, null)}；" +
                    Environment.NewLine + direct.Details,
            };
        }
        catch (WindowsJobObjectException directFailure)
        {
            throw new WindowsJobObjectException(
                "DESKTOP_PARENT_AND_DIRECT_COMPATIBILITY_PROCESS_CREATE_FAILED",
                "桌面父进程兼容启动与直接兼容启动均失败。",
                ComposeDesktopParentCompatibilityFallbackDetails(
                    desktopParentFailure,
                    directFailure));
        }
    }

    private WindowsCompatibilityProcessLaunch CreateDesktopParentCompatibilityProcessCore(
        ProcessStartInfo startInfo,
        string applicationPath,
        string workingDirectory,
        DesktopParentCandidate parent)
    {
        var expectedArguments = new List<string>(startInfo.ArgumentList.Count + 1)
        {
            applicationPath,
        };
        expectedArguments.AddRange(startInfo.ArgumentList);
        var commandLine = (BuildCommandLine(startInfo, applicationPath) + '\0').ToCharArray();
        var environment = BuildUnicodeEnvironment(startInfo);
        var processInformation = default(ProcessInformationNative);
        SafeProcessHandle? processHandle = null;
        SafeKernelObjectHandle? threadHandle = null;
        Process? managedProcess = null;
        var attributeList = IntPtr.Zero;
        var exactCreatedProcess = false;
        var completed = false;
        try
        {
            attributeList = AllocateDesktopParentProcessAttributeList(
                parent.Handle.DangerousGetHandle());
            var startupInformation = new StartupInformationExNative
            {
                StartupInfo = new StartupInformationNative
                {
                    Size = checked((uint)Marshal.SizeOf<StartupInformationExNative>()),
                },
                AttributeList = attributeList,
            };
            var creationFlags = CreateSuspended |
                                CreateUnicodeEnvironment |
                                ExtendedStartupinfoPresent |
                                (startInfo.CreateNoWindow ? CreateNoWindow : 0);

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
                            inheritHandles: false,
                            creationFlags,
                            environmentPointer,
                            workingDirectory,
                            ref startupInformation,
                            out processInformation))
                    {
                        var nativeErrorCode = Marshal.GetLastPInvokeError();
                        throw CreateWin32Exception(
                            ClassifyDesktopParentCompatibilityCreateFailure(nativeErrorCode),
                            "无法通过同会话 explorer.exe 父进程创建桌面兼容进程。",
                            nativeErrorCode,
                            $"ParentPID={parent.ProcessId}, Application={applicationPath}。");
                    }
                }
            }

            processHandle = new SafeProcessHandle(
                processInformation.ProcessHandle,
                ownsHandle: true);
            processInformation.ProcessHandle = IntPtr.Zero;
            threadHandle = new SafeKernelObjectHandle(
                processInformation.ThreadHandle,
                ownsHandle: true);
            processInformation.ThreadHandle = IntPtr.Zero;
            exactCreatedProcess = true;

            var processId = checked((int)processInformation.ProcessId);
            var threadId = checked((int)processInformation.ThreadId);
            var actualProcessId = GetProcessId(processHandle);
            var actualThreadId = GetThreadId(threadHandle);
            var threadOwnerProcessId = GetProcessIdOfThread(threadHandle);
            if (actualProcessId != processInformation.ProcessId ||
                actualThreadId != processInformation.ThreadId ||
                threadOwnerProcessId != processInformation.ProcessId)
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_HANDLE_IDENTITY_MISMATCH",
                    "桌面兼容进程的原生句柄与 CreateProcess 返回身份不一致。",
                    $"ExpectedPID/TID={processId}/{threadId}, " +
                    $"ActualPID/TID/ThreadOwner={actualProcessId}/{actualThreadId}/" +
                    $"{threadOwnerProcessId}。");
            }

            EnsureDesktopCompatibilityProcessAlive(
                processHandle,
                processId,
                "DESKTOP_PARENT_PROCESS_EXITED_BEFORE_RESUME",
                "桌面兼容进程在恢复主线程前已退出。");

            var identity = CaptureProcessIdentity(processHandle);
            if (!PathsEqual(identity.ExecutablePath, applicationPath))
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_IMAGE_MISMATCH",
                    "桌面兼容进程映像与请求路径不一致。",
                    $"PID={processId}, 请求={applicationPath}，实际={identity.ExecutablePath}。");
            }

            if (!TryReadProcessOwnerSid(processHandle, out var ownerSid) ||
                !ownerSid.Equals(_currentUserSid, StringComparison.Ordinal))
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_OWNER_MISMATCH",
                    "桌面兼容进程不属于当前 Windows 用户。",
                    $"PID={processId}, expected={_currentUserSid}, actual={ownerSid}。");
            }

            var sessionId = GetSessionId(processId);
            if (sessionId != _currentWindowsSessionId)
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_SESSION_MISMATCH",
                    "桌面兼容进程不在当前 Windows session。",
                    $"PID={processId}, expected={_currentWindowsSessionId}, actual={sessionId}。");
            }

            var actualArguments = WindowsProcessInspector.ParseCommandLine(
                ReadCommandLine(processHandle));
            if (!actualArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal))
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_ARGUMENT_MISMATCH",
                    "桌面兼容进程的实际 argv 与请求不一致。",
                    $"PID={processId}, ExpectedCount={expectedArguments.Count}, " +
                    $"ActualCount={actualArguments.Count}。");
            }

            VerifySuspendedDesktopProcessParameters(
                processHandle,
                workingDirectory,
                environment);

            bool? childInAnyJob = null;
            var childJobDetails = "ChildIsInAnyJob=Unknown";
            if (IsProcessInAnyJob(processHandle, IntPtr.Zero, out var isInAnyJob))
            {
                childInAnyJob = isInAnyJob;
                childJobDetails = $"ChildIsInAnyJob={isInAnyJob}";
            }
            else
            {
                childJobDetails += $"(Win32={Marshal.GetLastPInvokeError()})";
            }

            managedProcess = Process.GetProcessById(processId);
            var managedIdentity = CaptureProcessIdentity(managedProcess.SafeHandle);
            if (managedIdentity.ProcessStartUtcTicks != identity.ProcessStartUtcTicks ||
                !PathsEqual(managedIdentity.ExecutablePath, identity.ExecutablePath))
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_MANAGED_PROCESS_GENERATION_MISMATCH",
                    "桌面兼容进程的托管包装器没有固定到刚创建的进程 generation。",
                    $"PID={processId}, ExpectedStart={identity.ProcessStartUtcTicks}, " +
                    $"ActualStart={managedIdentity.ProcessStartUtcTicks}。");
            }

            var previousSuspendCount = ResumeThread(threadHandle);
            if (previousSuspendCount == ResumeFailed)
            {
                throw CreateWin32Exception(
                    "DESKTOP_PARENT_THREAD_RESUME_FAILED",
                    "无法恢复桌面兼容进程的主线程。",
                    Marshal.GetLastPInvokeError(),
                    $"PID/TID={processId}/{threadId}。");
            }

            if (previousSuspendCount != 1)
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_THREAD_SUSPEND_COUNT_UNEXPECTED",
                    "桌面兼容进程的主线程并非恰好一层挂起。",
                    $"PID/TID={processId}/{threadId}, " +
                    $"PreviousSuspendCount={previousSuspendCount}。");
            }

            EnsureDesktopCompatibilityProcessAlive(
                processHandle,
                processId,
                "DESKTOP_PARENT_PROCESS_EXITED_AFTER_RESUME",
                "桌面兼容进程在恢复后的存活核验期间已退出。");

            var result = new WindowsCompatibilityProcessLaunch(
                managedProcess,
                new WindowsJobProcessIdentity(
                    processId,
                    identity.ProcessStartUtcTicks,
                    identity.ExecutablePath,
                    sessionId),
                childInAnyJob,
                $"Strategy=explorer-parent, ParentPID={parent.ProcessId}, " +
                $"ParentIsInAnyJob={FormatNullableBoolean(parent.IsInAnyJob)}, " +
                $"PID/TID={processId}/{threadId}, Session={sessionId}, " +
                $"{childJobDetails}, ArgvCount={actualArguments.Count}, " +
                $"EnvironmentEntries={startInfo.Environment.Count}, " +
                $"WorkingDirectory={workingDirectory}, " +
                "SuspendedProcessParametersVerified=True, ResumePreviousSuspendCount=1；" +
                "兼容模式保留精确进程身份与数据隔离参数核验，但不承诺专用 Job 托管。" );
            managedProcess = null;
            completed = true;
            return result;
        }
        catch (Exception ex) when (
            ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            throw new WindowsJobObjectException(
                "DESKTOP_PARENT_PROCESS_VERIFY_FAILED",
                "桌面兼容进程的创建后核验失败。",
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
            threadHandle?.Dispose();
            processHandle?.Dispose();
            FreeDesktopParentProcessAttributeList(attributeList);

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

    private DesktopParentCandidate? TryOpenDesktopParentCandidate(out string details)
    {
        Process[] explorers;
        try
        {
            explorers = Process.GetProcessesByName("explorer");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            details =
                $"Session={_currentWindowsSessionId}; explorer 枚举失败：" +
                $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }

        DesktopParentCandidate? inJobFallback = null;
        var rejected = new List<string>();
        try
        {
            foreach (var explorer in explorers)
            {
                SafeProcessHandle? handle = null;
                try
                {
                    if (explorer.SessionId != _currentWindowsSessionId)
                    {
                        continue;
                    }

                    handle = OpenProcess(
                        ProcessCreateProcess | ProcessQueryLimitedInformation | Synchronize,
                        inheritHandle: false,
                        explorer.Id);
                    if (handle.IsInvalid)
                    {
                        rejected.Add($"PID={explorer.Id}:OpenProcess Win32={Marshal.GetLastPInvokeError()}");
                        handle.Dispose();
                        handle = null;
                        continue;
                    }

                    if (GetProcessId(handle) != checked((uint)explorer.Id) ||
                        WaitForSingleObject(handle, 0) != WaitTimeout)
                    {
                        rejected.Add($"PID={explorer.Id}:generation-not-live");
                        continue;
                    }

                    var identity = CaptureProcessIdentity(handle);
                    if (!Path.GetFileName(identity.ExecutablePath).Equals(
                            "explorer.exe",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        rejected.Add($"PID={explorer.Id}:image-mismatch");
                        continue;
                    }

                    if (!TryReadProcessOwnerSid(handle, out var ownerSid) ||
                        !ownerSid.Equals(_currentUserSid, StringComparison.Ordinal) ||
                        GetSessionId(explorer.Id) != _currentWindowsSessionId)
                    {
                        rejected.Add($"PID={explorer.Id}:owner-or-session-mismatch");
                        continue;
                    }

                    bool? isInAnyJob = null;
                    if (IsProcessInAnyJob(handle, IntPtr.Zero, out var inAnyJob))
                    {
                        isInAnyJob = inAnyJob;
                    }

                    var candidate = new DesktopParentCandidate(
                        handle,
                        explorer.Id,
                        identity.ExecutablePath,
                        isInAnyJob);
                    handle = null;
                    if (isInAnyJob == false)
                    {
                        inJobFallback?.Dispose();
                        details =
                            $"Session={_currentWindowsSessionId}; ParentPID={candidate.ProcessId}; " +
                            "ParentIsInAnyJob=False";
                        return candidate;
                    }

                    inJobFallback?.Dispose();
                    inJobFallback = candidate;
                }
                catch (Exception ex) when (
                    ex is Win32Exception or InvalidOperationException or ArgumentException or
                        WindowsJobObjectException)
                {
                    rejected.Add($"PID={explorer.Id}:{ex.GetType().Name}:{ex.Message}");
                }
                finally
                {
                    handle?.Dispose();
                }
            }

            if (inJobFallback is not null)
            {
                var result = inJobFallback;
                inJobFallback = null;
                details =
                    $"Session={_currentWindowsSessionId}; ParentPID={result.ProcessId}; " +
                    $"ParentIsInAnyJob={FormatNullableBoolean(result.IsInAnyJob)}";
                return result;
            }

            details =
                $"Session={_currentWindowsSessionId}; ExplorerCount={explorers.Length}; " +
                $"Rejected={string.Join(";", rejected.Take(4))}";
            return null;
        }
        finally
        {
            inJobFallback?.Dispose();
            foreach (var explorer in explorers)
            {
                explorer.Dispose();
            }
        }
    }

    private static void VerifySuspendedDesktopProcessParameters(
        SafeProcessHandle processHandle,
        string expectedWorkingDirectory,
        char[] expectedEnvironment)
    {
        var basicInformationLength = checked(IntPtr.Size * 6);
        var basicInformation = Marshal.AllocHGlobal(basicInformationLength);
        try
        {
            var status = NtQueryInformationProcess(
                processHandle,
                0,
                basicInformation,
                basicInformationLength,
                out _);
            if (status != 0)
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_PARAMETERS_QUERY_FAILED",
                    "无法读取 suspended 桌面进程的 PEB。",
                    $"NtStatus=0x{status:X8}。");
            }

            var peb = Marshal.ReadIntPtr(basicInformation, IntPtr.Size);
            if (peb == IntPtr.Zero)
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_PARAMETERS_QUERY_FAILED",
                    "suspended 桌面进程的 PEB 指针为空。",
                    "PebAddress=0。");
            }

            var processParameters = ReadRemoteIntPtrForDesktopParameters(
                processHandle,
                IntPtr.Add(peb, IntPtr.Size == 8 ? 0x20 : 0x10));
            if (processParameters == IntPtr.Zero)
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_PARAMETERS_QUERY_FAILED",
                    "suspended 桌面进程的 ProcessParameters 指针为空。",
                    "ProcessParameters=0。");
            }

            var currentDirectory = ReadRemoteUnicodeStringForDesktopParameters(
                processHandle,
                IntPtr.Add(processParameters, IntPtr.Size == 8 ? 0x38 : 0x24));
            if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDirectory)).Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedWorkingDirectory)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_WORKING_DIRECTORY_MISMATCH",
                    "suspended 桌面进程的实际工作目录与请求不一致。",
                    $"Expected={expectedWorkingDirectory}, Actual={currentDirectory}。");
            }

            var environmentPointer = ReadRemoteIntPtrForDesktopParameters(
                processHandle,
                IntPtr.Add(processParameters, IntPtr.Size == 8 ? 0x80 : 0x48));
            if (environmentPointer == IntPtr.Zero)
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_ENVIRONMENT_MISMATCH",
                    "suspended 桌面进程的环境块指针为空。",
                    $"ExpectedCharacters={expectedEnvironment.Length}。");
            }

            var expectedBytes = Encoding.Unicode.GetBytes(expectedEnvironment);
            var actualBytes = ReadRemoteBytesForDesktopParameters(
                processHandle,
                environmentPointer,
                expectedBytes.Length);
            if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
            {
                throw new WindowsJobObjectException(
                    "DESKTOP_PARENT_PROCESS_ENVIRONMENT_MISMATCH",
                    "suspended 桌面进程的实际 Unicode 环境块与请求不一致。",
                    $"ExpectedBytes={expectedBytes.Length}, ActualBytes={actualBytes.Length}；" +
                    "为避免泄露凭据，不输出环境变量值。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(basicInformation);
        }
    }

    private static IntPtr ReadRemoteIntPtrForDesktopParameters(
        SafeProcessHandle processHandle,
        IntPtr address)
    {
        var bytes = ReadRemoteBytesForDesktopParameters(processHandle, address, IntPtr.Size);
        return IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(bytes, 0))
            : new IntPtr(BitConverter.ToInt32(bytes, 0));
    }

    private static string ReadRemoteUnicodeStringForDesktopParameters(
        SafeProcessHandle processHandle,
        IntPtr address)
    {
        var structure = ReadRemoteBytesForDesktopParameters(
            processHandle,
            address,
            IntPtr.Size == 8 ? 16 : 8);
        var byteLength = BitConverter.ToUInt16(structure, 0);
        var buffer = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(structure, 8))
            : new IntPtr(BitConverter.ToInt32(structure, 4));
        if (byteLength == 0 || buffer == IntPtr.Zero)
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(
            ReadRemoteBytesForDesktopParameters(processHandle, buffer, byteLength));
    }

    private static byte[] ReadRemoteBytesForDesktopParameters(
        SafeProcessHandle processHandle,
        IntPtr address,
        int byteCount)
    {
        var buffer = Marshal.AllocHGlobal(byteCount);
        try
        {
            if (!ReadProcessMemoryForDesktopParameters(
                    processHandle,
                    address,
                    buffer,
                    checked((nuint)byteCount),
                    out var bytesRead) ||
                bytesRead != checked((nuint)byteCount))
            {
                throw CreateWin32Exception(
                    "DESKTOP_PARENT_PROCESS_PARAMETERS_READ_FAILED",
                    "无法读取 suspended 桌面进程的启动参数内存。",
                    Marshal.GetLastPInvokeError(),
                    $"Address=0x{address.ToInt64():X}, Requested={byteCount}, Read={bytesRead}。");
            }

            var bytes = new byte[byteCount];
            Marshal.Copy(buffer, bytes, 0, byteCount);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void EnsureDesktopCompatibilityProcessAlive(
        SafeProcessHandle processHandle,
        int processId,
        string exitedCode,
        string exitedMessage)
    {
        var waitResult = WaitForSingleObject(processHandle, 0);
        if (waitResult == WaitFailed)
        {
            throw CreateWin32Exception(
                "DESKTOP_PARENT_PROCESS_WAIT_FAILED",
                "无法核验桌面兼容进程是否仍在运行。",
                Marshal.GetLastPInvokeError(),
                $"PID={processId}。");
        }

        if (waitResult != WaitTimeout)
        {
            throw new WindowsJobObjectException(
                exitedCode,
                exitedMessage,
                $"PID={processId}, WaitResult=0x{waitResult:X8}。");
        }
    }

    private static IntPtr AllocateDesktopParentProcessAttributeList(IntPtr parentProcessHandle)
    {
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == 0)
        {
            throw CreateWin32Exception(
                "DESKTOP_PARENT_ATTR_LIST_SIZE_FAILED",
                "无法计算桌面父进程 CreateProcess 属性列表大小。",
                Marshal.GetLastPInvokeError());
        }

        var total = IntPtr.Size + checked((int)size);
        var basePointer = Marshal.AllocHGlobal(total);
        try
        {
            Marshal.WriteIntPtr(basePointer, parentProcessHandle);
            var attributeList = IntPtr.Add(basePointer, IntPtr.Size);
            var sizeCopy = size;
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref sizeCopy))
            {
                throw CreateWin32Exception(
                    "DESKTOP_PARENT_ATTR_LIST_INIT_FAILED",
                    "无法初始化桌面父进程 CreateProcess 属性列表。",
                    Marshal.GetLastPInvokeError());
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeParentProcess,
                    basePointer,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                DeleteProcThreadAttributeList(attributeList);
                throw CreateWin32Exception(
                    "DESKTOP_PARENT_ATTR_PARENT_SET_FAILED",
                    "无法设置桌面 explorer.exe 父进程属性。",
                    Marshal.GetLastPInvokeError());
            }

            return attributeList;
        }
        catch
        {
            Marshal.FreeHGlobal(basePointer);
            throw;
        }
    }

    private static void FreeDesktopParentProcessAttributeList(IntPtr attributeList)
    {
        if (attributeList == IntPtr.Zero)
        {
            return;
        }

        DeleteProcThreadAttributeList(attributeList);
        Marshal.FreeHGlobal(IntPtr.Subtract(attributeList, IntPtr.Size));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemoryForDesktopParameters(
        SafeProcessHandle processHandle,
        IntPtr baseAddress,
        IntPtr buffer,
        nuint size,
        out nuint numberOfBytesRead);

    private static string FormatNullableBoolean(bool? value) =>
        value is null ? "Unknown" : value.Value.ToString();

    private sealed class DesktopParentCandidate : IDisposable
    {
        public DesktopParentCandidate(
            SafeProcessHandle handle,
            int processId,
            string executablePath,
            bool? isInAnyJob)
        {
            Handle = handle;
            ProcessId = processId;
            ExecutablePath = executablePath;
            IsInAnyJob = isInAnyJob;
        }

        public SafeProcessHandle Handle { get; }

        public int ProcessId { get; }

        public string ExecutablePath { get; }

        public bool? IsInAnyJob { get; }

        public void Dispose() => Handle.Dispose();
    }
}
