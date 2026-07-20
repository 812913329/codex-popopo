using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace CodexProfileLauncher.Windows.Tests;

/// <summary>
/// Opt-in verification against the locally installed Store Codex. The versioned production
/// runtime-cache is intentionally retained; only this test's exact process tree/profile is removed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RealStoreMirrorLaunchIntegrationTests
{
    private const string OptInEnvironmentVariable =
        "CODEX_RUN_REAL_STORE_MIRROR_INTEGRATION";
    private const string TestIdentityEnvironmentVariable =
        "CPL_REAL_STORE_MIRROR_TEST_ID";
    private const string TestHostPathEnvironmentVariable =
        "CPL_REAL_STORE_MIRROR_TEST_HOST_PATH";
    private const int AppModelErrorNoPackage = 15_700;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessBasicInformation = 0;
    private const int MaximumEnvironmentBytes = 4 * 1024 * 1024;

    [TestMethod]
    [TestCategory("RealStoreMirrorIntegration")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task InstalledStoreCodex_MirrorLaunchIsVisibleIsolatedAndUnpackaged()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"真实 Store Codex 整链测试默认跳过；仅在显式设置 {OptInEnvironmentVariable}=1 时运行。");
            return;
        }

        var sourceInstallation = await new WindowsCodexAppLocator().ResolveAsync();
        var launcherPaths = new LauncherPaths();
        var mirrorInstallation = await new CodexRuntimeMirrorManager(launcherPaths)
            .EnsureMirrorAsync(sourceInstallation);

        Assert.IsFalse(
            PathUtilities.IsSameOrNested(
                mirrorInstallation.ExecutablePath,
                sourceInstallation.InstallRoot),
            "运行副本不得仍位于 Store 包安装根目录。");
        Assert.DoesNotContain(
            "WindowsApps",
            mirrorInstallation.ExecutablePath.Split(Path.DirectorySeparatorChar));
        Assert.AreEqual(
            ComputeSha256(sourceInstallation.ExecutablePath),
            ComputeSha256(mirrorInstallation.ExecutablePath),
            ignoreCase: true,
            "mirror ChatGPT.exe 与已安装 Store 样本哈希不一致。");

        var testId = Guid.NewGuid().ToString("N");
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"CodexProfileLauncher.RealStoreMirror.{testId}");
        var inspector = new WindowsProcessInspector();
        WindowsCompatibilityProcessLaunch? launch = null;
        ObservedProcessIdentity? exactRoot = null;
        try
        {
            var profileRoot = Path.Combine(temporaryRoot, "profile");
            var workingDirectory = Path.Combine(temporaryRoot, "working-directory");
            var profilePaths = ProfilePaths.FromRoot(profileRoot);
            Directory.CreateDirectory(profilePaths.DataRoot);
            Directory.CreateDirectory(profilePaths.CodexHome);
            Directory.CreateDirectory(profilePaths.AppData);
            Directory.CreateDirectory(workingDirectory);

            var resultPath = Path.Combine(temporaryRoot, "creator-result.json");
            var creatorStartInfo = new ProcessStartInfo
            {
                FileName = GetTestHostExecutablePath(),
                WorkingDirectory = Path.GetDirectoryName(GetTestHostExecutablePath())!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
                     {
                         "--real-store-mirror-desktop-parent-probe",
                         mirrorInstallation.ExecutablePath,
                         profileRoot,
                         workingDirectory,
                         testId,
                         resultPath,
                     })
            {
                creatorStartInfo.ArgumentList.Add(argument);
            }

            var launchedUtc = DateTimeOffset.UtcNow;
            using var creator = Process.Start(creatorStartInfo)
                ?? throw new AssertFailedException("无法启动真实 mirror creator TestHost。");
            await creator.WaitForExitAsync();
            var creatorExitedUtc = DateTimeOffset.UtcNow;
            var creatorOutput = await creator.StandardOutput.ReadToEndAsync();
            var creatorError = await creator.StandardError.ReadToEndAsync();
            Assert.IsTrue(
                File.Exists(resultPath),
                $"creator TestHost 未写出精确进程身份。exit={creator.ExitCode} " +
                $"stdout={creatorOutput} stderr={creatorError}");
            var probe = JsonSerializer.Deserialize<RealStoreMirrorProbeResult>(
                await File.ReadAllTextAsync(resultPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new AssertFailedException("creator result JSON 为空。");
            Assert.AreEqual(
                0,
                creator.ExitCode,
                $"creator TestHost 失败。Code={probe.Code} Details={probe.Details} " +
                $"stdout={creatorOutput} stderr={creatorError}");
            Assert.IsTrue(probe.Succeeded, $"{probe.Code}: {probe.Details}");
            StringAssert.Contains(
                probe.Details,
                "SuspendedProcessParametersVerified=True",
                "生产桌面父进程入口没有返回 suspended cwd/environment 的核验证据。");
            Assert.IsGreaterThan(0, probe.ProcessId);
            Assert.IsGreaterThan(0L, probe.ProcessStartUtcTicks);

            exactRoot = new ObservedProcessIdentity
            {
                ProcessId = probe.ProcessId,
                ProcessStartUtcTicks = probe.ProcessStartUtcTicks,
                ExecutablePath = probe.ExecutablePath,
            };
            var child = Process.GetProcessById(probe.ProcessId);
            launch = new WindowsCompatibilityProcessLaunch(
                child,
                new WindowsJobProcessIdentity(
                    probe.ProcessId,
                    probe.ProcessStartUtcTicks,
                    probe.ExecutablePath,
                    probe.WindowsSessionId),
                probe.IsInAnyJob,
                probe.Details);
            StringAssert.Contains(
                launch.Details,
                "SuspendedProcessParametersVerified=True");
            child.Refresh();
            Assert.IsFalse(child.HasExited, "creator 退出时真实 mirror 根进程已退出。");
            Assert.AreEqual(
                probe.ProcessStartUtcTicks,
                child.StartTime.ToUniversalTime().Ticks,
                "creator 退出后 PID 已被复用。");
            Assert.AreEqual(
                Path.GetFullPath(probe.ExecutablePath),
                Path.GetFullPath(child.MainModule?.FileName ?? string.Empty),
                ignoreCase: true,
                "creator 退出后的根进程映像不匹配。");

            Assert.AreEqual(launch.Process.Id, launch.Identity.ProcessId);
            Assert.AreEqual(
                Path.GetFullPath(mirrorInstallation.ExecutablePath),
                Path.GetFullPath(launch.Identity.ExecutablePath),
                ignoreCase: true);

            using (var processHandle = OpenReadableProcess(launch.Process.Id))
            {
                Assert.AreEqual(
                    AppModelErrorNoPackage,
                    QueryPackageIdentity(processHandle),
                    "本地运行副本进程仍带有 MSIX package identity。");

                var runtime = ReadRuntimeSnapshot(processHandle);
                AssertEnvironmentPath(runtime, "CODEX_HOME", profilePaths.CodexHome);
                AssertEnvironmentPath(runtime, "CODEX_SQLITE_HOME", profilePaths.CodexHome);
                AssertEnvironmentPath(
                    runtime,
                    "CODEX_ELECTRON_USER_DATA_PATH",
                    profilePaths.AppData);
                Assert.IsTrue(
                    runtime.EnvironmentValues.TryGetValue(
                        TestIdentityEnvironmentVariable,
                        out var actualTestId),
                    "子进程环境块缺少本测试唯一身份标记。");
                Assert.AreEqual(
                    testId,
                    actualTestId,
                    "子进程没有收到本测试唯一身份标记。");
            }

            Assert.IsTrue(
                inspector.VerifyProfileRootArguments(
                    launch.Process.Id,
                    profilePaths.AppData,
                    out var argumentDetails),
                argumentDetails);

            var receipt = new RunningInstanceReceipt
            {
                ProfileId = Guid.NewGuid(),
                RootProcessId = launch.Identity.ProcessId,
                ProcessStartUtcTicks = launch.Identity.ProcessStartUtcTicks,
                ExecutablePath = launch.Identity.ExecutablePath,
                CodexVersion = mirrorInstallation.DisplayVersion,
                CodexHomePath = profilePaths.CodexHome,
                AppDataPath = profilePaths.AppData,
                LaunchedUtc = launchedUtc,
            };
            var verification = await new StartupIsolationVerifier(inspector).VerifyAsync(
                launch.Process,
                receipt,
                profilePaths,
                launchedUtc);
            Assert.IsTrue(
                verification.IsVerified,
                $"{verification.Message} {verification.Details} Launch={launch.Details}");

            var minimumSurvivalDeadline = creatorExitedUtc.AddSeconds(5);
            if (DateTimeOffset.UtcNow < minimumSurvivalDeadline)
            {
                await Task.Delay(minimumSurvivalDeadline - DateTimeOffset.UtcNow);
            }

            launch.Process.Refresh();
            Assert.IsFalse(launch.Process.HasExited, "启动调用返回五秒后根进程已经退出。");
            Assert.AreEqual(
                launch.Identity.ProcessStartUtcTicks,
                launch.Process.StartTime.ToUniversalTime().Ticks,
                "启动调用返回后的 PID 已经不是同一进程 generation。");
            Assert.AreNotEqual(
                IntPtr.Zero,
                launch.Process.MainWindowHandle,
                "真实 Codex 没有创建用户可见窗口。");
            Assert.IsTrue(launch.Process.Responding, "真实 Codex 窗口没有响应。");

            var tree = inspector.CaptureProcessTree(exactRoot);
            Assert.IsEmpty(tree.InspectionErrors, string.Join(Environment.NewLine, tree.InspectionErrors));
            Assert.IsTrue(
                tree.Identities.Any(identity =>
                    Path.GetFileName(identity.ExecutablePath)
                        .Equals("codex.exe", StringComparison.OrdinalIgnoreCase)),
                "真实镜像进程树没有启动内置 codex.exe app-server。");
        }
        finally
        {
            var cleanupErrors = await CleanupExactTestTreeAsync(
                launch,
                exactRoot,
                inspector);
            launch?.Process.Dispose();
            cleanupErrors.AddRange(await DeleteTemporaryProfileAsync(temporaryRoot));
            if (cleanupErrors.Count > 0)
            {
                Assert.Fail(
                    "真实 Store mirror 集成测试清理不完整：" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, cleanupErrors));
            }
        }
    }
    private static void AssertEnvironmentPath(
        ProcessRuntimeSnapshot runtime,
        string name,
        string expected)
    {
        Assert.IsTrue(
            runtime.EnvironmentValues.TryGetValue(name, out var actual),
            $"子进程环境块缺少 {name}。");
        Assert.AreEqual(
            Path.GetFullPath(expected),
            Path.GetFullPath(actual),
            ignoreCase: true,
            $"子进程环境块中的 {name} 不匹配。");
    }

    private static async Task<List<string>> CleanupExactTestTreeAsync(
        WindowsCompatibilityProcessLaunch? launch,
        ObservedProcessIdentity? exactRoot,
        WindowsProcessInspector inspector)
    {
        var errors = new List<string>();
        if (exactRoot is null)
        {
            return errors;
        }

        var snapshot = inspector.CaptureProcessTree(exactRoot);
        if (snapshot.InspectionErrors.Count == 0)
        {
            var termination = inspector.TerminateVerifiedIdentities(
                exactRoot,
                snapshot.Identities);
            if (termination.InspectionErrors.Count == 0)
            {
                return errors;
            }
        }

        if (launch is null)
        {
            errors.Add(
                $"无法取得 PID={exactRoot.ProcessId} 的托管句柄执行精确清理 fallback。");
            return errors;
        }

        // Safe fallback: this Process is pinned to the exact PID/start-time/image returned by
        // the production create call. No process-name or package-wide termination is used.
        try
        {
            launch.Process.Refresh();
            if (!launch.Process.HasExited)
            {
                var sameGeneration =
                    launch.Process.StartTime.ToUniversalTime().Ticks ==
                    exactRoot.ProcessStartUtcTicks;
                var sameImage = string.Equals(
                    Path.GetFullPath(launch.Process.MainModule?.FileName ?? string.Empty),
                    Path.GetFullPath(exactRoot.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
                if (!sameGeneration || !sameImage)
                {
                    errors.Add(
                        $"拒绝清理身份已变化的 PID={exactRoot.ProcessId}。");
                    return errors;
                }

                launch.Process.Kill(entireProcessTree: true);
                if (!launch.Process.WaitForExit(10_000))
                {
                    errors.Add($"PID={exactRoot.ProcessId} 的测试进程树未在十秒内退出。");
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The exact root already exited between refresh and cleanup.
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            errors.Add($"精确测试进程树清理失败：{exception.Message}");
        }

        var known = snapshot.Identities.Append(exactRoot).ToArray();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var live = inspector.FindLiveIdentities(known);
            if (live.InspectionErrors.Count > 0)
            {
                errors.AddRange(live.InspectionErrors);
                break;
            }

            if (live.LiveIdentities.Count == 0)
            {
                return errors;
            }

            await Task.Delay(250);
        }

        var remaining = inspector.FindLiveIdentities(known);
        foreach (var identity in remaining.LiveIdentities)
        {
            errors.Add(
                $"测试进程仍存活：PID={identity.ProcessId}, Image={identity.ExecutablePath}");
        }

        return errors;
    }

    private static async Task<IReadOnlyList<string>> DeleteTemporaryProfileAsync(string root)
    {
        var errors = new List<string>();
        if (!Directory.Exists(root))
        {
            return errors;
        }

        var normalizedRoot = Path.GetFullPath(root);
        var normalizedTemp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!normalizedRoot.StartsWith(normalizedTemp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(normalizedRoot)
                .StartsWith("CodexProfileLauncher.RealStoreMirror.", StringComparison.Ordinal))
        {
            return [$"拒绝删除边界外临时目录：{normalizedRoot}"];
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             normalizedRoot,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(normalizedRoot, recursive: true);
                return errors;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 19)
                {
                    errors.Add(
                        $"临时 profile 删除失败：Path={normalizedRoot} | Error={exception.Message}");
                    break;
                }

                await Task.Delay(250);
            }
        }

        return errors;
    }

    private static SafeProcessHandle OpenReadableProcess(int processId)
    {
        var handle = OpenProcess(
            ProcessQueryInformation | ProcessQueryLimitedInformation | ProcessVmRead,
            inheritHandle: false,
            processId);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new AssertFailedException(
            $"无法打开测试根进程读取 PEB：PID={processId}, Win32={error}。");
    }

    private static int QueryPackageIdentity(SafeProcessHandle processHandle)
    {
        uint packageFullNameLength = 0;
        return GetPackageFullName(
            processHandle,
            ref packageFullNameLength,
            IntPtr.Zero);
    }

    private static ProcessRuntimeSnapshot ReadRuntimeSnapshot(
        SafeProcessHandle processHandle)
    {
        var basicInformationLength = checked(IntPtr.Size * 6);
        var basicInformation = Marshal.AllocHGlobal(basicInformationLength);
        try
        {
            var status = NtQueryInformationProcess(
                processHandle,
                ProcessBasicInformation,
                basicInformation,
                basicInformationLength,
                out _);
            if (status != 0)
            {
                throw new AssertFailedException(
                    $"NtQueryInformationProcess 读取 PEB 失败：NTSTATUS=0x{status:X8}。");
            }

            var peb = Marshal.ReadIntPtr(basicInformation, IntPtr.Size);
            var processParameters = ReadRemoteIntPtr(
                processHandle,
                IntPtr.Add(peb, IntPtr.Size == 8 ? 0x20 : 0x10));
            var environmentPointer = ReadRemoteIntPtr(
                processHandle,
                IntPtr.Add(processParameters, IntPtr.Size == 8 ? 0x80 : 0x48));
            var environment = ReadSelectedEnvironmentValues(
                processHandle,
                environmentPointer,
                [
                    "CODEX_HOME",
                    "CODEX_SQLITE_HOME",
                    "CODEX_ELECTRON_USER_DATA_PATH",
                    TestIdentityEnvironmentVariable,
                ]);
            return new(environment);
        }
        finally
        {
            Marshal.FreeHGlobal(basicInformation);
        }
    }
    private static IReadOnlyDictionary<string, string> ReadSelectedEnvironmentValues(
        SafeProcessHandle processHandle,
        IntPtr environmentPointer,
        IReadOnlyList<string> selectedNames)
    {
        if (environmentPointer == IntPtr.Zero)
        {
            throw new AssertFailedException("子进程 PEB 的 Environment 指针为空。");
        }

        if (VirtualQueryEx(
                processHandle,
                environmentPointer,
                out var memory,
                checked((nuint)Marshal.SizeOf<MemoryBasicInformation>())) == 0)
        {
            throw new AssertFailedException(
                $"VirtualQueryEx(Environment) 失败：Win32={Marshal.GetLastPInvokeError()}。");
        }

        var offset = checked(environmentPointer.ToInt64() - memory.BaseAddress.ToInt64());
        var available = checked((long)memory.RegionSize - offset);
        var byteCount = checked((int)Math.Min(available, MaximumEnvironmentBytes));
        if (byteCount <= 0)
        {
            throw new AssertFailedException("子进程环境块所在内存区域无可读字节。");
        }

        var block = Encoding.Unicode.GetString(
            ReadRemoteBytes(processHandle, environmentPointer, byteCount));
        var terminator = block.IndexOf("\0\0", StringComparison.Ordinal);
        if (terminator < 0)
        {
            throw new AssertFailedException(
                $"子进程环境块超过测试读取上限 {MaximumEnvironmentBytes} bytes。");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in block[..terminator].Split('\0'))
        {
            foreach (var name in selectedNames)
            {
                var prefix = name + "=";
                if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    values[name] = entry[prefix.Length..];
                    break;
                }
            }
        }

        return values;
    }

    private static IntPtr ReadRemoteIntPtr(
        SafeProcessHandle processHandle,
        IntPtr address)
    {
        var bytes = ReadRemoteBytes(processHandle, address, IntPtr.Size);
        return IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(bytes, 0))
            : new IntPtr(BitConverter.ToInt32(bytes, 0));
    }


    private static byte[] ReadRemoteBytes(
        SafeProcessHandle processHandle,
        IntPtr address,
        int byteCount)
    {
        var local = Marshal.AllocHGlobal(byteCount);
        try
        {
            if (!ReadProcessMemory(
                    processHandle,
                    address,
                    local,
                    checked((nuint)byteCount),
                    out var bytesRead) ||
                bytesRead != checked((nuint)byteCount))
            {
                throw new AssertFailedException(
                    $"ReadProcessMemory 失败：Address=0x{address.ToInt64():X}, " +
                    $"Bytes={byteCount}, Read={bytesRead}, Win32={Marshal.GetLastPInvokeError()}。");
            }

            var bytes = new byte[byteCount];
            Marshal.Copy(local, bytes, 0, byteCount);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string GetTestHostExecutablePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(TestHostPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var normalized = Path.GetFullPath(explicitPath);
            return File.Exists(normalized)
                ? normalized
                : throw new AssertFailedException(
                    $"{TestHostPathEnvironmentVariable} 指向的 creator TestHost 不存在：{normalized}");
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new AssertFailedException("无法确定当前测试配置。");
        var workspaceRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var executable = Path.Combine(
            workspaceRoot,
            "tests",
            "CodexProfileLauncher.JobBroker.TestHost",
            "bin",
            configuration,
            "net10.0-windows10.0.19041.0",
            "win-x64",
            "CodexProfileLauncher.JobBroker.TestHost.exe");
        return File.Exists(executable)
            ? executable
            : throw new AssertFailedException($"creator TestHost 不存在：{executable}");
    }

    private sealed record RealStoreMirrorProbeResult(
        bool Succeeded,
        string Code,
        string Details,
        int ProcessId,
        long ProcessStartUtcTicks,
        string ExecutablePath,
        int WindowsSessionId,
        bool? IsInAnyJob);
    private sealed record ProcessRuntimeSnapshot(
        IReadOnlyDictionary<string, string> EnvironmentValues);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

#pragma warning disable SYSLIB1054 // Test-only blittable P/Invokes avoid enabling unsafe for the whole test assembly.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        SafeProcessHandle processHandle,
        IntPtr baseAddress,
        IntPtr buffer,
        nuint size,
        out nuint numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(
        SafeProcessHandle processHandle,
        IntPtr address,
        out MemoryBasicInformation buffer,
        nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetPackageFullName(
        SafeProcessHandle processHandle,
        ref uint packageFullNameLength,
        IntPtr packageFullName);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);
#pragma warning restore SYSLIB1054
}