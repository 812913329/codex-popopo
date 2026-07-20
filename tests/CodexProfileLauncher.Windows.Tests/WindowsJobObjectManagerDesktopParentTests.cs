using System.Diagnostics;
using System.Text;
using CodexProfileLauncher.Infrastructure;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class WindowsJobObjectManagerDesktopParentTests
{
    [TestMethod]
    public void DesktopParentCreateFailure_SeparatesAccessDeniedFromOtherNativeErrors()
    {
        Assert.AreEqual(
            "DESKTOP_PARENT_COMPATIBILITY_PROCESS_ACCESS_DENIED",
            WindowsJobObjectManager.ClassifyDesktopParentCompatibilityCreateFailure(5));
        Assert.AreEqual(
            "DESKTOP_PARENT_COMPATIBILITY_PROCESS_ACCESS_DENIED",
            WindowsJobObjectManager.ClassifyDesktopParentCompatibilityCreateFailure(
                unchecked((int)0x80070005)));
        Assert.AreEqual(
            "DESKTOP_PARENT_COMPATIBILITY_PROCESS_CREATE_FAILED",
            WindowsJobObjectManager.ClassifyDesktopParentCompatibilityCreateFailure(87));
    }

    [TestMethod]
    public void DesktopParentFallback_OnlyAcceptsUnavailableOrPreCreateFailures()
    {
        Assert.IsTrue(WindowsJobObjectManager.ShouldUseDirectCompatibilityFallback(
            "DESKTOP_PARENT_EXPLORER_UNAVAILABLE"));
        Assert.IsTrue(WindowsJobObjectManager.ShouldUseDirectCompatibilityFallback(
            "DESKTOP_PARENT_COMPATIBILITY_PROCESS_ACCESS_DENIED"));
        Assert.IsFalse(WindowsJobObjectManager.ShouldUseDirectCompatibilityFallback(
            "DESKTOP_PARENT_PROCESS_IMAGE_MISMATCH"));
        Assert.IsFalse(WindowsJobObjectManager.ShouldUseDirectCompatibilityFallback(
            "DESKTOP_PARENT_THREAD_SUSPEND_COUNT_UNEXPECTED"));
    }

    [TestMethod]
    public void DesktopParentFallbackDetails_PreserveBothExactFailures()
    {
        var desktopFailure = new WindowsJobObjectException(
            "DESKTOP_PARENT_COMPATIBILITY_PROCESS_ACCESS_DENIED",
            "desktop failed",
            "Win32=5 ParentPID=123");
        var directFailure = new WindowsJobObjectException(
            "COMPATIBILITY_PROCESS_CREATE_FAILED",
            "direct failed",
            "HRESULT=0x80070005");

        var succeededDetails =
            WindowsJobObjectManager.ComposeDesktopParentCompatibilityFallbackDetails(
                desktopFailure,
                null);
        StringAssert.Contains(succeededDetails, desktopFailure.Code);
        StringAssert.Contains(succeededDetails, desktopFailure.Details);
        StringAssert.Contains(succeededDetails, "DirectFallback=Succeeded");

        var failedDetails =
            WindowsJobObjectManager.ComposeDesktopParentCompatibilityFallbackDetails(
                desktopFailure,
                directFailure);
        StringAssert.Contains(failedDetails, desktopFailure.Code);
        StringAssert.Contains(failedDetails, desktopFailure.Details);
        StringAssert.Contains(failedDetails, directFailure.Code);
        StringAssert.Contains(failedDetails, directFailure.Details);
    }

    [TestMethod]
    public async Task DesktopParentLaunch_PreservesEnvironmentWorkingDirectoryAndArguments()
    {
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.IsTrue(File.Exists(powershellPath), powershellPath);

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"cpl-desktop-parent-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(temporaryRoot, "working directory");
        var markerPath = Path.Combine(workingDirectory, "launch marker.txt");
        Directory.CreateDirectory(workingDirectory);

        WindowsCompatibilityProcessLaunch? launch = null;
        try
        {
            var manager = new WindowsJobObjectManager(
                Environment.ProcessPath
                    ?? throw new AssertFailedException("无法定位 Windows 测试进程路径。"));
            var expectedEnvironment = $"env-{Guid.NewGuid():N}";
            var expectedArgument = $"argument-{Guid.NewGuid():N}";
            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["CPL_DESKTOP_PARENT_TEST_ENV"] = expectedEnvironment;
            startInfo.Environment["CPL_DESKTOP_PARENT_TEST_ARGUMENT"] = expectedArgument;
            startInfo.Environment["CPL_DESKTOP_PARENT_TEST_MARKER"] = markerPath;
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            var script =
                "$lines = @($env:CPL_DESKTOP_PARENT_TEST_ENV, " +
                "[Environment]::CurrentDirectory, " +
                "$env:CPL_DESKTOP_PARENT_TEST_ARGUMENT); " +
                "[IO.File]::WriteAllLines($env:CPL_DESKTOP_PARENT_TEST_MARKER, $lines); " +
                "Start-Sleep -Seconds 30";
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

            launch = manager.StartDesktopParentCompatibilityProcess(startInfo);
            Assert.AreEqual(launch.Process.Id, launch.Identity.ProcessId);
            Assert.AreEqual(manager.CurrentWindowsSessionId, launch.Identity.WindowsSessionId);
            Assert.AreEqual(
                Path.GetFullPath(powershellPath),
                Path.GetFullPath(launch.Identity.ExecutablePath),
                ignoreCase: true);
            if (launch.Details.Contains("Strategy=explorer-parent", StringComparison.Ordinal))
            {
                StringAssert.Contains(launch.Details, "ParentPID=");
                StringAssert.Contains(launch.Details, "ParentIsInAnyJob=");
                StringAssert.Contains(launch.Details, "ResumePreviousSuspendCount=1");
            }
            else
            {
                // Restricted/elevated test hosts can be denied PROCESS_CREATE_PROCESS
                // on their medium-integrity Explorer. The production contract then
                // requires an explicit, observable direct fallback rather than a
                // hidden strategy switch.
                StringAssert.Contains(launch.Details, "DesktopParentAttempt=");
                StringAssert.Contains(launch.Details, "DirectFallback=Succeeded");
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(markerPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.IsTrue(File.Exists(markerPath), "桌面父进程测试子进程没有写出 marker。");
            var lines = await File.ReadAllLinesAsync(markerPath);
            Assert.HasCount(3, lines);
            Assert.AreEqual(expectedEnvironment, lines[0]);
            Assert.AreEqual(
                Path.GetFullPath(workingDirectory),
                Path.GetFullPath(lines[1]),
                ignoreCase: true);
            Assert.AreEqual(expectedArgument, lines[2]);

            launch.Process.Refresh();
            Assert.IsFalse(launch.Process.HasExited);
        }
        finally
        {
            if (launch is not null)
            {
                try
                {
                    launch.Process.Refresh();
                    if (!launch.Process.HasExited)
                    {
                        launch.Process.Kill(entireProcessTree: false);
                        _ = launch.Process.WaitForExit(5_000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Exact child has already exited.
                }

                launch.Process.Dispose();
            }

            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }
}
