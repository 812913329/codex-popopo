using System.Diagnostics;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Infrastructure;
using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
public sealed class WindowsProcessInspectorTests
{
    [TestMethod]
    public void ParseCommandLine_PreservesQuotedUnicodePath()
    {
        const string appData = @"E:\隔离 环境\app-data";
        var arguments = WindowsProcessInspector.ParseCommandLine(
            $"\"C:\\Program Files\\ChatGPT.exe\" \"--user-data-dir={appData}\" --new-window");

        Assert.HasCount(3, arguments);
        Assert.AreEqual(@"C:\Program Files\ChatGPT.exe", arguments[0]);
        Assert.IsTrue(WindowsProcessInspector.ArgumentsUseProfile(arguments, appData, requireNewWindow: true));
    }

    [TestMethod]
    public void ArgumentsUseProfile_RejectsChromiumChild()
    {
        const string appData = @"E:\profiles\one\app-data";
        var arguments = new[]
        {
            @"C:\ChatGPT.exe",
            $"--user-data-dir={appData}",
            "--new-window",
            "--type=renderer",
        };

        Assert.IsFalse(WindowsProcessInspector.ArgumentsUseProfile(arguments, appData, requireNewWindow: true));
    }

    [TestMethod]
    public void ArgumentsUseProfile_RejectsDifferentOrDuplicateDataRoot()
    {
        const string expected = @"E:\profiles\one\app-data";
        var different = new[]
        {
            @"C:\ChatGPT.exe",
            @"--user-data-dir=E:\profiles\two\app-data",
            "--new-window",
        };
        var duplicate = new[]
        {
            @"C:\ChatGPT.exe",
            $"--user-data-dir={expected}",
            $"--user-data-dir={expected}",
            "--new-window",
        };

        Assert.IsFalse(WindowsProcessInspector.ArgumentsUseProfile(different, expected, requireNewWindow: true));
        Assert.IsFalse(WindowsProcessInspector.ArgumentsUseProfile(duplicate, expected, requireNewWindow: true));
    }

    [TestMethod]
    public void CaptureAndRecheckCurrentProcess_UsesPidStartAndExecutableIdentity()
    {
        var inspector = new WindowsProcessInspector();
        using var current = Process.GetCurrentProcess();

        var captured = inspector.CaptureProcessTree(current.Id);
        var root = captured.Identities.Single(identity => identity.ProcessId == current.Id);
        var live = inspector.FindLiveIdentities(new[] { root });

        Assert.HasCount(1, live.LiveIdentities);
        Assert.AreEqual(root.ProcessStartUtcTicks, live.LiveIdentities[0].ProcessStartUtcTicks);
        Assert.AreEqual(root.ExecutablePath, live.LiveIdentities[0].ExecutablePath, ignoreCase: true);
    }

    [TestMethod]
    public void TerminateVerifiedIdentities_EndsOnlyExactTestChildIdentity()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");

        using var child = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法创建隔离的测试子进程。");
        try
        {
            var identity = new ObservedProcessIdentity
            {
                ProcessId = child.Id,
                ParentProcessId = Environment.ProcessId,
                ProcessStartUtcTicks = child.StartTime.ToUniversalTime().Ticks,
                ExecutablePath = Path.GetFullPath(startInfo.FileName),
            };
            var inspector = new WindowsProcessInspector();

            var result = inspector.TerminateVerifiedIdentities(identity, new[] { identity });
            Assert.IsTrue(child.WaitForExit(5_000));
            CollectionAssert.Contains(result.TerminatedProcessIds.ToArray(), child.Id);
            Assert.IsEmpty(result.InspectionErrors);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit();
            }
        }
    }

    [TestMethod]
    public void TerminateVerifiedIdentities_RefusesMismatchedStartTime()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");

        using var child = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法创建隔离的测试子进程。");
        try
        {
            var wrongIdentity = new ObservedProcessIdentity
            {
                ProcessId = child.Id,
                ParentProcessId = Environment.ProcessId,
                ProcessStartUtcTicks = child.StartTime.ToUniversalTime().AddTicks(-1).Ticks,
                ExecutablePath = Path.GetFullPath(startInfo.FileName),
            };
            var inspector = new WindowsProcessInspector();

            var result = inspector.TerminateVerifiedIdentities(wrongIdentity, new[] { wrongIdentity });

            Assert.IsFalse(child.HasExited);
            Assert.IsEmpty(result.TerminatedProcessIds);
            Assert.IsEmpty(result.InspectionErrors);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit();
            }
        }
    }

    [TestMethod]
    public void SelectStrictProcessTree_RejectsChildThatPredatesParentGeneration()
    {
        var root = Identity(100, 0, 1_000, @"C:\root.exe");
        var staleChild = Identity(200, 100, 999, @"C:\stale.exe");
        var validChild = Identity(201, 100, 1_001, @"C:\child.exe");
        var validGrandchild = Identity(202, 201, 1_002, @"C:\grandchild.exe");

        var selected = WindowsProcessInspector.SelectStrictProcessTree(
            root,
            new[] { root, staleChild, validChild, validGrandchild });

        CollectionAssert.AreEquivalent(
            new[] { 100, 201, 202 },
            selected.Select(identity => identity.ProcessId).ToArray());
    }

    [TestMethod]
    public void SelectStrictProcessTree_DoesNotAuthorizeSurvivorWithoutExactRootSnapshot()
    {
        var oldRoot = Identity(100, 0, 1_000, @"C:\root.exe");
        // A later process reused PID 100, created this child, and then exited.
        // Only the surviving child's static PPID remains visible.
        var replacementChild = Identity(200, 100, 3_000, @"C:\unrelated.exe");

        var selected = WindowsProcessInspector.SelectStrictProcessTree(
            oldRoot,
            new[] { replacementChild });

        Assert.IsEmpty(selected);
    }

    [TestMethod]
    public void SelectStrictProcessTree_TrustedLeasedParentFindsSurvivingGrandchild()
    {
        var root = Identity(100, 0, 1_000, @"C:\root.exe");
        var leasedChild = Identity(200, 100, 2_000, @"C:\child.exe");
        var survivingGrandchild = Identity(300, 200, 3_000, @"C:\grandchild.exe");

        var withoutIntermediateLease = WindowsProcessInspector.SelectStrictProcessTreeWithTrustedAnchors(
            root,
            new[] { survivingGrandchild },
            new[] { root });
        var withFullLease = WindowsProcessInspector.SelectStrictProcessTreeWithTrustedAnchors(
            root,
            new[] { survivingGrandchild },
            new[] { root, leasedChild });

        Assert.IsEmpty(withoutIntermediateLease);
        CollectionAssert.AreEqual(
            new[] { survivingGrandchild.ProcessId },
            withFullLease.Select(identity => identity.ProcessId).ToArray());
    }

    [TestMethod]
    public void RootAbsentLineage_RejectsLiveGrandchildWhenCriticalParentIsNoLongerLive()
    {
        var root = Identity(100, 0, 1_000, @"C:\root.exe");
        var missingParent = Identity(200, 100, 2_000, @"C:\child.exe");
        var liveGrandchild = Identity(300, 200, 3_000, @"C:\grandchild.exe");

        var valid = WindowsProcessInspector.TryValidateLiveLineage(
            root,
            new[] { root, missingParent, liveGrandchild },
            new[] { liveGrandchild },
            out var error);

        Assert.IsFalse(valid);
        StringAssert.Contains(error, "关键父代");
    }

    [TestMethod]
    public void OrderForTermination_UsesDeepestChildrenFirstAndRootLast()
    {
        var root = Identity(100, 0, 1_000, @"C:\root.exe");
        var child = Identity(200, 100, 1_001, @"C:\child.exe");
        var grandchild = Identity(300, 200, 1_002, @"C:\grandchild.exe");

        var ordered = WindowsProcessInspector.OrderForTermination(
            root,
            new[] { child, root, grandchild });

        CollectionAssert.AreEqual(
            new[] { 300, 200, 100 },
            ordered.Select(identity => identity.ProcessId).ToArray());
    }

    [TestMethod]
    public void StableStopGate_RejectsRoundThatSawDescendantEvenIfItExitedBeforeLivenessCheck()
    {
        var descendant = Identity(200, 100, 2_000, @"C:\child.exe");
        var sawDescendant = MainWindowViewModel.IsStopSnapshotEmpty(
            new ProcessTreeInspectionResult(new[] { descendant }, []),
            new ProcessDiscoveryResult([], []),
            new LiveIdentityInspectionResult([], []));
        var laterEmpty = MainWindowViewModel.IsStopSnapshotEmpty(
            new ProcessTreeInspectionResult([], []),
            new ProcessDiscoveryResult([], []),
            new LiveIdentityInspectionResult([], []));

        Assert.IsFalse(sawDescendant);
        Assert.IsTrue(laterEmpty);
        Assert.IsFalse(MainWindowViewModel.HasStableEmptyStopSnapshots(new[] { sawDescendant, laterEmpty }));
        Assert.IsTrue(MainWindowViewModel.HasStableEmptyStopSnapshots(new[] { laterEmpty, laterEmpty }));
    }

    [TestMethod]
    public void ForcedStopGate_RejectsOwnershipLeaseInspectionError()
    {
        var failedLease = new ProcessTerminationResult(
            [],
            ["PID=200: ownership lease failed"],
            []);

        Assert.IsFalse(MainWindowViewModel.CanContinueAfterTermination(failedLease));
    }

    [TestMethod]
    public void PostExitQuiescence_DoesNotSucceedWhenEveryBoundedPassDiscoversAnotherGeneration()
    {
        var stablePasses = 0;
        for (var pass = 0; pass < 8; pass++)
        {
            stablePasses = WindowsProcessInspector.NextStablePostExitPassCount(
                stablePasses,
                discoveredNewIdentity: true,
                allLeasesExited: true);
        }

        Assert.IsFalse(WindowsProcessInspector.HasStablePostExitQuiescence(stablePasses));

        stablePasses = WindowsProcessInspector.NextStablePostExitPassCount(
            stablePasses,
            discoveredNewIdentity: false,
            allLeasesExited: true);
        Assert.IsFalse(WindowsProcessInspector.HasStablePostExitQuiescence(stablePasses));
        stablePasses = WindowsProcessInspector.NextStablePostExitPassCount(
            stablePasses,
            discoveredNewIdentity: false,
            allLeasesExited: true);
        Assert.IsTrue(WindowsProcessInspector.HasStablePostExitQuiescence(stablePasses));
    }

    [TestMethod]
    public void TerminateVerifiedIdentities_AllowsMultipleGenerationsOfSamePidWithoutThrowing()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");

        using var child = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法创建隔离的测试子进程。");
        try
        {
            var current = Identity(
                child.Id,
                0,
                child.StartTime.ToUniversalTime().Ticks,
                Path.GetFullPath(startInfo.FileName));
            var oldGeneration = Identity(
                child.Id,
                0,
                current.ProcessStartUtcTicks - 1,
                current.ExecutablePath);
            var inspector = new WindowsProcessInspector();

            var result = inspector.TerminateVerifiedIdentities(
                current,
                new[] { oldGeneration, current });

            Assert.IsTrue(child.WaitForExit(5_000));
            CollectionAssert.Contains(result.TerminatedProcessIds.ToArray(), child.Id);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit();
            }
        }
    }

    [TestMethod]
    public void TerminateVerifiedIdentities_DoesNotUseSyntheticMissingRootAsAuthorization()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");

        using var child = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法创建隔离的测试子进程。");
        try
        {
            var syntheticRoot = Identity(int.MaxValue - 1, 0, 1, @"C:\missing-root.exe");
            var unrelated = Identity(
                child.Id,
                syntheticRoot.ProcessId,
                child.StartTime.ToUniversalTime().Ticks,
                Path.GetFullPath(startInfo.FileName));
            var inspector = new WindowsProcessInspector();

            var result = inspector.TerminateVerifiedIdentities(
                syntheticRoot,
                new[] { unrelated });

            Assert.IsFalse(child.HasExited);
            Assert.IsEmpty(result.TerminatedProcessIds);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit();
            }
        }
    }

    private static ObservedProcessIdentity Identity(
        int processId,
        int parentProcessId,
        long startTicks,
        string executablePath) =>
        new()
        {
            ProcessId = processId,
            ParentProcessId = parentProcessId,
            ProcessStartUtcTicks = startTicks,
            ExecutablePath = executablePath,
        };
}
