using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;
using CodexProfileLauncher.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace CodexProfileLauncher.JobBroker.TestHost;

internal static partial class Program
{
    private const string PauseReachedEnvironment =
        "CODEX_PROFILE_LAUNCHER_TEST_PAUSE_REACHED_EVENT";
    private const string PauseReleaseEnvironment =
        "CODEX_PROFILE_LAUNCHER_TEST_PAUSE_RELEASE_EVENT";
    private const uint KillOnJobClose = 0x00002000;
    private const uint BreakawayOk = 0x00000800;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int ErrorAlreadyExists = 183;

    public static int Main(string[] arguments)
    {
        if (arguments.Length > 0 &&
            arguments[0].Equals("--real-store-mirror-desktop-parent-probe", StringComparison.Ordinal))
        {
            return RunRealStoreMirrorDesktopParentProbe(arguments);
        }

        if (arguments.Length > 0 &&
            arguments[0].Equals("--outer-job-compatibility-process-probe", StringComparison.Ordinal))
        {
            return RunOuterJobCompatibilityProcessProbe(arguments);
        }

        if (arguments.Length > 0 &&
            arguments[0].Equals("--outer-job-startbroker-probe", StringComparison.Ordinal))
        {
            return RunOuterJobStartBrokerProbe(arguments);
        }

        if (arguments.Length > 0 &&
            arguments[0].Equals("--outer-job-detached-lifecycle-probe", StringComparison.Ordinal))
        {
            return RunOuterJobDetachedLifecycleProbe(arguments);
        }

        if (arguments.Length > 0 &&
            arguments[0].Equals("--startbroker-abrupt-parent-exit-probe", StringComparison.Ordinal))
        {
            return RunStartBrokerAbruptParentExitProbe(arguments);
        }

        if (arguments.Length > 0 &&
            arguments[0].Equals("--resume-before-durable-exit-probe", StringComparison.Ordinal))
        {
            return RunResumeBeforeDurableExitProbe(arguments);
        }

        if (!TryParseStandardBrokerArguments(arguments, out var request))
        {
            return 20;
        }

        var pauseReachedName = Environment.GetEnvironmentVariable(PauseReachedEnvironment);
        var pauseReleaseName = Environment.GetEnvironmentVariable(PauseReleaseEnvironment);
        if (string.IsNullOrWhiteSpace(pauseReachedName) ||
            string.IsNullOrWhiteSpace(pauseReleaseName))
        {
            return 21;
        }

        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(request.ReadyEventName);
            using var cancelEvent = EventWaitHandle.OpenExisting(request.CancelEventName);
            using var pauseReached = EventWaitHandle.OpenExisting(pauseReachedName);
            using var pauseRelease = EventWaitHandle.OpenExisting(pauseReleaseName);
            using var jobHandle = CreateJobObject(IntPtr.Zero, request.JobObjectName);
            var createError = Marshal.GetLastPInvokeError();
            if (jobHandle.IsInvalid || createError == ErrorAlreadyExists)
            {
                return 22;
            }

            var information = new JobObjectExtendedLimitInformationNative
            {
                BasicLimitInformation = new JobObjectBasicLimitInformationNative
                {
                    LimitFlags = KillOnJobClose,
                },
            };
            var informationSize = checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>());
            if (!SetInformationJobObject(
                    jobHandle,
                    JobObjectExtendedLimitInformation,
                    ref information,
                    informationSize))
            {
                return 23;
            }

            var verified = new JobObjectExtendedLimitInformationNative();
            if (!QueryInformationJobObject(
                    jobHandle,
                    JobObjectExtendedLimitInformation,
                    ref verified,
                    informationSize,
                    out _) ||
                (verified.BasicLimitInformation.LimitFlags & KillOnJobClose) == 0)
            {
                return 24;
            }

            if (!pauseReached.Set() || !pauseRelease.WaitOne(TimeSpan.FromSeconds(30)))
            {
                return 25;
            }

            if (!readyEvent.Set())
            {
                return 26;
            }

            while (!cancelEvent.WaitOne(TimeSpan.FromMilliseconds(100)))
            {
                // Test host intentionally owns the real Job handle until the
                // standard cancel signal or exact-process termination.
            }

            return 0;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return 27;
        }
        catch (UnauthorizedAccessException)
        {
            return 28;
        }
        catch (Win32Exception)
        {
            return 29;
        }
    }

    private static int RunRealStoreMirrorDesktopParentProbe(string[] arguments)
    {
        if (arguments.Length != 6 ||
            string.IsNullOrWhiteSpace(arguments[1]) ||
            string.IsNullOrWhiteSpace(arguments[2]) ||
            string.IsNullOrWhiteSpace(arguments[3]) ||
            string.IsNullOrWhiteSpace(arguments[4]) ||
            string.IsNullOrWhiteSpace(arguments[5]))
        {
            return 80;
        }

        var executablePath = Path.GetFullPath(arguments[1]);
        var profileRoot = Path.GetFullPath(arguments[2]);
        var workingDirectory = Path.GetFullPath(arguments[3]);
        var testId = arguments[4];
        var resultPath = Path.GetFullPath(arguments[5]);
        try
        {
            if (!File.Exists(executablePath) ||
                !Directory.Exists(profileRoot) ||
                !Directory.Exists(workingDirectory))
            {
                WriteRealStoreProbeResult(
                    resultPath,
                    new(false, "PROBE_INPUT_MISSING", "程序或测试目录不存在。"));
                return 81;
            }

            var profile = new CodexProfile
            {
                Id = Guid.NewGuid(),
                Name = $"real-store-mirror-{testId}",
                DataRoot = profileRoot,
                WorkingDirectory = workingDirectory,
            };
            var installation = new CodexInstallation(
                "RealStoreMirrorProbe",
                "RealStoreMirrorProbe",
                new Version(0, 0),
                Path.GetDirectoryName(executablePath)!,
                executablePath);
            var startInfo = CodexProcessLauncher.BuildStartInfo(
                profile,
                installation,
                ProfileAiLaunchConfiguration.Disabled);
            startInfo.Environment["CPL_REAL_STORE_MIRROR_TEST_ID"] = testId;

            var manager = new WindowsJobObjectManager(
                Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法定位 TestHost 路径。"));
            var launch = manager.StartDesktopParentCompatibilityProcess(startInfo);
            var identityPublished = false;
            try
            {
                WriteRealStoreProbeResult(
                    resultPath,
                    new(
                        true,
                        string.Empty,
                        launch.Details,
                        launch.Identity.ProcessId,
                        launch.Identity.ProcessStartUtcTicks,
                        launch.Identity.ExecutablePath,
                        launch.Identity.WindowsSessionId,
                        launch.IsInAnyJob));
                identityPublished = true;
            }
            finally
            {
                if (!identityPublished)
                {
                    // Without a durable exact identity the parent cannot safely reclaim the tree,
                    // so creator-side publication failure must fail closed.
                    try
                    {
                        launch.Process.Refresh();
                        if (!launch.Process.HasExited)
                        {
                            launch.Process.Kill(entireProcessTree: true);
                            _ = launch.Process.WaitForExit(10_000);
                        }
                    }
                    catch (Exception cleanupException) when (
                        cleanupException is InvalidOperationException or Win32Exception)
                    {
                        // The original result publication failure remains the primary error.
                    }
                }

                // On success dispose only the wrapper/handle. The exact mirror tree must outlive
                // this creator and is reclaimed by the parent integration test.
                launch.Process.Dispose();
            }

            return 0;
        }
        catch (Exception exception) when (
            exception is WindowsJobObjectException or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            try
            {
                var code = exception is WindowsJobObjectException windowsError
                    ? windowsError.Code
                    : exception.GetType().Name;
                var details = exception is WindowsJobObjectException jobError
                    ? $"{jobError.Message} | {jobError.Details}"
                    : exception.Message;
                WriteRealStoreProbeResult(resultPath, new(false, code, details));
            }
            catch
            {
                // The numeric exit code still makes the creator failure observable.
            }

            return 82;
        }
    }

    private static void WriteRealStoreProbeResult(
        string resultPath,
        RealStoreMirrorProbeResult result)
    {
        var parent = Path.GetDirectoryName(resultPath)
            ?? throw new IOException("Probe result path 没有父目录。");
        Directory.CreateDirectory(parent);
        using var stream = new FileStream(
            resultPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, result);
        stream.Flush(flushToDisk: true);
    }
    private static int RunOuterJobCompatibilityProcessProbe(string[] arguments)
    {
        if (arguments.Length != 2)
        {
            return 70;
        }

        var outerJob = CreateJobObject(IntPtr.Zero, null);
        if (outerJob.IsInvalid)
        {
            return 71;
        }

        Process? child = null;
        try
        {
            var information = KillOnCloseInformation();
            if (!SetInformationJobObject(
                    outerJob,
                    JobObjectExtendedLimitInformation,
                    ref information,
                    checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>())) ||
                !AssignProcessToJobObject(outerJob, GetCurrentProcess()))
            {
                return 72;
            }

            var manager = new WindowsJobObjectManager(arguments[1]);
            var startInfo = new ProcessStartInfo
            {
                FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");

            var launch = manager.StartCompatibilityProcess(startInfo);
            child = launch.Process;
            child.Refresh();
            if (launch.IsInAnyJob != true ||
                !IsProcessInJob(child.SafeHandle, outerJob, out var isInOuterJob) ||
                !isInOuterJob ||
                child.HasExited)
            {
                return 73;
            }

            child.Kill(entireProcessTree: false);
            return child.WaitForExit(5_000) ? 0 : 74;
        }
        catch (WindowsJobObjectException)
        {
            return 75;
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    child.Refresh();
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: false);
                        _ = child.WaitForExit(5_000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                child.Dispose();
            }

            // This process is itself a member; leaking the synthetic handle until
            // process teardown avoids KILL_ON_JOB_CLOSE terminating the probe first.
            outerJob.SetHandleAsInvalid();
            outerJob.Dispose();
        }
    }

    private static int RunOuterJobStartBrokerProbe(string[] arguments)
    {
        if (arguments.Length != 4 ||
            !Guid.TryParseExact(arguments[2], "N", out var profileId) ||
            !Guid.TryParseExact(arguments[3], "N", out var launchId))
        {
            return 40;
        }

        var outerJob = CreateJobObject(IntPtr.Zero, null);
        if (outerJob.IsInvalid)
        {
            return 41;
        }

        try
        {
            var information = KillOnCloseInformation();
            if (!SetInformationJobObject(
                    outerJob,
                    JobObjectExtendedLimitInformation,
                    ref information,
                    checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>())) ||
                !AssignProcessToJobObject(outerJob, GetCurrentProcess()))
            {
                return 42;
            }

            var manager = new WindowsJobObjectManager(arguments[1]);
            var names = manager.CreateNames(profileId, launchId);
            try
            {
                // Non-breakaway outer Jobs used to hard-fail. Product now falls
                // back to PROC_THREAD_ATTRIBUTE_PARENT_PROCESS (explorer) so
                // chat/browser/archive launches still work on user machines.
                using var broker = manager.StartBroker(names);
                if (broker.BrokerProcessId <= 0)
                {
                    return 43;
                }

                if (!manager.Inspect(names.JobObjectName).Exists ||
                    !manager.Inspect(names.JobObjectName).KillOnJobClose)
                {
                    return 45;
                }

                var brokerPid = broker.BrokerProcessId;
                // Dispose closes launcher-side handles; then kill the detached
                // broker so its last Job handle releases KILL_ON_JOB_CLOSE.
                broker.Dispose();
                try
                {
                    using var brokerProcess = Process.GetProcessById(brokerPid);
                    if (!brokerProcess.HasExited)
                    {
                        brokerProcess.Kill(entireProcessTree: true);
                        _ = brokerProcess.WaitForExit(5_000);
                    }
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (!manager.Inspect(names.JobObjectName).Exists &&
                        !manager.InspectReadyEvent(names.ReadyEventName).Exists &&
                        !manager.InspectCancelEvent(names.CancelEventName).Exists)
                    {
                        return 0;
                    }

                    Thread.Sleep(50);
                }

                // Job name may linger briefly while kernel tears down the last
                // handle; broker death is the success signal we require.
                return Process.GetProcesses().Any(p =>
                {
                    try
                    {
                        return p.Id == brokerPid;
                    }
                    catch
                    {
                        return false;
                    }
                    finally
                    {
                        p.Dispose();
                    }
                })
                    ? 46
                    : 0;
            }
            catch (WindowsJobObjectException ex)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "cpl-outer-job-probe-error.txt"),
                        $"{ex.Code}{Environment.NewLine}{ex.Message}{Environment.NewLine}{ex.Details}");
                }
                catch
                {
                    // ignore diagnostic write failures
                }

                return 44;
            }
        }
        finally
        {
            // Keep the KILL handle alive through process teardown. Explicitly
            // closing it while this process remains a member would correctly
            // terminate the test host before Main can return its probe result.
            outerJob.SetHandleAsInvalid();
            outerJob.Dispose();
        }
    }

    private static int RunOuterJobDetachedLifecycleProbe(string[] arguments)
    {
        if (arguments.Length != 5 ||
            !Guid.TryParseExact(arguments[2], "N", out var profileId) ||
            !Guid.TryParseExact(arguments[3], "N", out var launchId) ||
            string.IsNullOrWhiteSpace(arguments[4]))
        {
            return 50;
        }

        var outerJob = CreateJobObject(IntPtr.Zero, null);
        if (outerJob.IsInvalid)
        {
            return 51;
        }

        WindowsJobObjectManager? manager = null;
        WindowsJobNames? names = null;
        var committed = false;
        try
        {
            var information = KillOnCloseInformation(BreakawayOk);
            if (!SetInformationJobObject(
                    outerJob,
                    JobObjectExtendedLimitInformation,
                    ref information,
                    checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>())) ||
                !AssignProcessToJobObject(outerJob, GetCurrentProcess()))
            {
                return 52;
            }

            manager = new WindowsJobObjectManager(arguments[1]);
            names = manager.CreateNames(profileId, launchId);
            using var broker = manager.StartBroker(names);
            var rootStartInfo = new ProcessStartInfo
            {
                FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            rootStartInfo.ArgumentList.Add("-NoLogo");
            rootStartInfo.ArgumentList.Add("-NoProfile");
            rootStartInfo.ArgumentList.Add("-NonInteractive");
            rootStartInfo.ArgumentList.Add("-Command");
            rootStartInfo.ArgumentList.Add("Start-Sleep -Seconds 60");
            using var transaction = manager.CreateAssignedAndResume(rootStartInfo, broker);
            using var root = transaction.Commit();
            File.WriteAllText(
                arguments[4],
                $"{broker.BrokerProcessId}|{root.Id}");
            committed = true;
            return 0;
        }
        catch (WindowsJobObjectException)
        {
            return 53;
        }
        catch (IOException)
        {
            return 54;
        }
        finally
        {
            if (!committed && manager is not null && names is not null)
            {
                try
                {
                    _ = manager.TerminateAndWaitForStableEmptyAsync(
                            names.JobObjectName,
                            TimeSpan.FromSeconds(5))
                        .GetAwaiter()
                        .GetResult();
                }
                catch (WindowsJobObjectException)
                {
                    // The setup path already failed and may have removed the name.
                }
            }

            // Keep the synthetic outer KILL handle alive through process teardown.
            // A correctly detached broker/root must survive this process exit.
            outerJob.SetHandleAsInvalid();
            outerJob.Dispose();
        }
    }

    private static int RunStartBrokerAbruptParentExitProbe(string[] arguments)
    {
        if (arguments.Length != 5 ||
            !Guid.TryParseExact(arguments[2], "N", out var profileId) ||
            !Guid.TryParseExact(arguments[3], "N", out var launchId) ||
            string.IsNullOrWhiteSpace(arguments[4]))
        {
            return 60;
        }

        try
        {
            var manager = new WindowsJobObjectManager(arguments[1]);
            var names = manager.CreateNames(profileId, launchId);
            var broker = manager.StartBroker(names);
            File.WriteAllText(
                arguments[4],
                broker.BrokerProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));

            // Simulate the launcher disappearing without Dispose/finally after
            // the detached broker has started. The broker must observe the
            // exact parent generation ending and reclaim its empty Job.
            Environment.Exit(0);
            return 61;
        }
        catch (WindowsJobObjectException)
        {
            return 62;
        }
        catch (IOException)
        {
            return 63;
        }
    }

    private static int RunResumeBeforeDurableExitProbe(string[] arguments)
    {
        if (arguments.Length != 5 ||
            !Guid.TryParseExact(arguments[2], "N", out var profileId) ||
            !Guid.TryParseExact(arguments[3], "N", out var launchId) ||
            string.IsNullOrWhiteSpace(arguments[4]))
        {
            return 70;
        }

        try
        {
            var manager = new WindowsJobObjectManager(arguments[1]);
            var names = manager.CreateNames(profileId, launchId);
            var broker = manager.StartBroker(names);
            var rootStartInfo = new ProcessStartInfo
            {
                FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            rootStartInfo.ArgumentList.Add("-NoLogo");
            rootStartInfo.ArgumentList.Add("-NoProfile");
            rootStartInfo.ArgumentList.Add("-NonInteractive");
            rootStartInfo.ArgumentList.Add("-Command");
            rootStartInfo.ArgumentList.Add("Start-Sleep -Seconds 60");
            var transaction = manager.CreateAssignedAndResume(rootStartInfo, broker);
            File.WriteAllText(
                arguments[4],
                $"{broker.BrokerProcessId}|{transaction.ProcessId}");

            // Deliberately bypass every IDisposable/finally path after the root
            // was resumed but before the durable commit message was sent.
            Environment.Exit(0);
            return 71;
        }
        catch (WindowsJobObjectException)
        {
            return 72;
        }
        catch (IOException)
        {
            return 73;
        }
    }

    private static JobObjectExtendedLimitInformationNative KillOnCloseInformation(uint additionalFlags = 0) =>
        new()
        {
            BasicLimitInformation = new JobObjectBasicLimitInformationNative
            {
                LimitFlags = KillOnJobClose | additionalFlags,
            },
        };

    private static bool TryParseStandardBrokerArguments(
        string[] arguments,
        out BrokerRequest request)
    {
        request = default;
        if (arguments.Length != 11 ||
            !arguments[0].Equals("--job-broker", StringComparison.Ordinal))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Length; index += 2)
        {
            if (!values.TryAdd(arguments[index], arguments[index + 1]))
            {
                return false;
            }
        }

        if (values.Count != 5 ||
            !values.TryGetValue("--job-name", out var jobObjectName) ||
            !values.TryGetValue("--ready-event", out var readyEventName) ||
            !values.TryGetValue("--cancel-event", out var cancelEventName) ||
            !values.TryGetValue("--parent-pid", out var parentProcessIdText) ||
            !values.TryGetValue("--parent-start-ticks", out var parentStartTicksText) ||
            !int.TryParse(parentProcessIdText, out var parentProcessId) ||
            !long.TryParse(parentStartTicksText, out var parentStartTicks) ||
            parentProcessId <= 0 ||
            parentStartTicks <= 0 ||
            !jobObjectName.StartsWith(@"Global\CodexProfileLauncher.Job.v1.", StringComparison.Ordinal) ||
            !readyEventName.StartsWith(@"Global\CodexProfileLauncher.JobReady.v1.", StringComparison.Ordinal) ||
            !cancelEventName.StartsWith(@"Global\CodexProfileLauncher.JobCancel.v1.", StringComparison.Ordinal))
        {
            return false;
        }

        request = new(
            jobObjectName,
            readyEventName,
            cancelEventName,
            parentProcessId,
            parentStartTicks);
        return true;
    }

    private sealed record RealStoreMirrorProbeResult(
        bool Succeeded,
        string Code,
        string Details,
        int ProcessId = 0,
        long ProcessStartUtcTicks = 0,
        string ExecutablePath = "",
        int WindowsSessionId = -1,
        bool? IsInAnyJob = null);
    private readonly record struct BrokerRequest(
        string JobObjectName,
        string ReadyEventName,
        string CancelEventName,
        int ParentProcessId,
        long ParentProcessStartUtcTicks);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationNative
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
    private struct IoCountersNative
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformationNative BasicLimitInformation;
        public IoCountersNative IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        SafeProcessHandle processHandle,
        SafeJobHandle jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(
        SafeJobHandle jobHandle,
        IntPtr processHandle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle jobHandle,
        int informationClass,
        ref JobObjectExtendedLimitInformationNative information,
        uint length);

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
    private static partial bool CloseHandle(IntPtr handle);
}
