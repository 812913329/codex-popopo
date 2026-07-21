using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Principal;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Infrastructure;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class WindowsJobObjectManagerTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    [TestMethod]
    public async Task EmptyBroker_GlobalNamesRemainOpenUntilBrokerReclaimsThenDisappear()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);

        Assert.IsTrue(fixture.Names.JobObjectName.StartsWith(@"Global\", StringComparison.Ordinal));
        Assert.IsTrue(fixture.Names.ReadyEventName.StartsWith(@"Global\", StringComparison.Ordinal));
        Assert.AreEqual(fixture.Manager.CurrentWindowsSessionId, broker.WindowsSessionId);

        var first = fixture.Manager.InspectOwnership(fixture.Names);
        Assert.IsTrue(first.Job.Exists);
        Assert.IsTrue(first.Job.KillOnJobClose);
        Assert.IsTrue(first.Job.IsEmpty);
        Assert.IsTrue(first.ReadyEvent.Exists);
        Assert.IsTrue(first.ReadyEvent.IsSignaled);
        Assert.IsTrue(first.CancelEvent.Exists);

        // Inspect opened and closed a launcher-side Job handle. The broker's
        // independent handle must keep the Global name available.
        await Task.Delay(300);
        Assert.IsTrue(fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists);

        Assert.IsTrue(await broker.AbortEmptySetupAsync(DefaultTimeout));
        broker.Dispose();
        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));
        Assert.IsFalse(fixture.Manager.InspectReadyEvent(fixture.Names.ReadyEventName).Exists);
    }

    [TestMethod]
    public async Task AssignedResume_CommitKeepsJobAndTerminateUsesStableEmptyGate()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        using var transaction = fixture.Manager.CreateAssignedAndResume(
            SleepProcessStartInfo(60),
            broker);
        using var root = transaction.Commit();

        Assert.AreEqual(fixture.Manager.CurrentWindowsSessionId, transaction.WindowsSessionId);
        var running = fixture.Manager.Inspect(fixture.Names.JobObjectName);
        Assert.IsTrue(running.Exists);
        Assert.IsTrue(running.KillOnJobClose);
        CollectionAssert.Contains(running.ProcessIds.ToArray(), transaction.ProcessId);
        Assert.IsTrue(running.Members.All(member =>
            member.WindowsSessionId == fixture.Manager.CurrentWindowsSessionId));

        broker.Dispose();
        Assert.IsTrue(fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists);
        Assert.IsTrue(await fixture.Manager.TerminateAndWaitForStableEmptyAsync(
            fixture.Names.JobObjectName,
            DefaultTimeout));
        Assert.IsTrue(root.WaitForExit(5_000));
        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));
    }

    [TestMethod]
    public async Task AbortAfterResume_RollsBackThroughSameAuthenticatedJobHandle()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        using var transaction = fixture.Manager.CreateAssignedAndResume(
            SleepProcessStartInfo(60),
            broker);

        Assert.IsTrue(await transaction.AbortAfterResumeAsync(DefaultTimeout));
        Assert.IsTrue(await WaitUntilAsync(
            () => !IsExactProcessAlive(
                transaction.ProcessId,
                transaction.ProcessStartUtcTicks),
            TimeSpan.FromSeconds(5)));
        broker.Dispose();
        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));
    }

    [TestMethod]
    public async Task BrokerConnection_IsSingleUseAndSecondCreateCannotKillCommittedRoot()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        using var transaction = fixture.Manager.CreateAssignedAndResume(
            SleepProcessStartInfo(60),
            broker);
        using var root = transaction.Commit();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Manager.CreateAssignedAndResume(SleepProcessStartInfo(60), broker));
        root.Refresh();
        Assert.IsFalse(root.HasExited);
        CollectionAssert.Contains(
            fixture.Manager.Inspect(fixture.Names.JobObjectName).ProcessIds.ToArray(),
            root.Id);

        Assert.IsTrue(await fixture.Manager.TerminateAndWaitForStableEmptyAsync(
            fixture.Names.JobObjectName,
            DefaultTimeout));
        Assert.IsTrue(root.WaitForExit(5_000));
    }

    [TestMethod]
    public async Task RootChild_InheritsJobAndSurvivesRootUntilTerminateJob()
    {
        using var fixture = CreateFixture();
        var markerPath = Path.Combine(Path.GetTempPath(), $"cpl-job-child-{Guid.NewGuid():N}.txt");
        fixture.TemporaryFiles.Add(markerPath);
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        using var transaction = fixture.Manager.CreateAssignedAndResume(
            ChildSpawnerStartInfo(markerPath),
            broker);
        using var root = transaction.Commit();

        Assert.IsTrue(await WaitUntilAsync(() => File.Exists(markerPath), DefaultTimeout));
        var childProcessId = int.Parse(
            await File.ReadAllTextAsync(markerPath),
            System.Globalization.CultureInfo.InvariantCulture);
        fixture.FallbackProcessIds.Add(childProcessId);
        Assert.IsTrue(root.WaitForExit(10_000));

        var afterRootExit = fixture.Manager.Inspect(fixture.Names.JobObjectName);
        Assert.IsTrue(afterRootExit.Exists);
        CollectionAssert.Contains(afterRootExit.ProcessIds.ToArray(), childProcessId);
        using (var child = Process.GetProcessById(childProcessId))
        {
            Assert.IsFalse(child.HasExited);
        }

        Assert.IsTrue(await fixture.Manager.TerminateAndWaitForStableEmptyAsync(
            fixture.Names.JobObjectName,
            DefaultTimeout));
        Assert.IsTrue(await WaitUntilAsync(
            () => !ProcessExists(childProcessId),
            TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task KillingBroker_FailClosedTerminatesCommittedRootAndDestroysName()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        using var transaction = fixture.Manager.CreateAssignedAndResume(
            SleepProcessStartInfo(60),
            broker);
        using var root = transaction.Commit();

        using (var brokerProcess = Process.GetProcessById(broker.BrokerProcessId))
        {
            Assert.AreEqual(
                broker.BrokerProcessStartUtcTicks,
                brokerProcess.StartTime.ToUniversalTime().Ticks);
            brokerProcess.Kill(entireProcessTree: false);
            Assert.IsTrue(brokerProcess.WaitForExit(5_000));
        }

        Assert.IsTrue(root.WaitForExit(5_000));
        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));
    }

    [TestMethod]
    public async Task RecoveryAndVerification_RequireUniqueExactBrokerAndJobSignals()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        var recovery = fixture.Manager.RecoverBroker(fixture.Names);

        Assert.AreEqual(WindowsJobBrokerRecoveryState.Found, recovery.State);
        Assert.IsNotNull(recovery.Broker);
        Assert.AreEqual(broker.BrokerProcessId, recovery.Broker.ProcessId);
        Assert.AreEqual(broker.BrokerProcessStartUtcTicks, recovery.Broker.ProcessStartUtcTicks);
        Assert.AreEqual(fixture.Manager.CurrentWindowsSessionId, recovery.Broker.WindowsSessionId);

        var receipt = CreatePendingReceipt(fixture, broker);
        Assert.IsTrue(fixture.Manager.VerifyBrokerIdentity(receipt, out var details), details);

        Assert.IsTrue(await broker.AbortEmptySetupAsync(DefaultTimeout));
        broker.Dispose();
        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));
        Assert.IsTrue(await WaitUntilAsync(
            () => fixture.Manager.RecoverBroker(fixture.Names).State ==
                WindowsJobBrokerRecoveryState.NotFound,
            DefaultTimeout));
    }

    [TestMethod]
    public async Task Recovery_IgnoresAccessibleSameBasenameProcessFromDifferentPath()
    {
        using var fixture = CreateFixture();
        var unrelatedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cpl-unrelated-broker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(unrelatedDirectory);
        var unrelatedExecutable = Path.Combine(unrelatedDirectory, "CodexProfileLauncher.exe");
        File.Copy(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            unrelatedExecutable);
        Process? unrelated = null;
        try
        {
            var unrelatedStartInfo = new ProcessStartInfo
            {
                FileName = unrelatedExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            unrelatedStartInfo.ArgumentList.Add("-NoLogo");
            unrelatedStartInfo.ArgumentList.Add("-NoProfile");
            unrelatedStartInfo.ArgumentList.Add("-Command");
            unrelatedStartInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
            unrelated = Process.Start(unrelatedStartInfo)
                ?? throw new AssertFailedException("无法创建同 basename 非候选进程。");

            using var broker = fixture.Manager.StartBroker(fixture.Names);
            var recovery = fixture.Manager.RecoverBroker(fixture.Names);
            Assert.AreEqual(WindowsJobBrokerRecoveryState.Found, recovery.State);
            Assert.IsNotNull(recovery.Broker);
            Assert.AreEqual(broker.BrokerProcessId, recovery.Broker.ProcessId);

            Assert.IsTrue(await broker.AbortEmptySetupAsync(DefaultTimeout));
        }
        finally
        {
            if (unrelated is not null)
            {
                if (!unrelated.HasExited)
                {
                    unrelated.Kill(entireProcessTree: true);
                    _ = unrelated.WaitForExit(5_000);
                }

                unrelated.Dispose();
            }

            Directory.Delete(unrelatedDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PinnedReceiptTerminate_RecoversMissingBrokerIdentityAndDrainsSameGeneration()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        using var transaction = fixture.Manager.CreateAssignedAndResume(
            SleepProcessStartInfo(60),
            broker);
        using var root = transaction.Commit();
        var pendingReceipt = CreatePendingReceipt(fixture, broker: null);

        var result = await fixture.Manager.TerminateVerifiedReceiptAndWaitForStableEmptyAsync(
            pendingReceipt,
            DefaultTimeout);

        Assert.AreEqual(ReceiptJobOperationState.Succeeded, result.State, result.Details);
        Assert.IsTrue(root.WaitForExit(5_000));
        broker.Dispose();
        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));
    }

    [TestMethod]
    public async Task StaleReceiptGeneration_CannotTerminateReplacementBrokerWithSameNames()
    {
        using var fixture = CreateFixture();
        WindowsJobObjectManager.JobBrokerConnection? firstBroker =
            fixture.Manager.StartBroker(fixture.Names);
        var staleReceipt = CreatePendingReceipt(fixture, firstBroker);
        Assert.IsTrue(await firstBroker.AbortEmptySetupAsync(DefaultTimeout));
        firstBroker.Dispose();
        firstBroker = null;
        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));

        using var replacementBroker = fixture.Manager.StartBroker(fixture.Names);
        var result = await fixture.Manager.TerminateVerifiedReceiptAndWaitForStableEmptyAsync(
            staleReceipt,
            DefaultTimeout);

        Assert.AreEqual(ReceiptJobOperationState.NotConfirmed, result.State);
        using (var liveReplacement = Process.GetProcessById(replacementBroker.BrokerProcessId))
        {
            liveReplacement.Refresh();
            Assert.IsFalse(liveReplacement.HasExited);
            Assert.AreEqual(
                replacementBroker.BrokerProcessStartUtcTicks,
                liveReplacement.StartTime.ToUniversalTime().Ticks);
        }

        Assert.IsTrue(fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists);
        Assert.IsTrue(await replacementBroker.AbortEmptySetupAsync(DefaultTimeout));
    }

    [TestMethod]
    public async Task VerifiedStableEmpty_UsesPinnedGenerationAndRequiresPersistedBroker()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        var receipt = CreatePendingReceipt(fixture, broker);

        var result = await fixture.Manager.ConfirmVerifiedReceiptStableEmptyAsync(
            receipt,
            DefaultTimeout);

        Assert.AreEqual(ReceiptJobOperationState.Succeeded, result.State, result.Details);
        Assert.IsEmpty(result.VerifiedMemberProcessIds);
        Assert.IsTrue(await broker.AbortEmptySetupAsync(DefaultTimeout));
    }

    [TestMethod]
    public async Task ExpiredPendingUnready_ReclaimsExactDeadParentBrokerWithoutResidualJob()
    {
        using var fixture = CreateTestHostFixture();
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new AssertFailedException("无法读取测试用户 SID。");
        var launchIdText = fixture.Names.JobObjectName[
            (fixture.Names.JobObjectName.LastIndexOf('.') + 1)..];
        var pauseReachedName =
            $@"Global\CodexProfileLauncher.JobTestPauseReached.v1.{sid}.{launchIdText}";
        var pauseReleaseName =
            $@"Global\CodexProfileLauncher.JobTestPauseRelease.v1.{sid}.{launchIdText}";
        var readyCreated = false;
        var cancelCreated = false;
        var reachedCreated = false;
        var releaseCreated = false;
        using var ready = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            fixture.Names.ReadyEventName,
            out readyCreated);
        using var cancel = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            fixture.Names.CancelEventName,
            out cancelCreated);
        using var pauseReached = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            pauseReachedName,
            out reachedCreated);
        using var pauseRelease = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            pauseReleaseName,
            out releaseCreated);
        Assert.IsTrue(readyCreated && cancelCreated && reachedCreated && releaseCreated);

        var startInfo = new ProcessStartInfo
        {
            FileName = fixture.Manager.BrokerExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fixture.Manager.BrokerExecutablePath),
        };
        startInfo.Environment["CODEX_PROFILE_LAUNCHER_TEST_PAUSE_REACHED_EVENT"] =
            pauseReachedName;
        startInfo.Environment["CODEX_PROFILE_LAUNCHER_TEST_PAUSE_RELEASE_EVENT"] =
            pauseReleaseName;
        foreach (var argument in new[]
                 {
                     "--job-broker",
                     "--job-name",
                     fixture.Names.JobObjectName,
                     "--ready-event",
                     fixture.Names.ReadyEventName,
                     "--cancel-event",
                     fixture.Names.CancelEventName,
                     "--parent-pid",
                     int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--parent-start-ticks",
                     "1",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Must leave outer Jobs (breakaway / explorer-parent). Plain Process.Start
        // inherits the test host Job and fails the independent-keeper check.
        using var brokerProcess = fixture.Manager.StartDetachedProcessOutsideJobs(startInfo);
        try
        {
            Assert.IsTrue(pauseReached.WaitOne(DefaultTimeout));
            Assert.IsFalse(ready.WaitOne(0));
            var pendingReceipt = CreatePendingReceipt(fixture, broker: null);

            var result = await fixture.Manager.ReclaimExpiredPendingUnreadyAsync(
                pendingReceipt,
                DefaultTimeout);

            Assert.AreEqual(PendingUnreadyReclaimState.Reclaimed, result.State, result.Details);
            Assert.IsTrue(brokerProcess.WaitForExit(5_000));
            Assert.IsFalse(fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists);
        }
        finally
        {
            if (!brokerProcess.HasExited)
            {
                brokerProcess.Kill(entireProcessTree: false);
                _ = brokerProcess.WaitForExit(5_000);
            }
        }
    }

    [TestMethod]
    public async Task StalePersistedUnreadyBroker_CannotReclaimReplacementGeneration()
    {
        using var fixture = CreateTestHostFixture();
        var (pauseReachedName, pauseReleaseName) = CreatePauseNames(fixture);
        var readyCreated = false;
        var cancelCreated = false;
        var reachedCreated = false;
        var releaseCreated = false;
        using var ready = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            fixture.Names.ReadyEventName,
            out readyCreated);
        using var cancel = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            fixture.Names.CancelEventName,
            out cancelCreated);
        using var pauseReached = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            pauseReachedName,
            out reachedCreated);
        using var pauseRelease = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            pauseReleaseName,
            out releaseCreated);
        Assert.IsTrue(readyCreated && cancelCreated && reachedCreated && releaseCreated);

        using var firstBroker = Process.Start(ControlledUnreadyBrokerStartInfo(
            fixture,
            pauseReachedName,
            pauseReleaseName)) ?? throw new AssertFailedException("无法创建第一代 unready broker。");
        Assert.IsTrue(pauseReached.WaitOne(DefaultTimeout));
        var staleReceipt = CreatePendingReceipt(fixture, broker: null);
        staleReceipt.BrokerProcessId = firstBroker.Id;
        staleReceipt.BrokerProcessStartUtcTicks = firstBroker.StartTime.ToUniversalTime().Ticks;
        firstBroker.Kill(entireProcessTree: false);
        Assert.IsTrue(firstBroker.WaitForExit(5_000));
        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));

        pauseReached.Reset();
        using var replacementBroker = Process.Start(ControlledUnreadyBrokerStartInfo(
            fixture,
            pauseReachedName,
            pauseReleaseName)) ?? throw new AssertFailedException("无法创建replacement unready broker。");
        try
        {
            Assert.IsTrue(pauseReached.WaitOne(DefaultTimeout));
            var result = await fixture.Manager.ReclaimExpiredPendingUnreadyAsync(
                staleReceipt,
                DefaultTimeout);

            Assert.AreEqual(PendingUnreadyReclaimState.NotConfirmed, result.State);
            Assert.AreEqual("PENDING_BROKER_GENERATION_MISMATCH", result.Code);
            replacementBroker.Refresh();
            Assert.IsFalse(replacementBroker.HasExited);
            Assert.IsTrue(fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists);
        }
        finally
        {
            if (!replacementBroker.HasExited)
            {
                replacementBroker.Kill(entireProcessTree: false);
                _ = replacementBroker.WaitForExit(5_000);
            }
        }

        Assert.IsTrue(await WaitUntilAsync(
            () => !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task WmiBreakawayFallback_RepeatedlyStartsActualBrokerAndReclaimsItsEmptyJob()
    {
        await VerifyWmiBrokerLifecycleAsync();
        await VerifyWmiBrokerLifecycleAsync();
    }

    [TestMethod]
    public void ReleaseStackTrace_MapsBuildMachineSourcePathToVirtualPath()
    {
#if !DEBUG
        var manager = new WindowsJobObjectManager(
            Environment.ProcessPath
                ?? throw new AssertFailedException("无法定位测试进程路径。"));
        var missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"cpl-missing-{Guid.NewGuid():N}.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = missingExecutable,
            UseShellExecute = false,
        };

        var exception = Assert.ThrowsExactly<FileNotFoundException>(
            () => manager.StartDetachedProcessOutsideJobsViaWmi(startInfo));
        var rendered = exception.ToString();
        StringAssert.Contains(rendered, "/_/CodexProfileLauncher");
        Assert.IsFalse(
            rendered.Contains(GetWorkspaceRoot(), StringComparison.OrdinalIgnoreCase),
            rendered);
#endif
    }

    private static async Task VerifyWmiBrokerLifecycleAsync()
    {
        var publishedBroker = Environment.GetEnvironmentVariable(
            "CPL_PUBLISHED_BROKER_PROBE_PATH");
        string brokerExecutable;
        string? managedEntryPoint = null;
        if (string.IsNullOrWhiteSpace(publishedBroker))
        {
            var dotnetRoot = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)
                    ?? throw new AssertFailedException("无法定位测试 .NET runtime。"),
                "..",
                "..",
                ".."));
            brokerExecutable = Path.Combine(dotnetRoot, "dotnet.exe");
            managedEntryPoint = BuildOutputPaths.RequireBrokerManagedEntryPoint();
        }
        else
        {
            brokerExecutable = publishedBroker;
        }

        brokerExecutable = Path.GetFullPath(brokerExecutable);
        Assert.IsTrue(File.Exists(brokerExecutable), $"broker host EXE 不存在：{brokerExecutable}");
        if (managedEntryPoint is not null)
        {
            managedEntryPoint = Path.GetFullPath(managedEntryPoint);
            Assert.IsTrue(File.Exists(managedEntryPoint), $"broker 入口 DLL 不存在：{managedEntryPoint}");
        }

        var manager = new WindowsJobObjectManager(brokerExecutable);
        var names = manager.CreateNames(Guid.NewGuid(), Guid.NewGuid());
        var readyCreated = false;
        var cancelCreated = false;
        using var ready = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            names.ReadyEventName,
            out readyCreated);
        using var cancel = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            names.CancelEventName,
            out cancelCreated);
        Assert.IsTrue(readyCreated && cancelCreated);
        using var parent = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = brokerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(brokerExecutable),
        };
        if (managedEntryPoint is not null)
        {
            startInfo.ArgumentList.Add(managedEntryPoint);
        }

        WindowsJobBroker.AppendBrokerArguments(
            startInfo,
            new WindowsJobBrokerRequest(
                names.JobObjectName,
                names.ReadyEventName,
                names.CancelEventName,
                parent.Id,
                parent.StartTime.ToUniversalTime().Ticks));

        Process broker;
        try
        {
            broker = manager.StartDetachedProcessOutsideJobsViaWmi(startInfo);
        }
        catch (WindowsJobObjectException ex)
        {
            throw new AssertFailedException($"{ex.Code}: {ex.Details}", ex);
        }

        using (broker)
        try
        {
            Assert.AreEqual(manager.CurrentWindowsSessionId, broker.SessionId);
            Assert.IsTrue(ready.WaitOne(DefaultTimeout), $"broker ready 超时：PID={broker.Id}");
            var inspection = manager.Inspect(names.JobObjectName);
            Assert.IsTrue(inspection.Exists);
            Assert.IsTrue(inspection.KillOnJobClose);
            Assert.IsTrue(inspection.IsEmpty);

            _ = cancel.Set();
            Assert.IsTrue(broker.WaitForExit(10_000));
            Assert.IsTrue(await WaitUntilAsync(
                () => !manager.Inspect(names.JobObjectName).Exists,
                DefaultTimeout));
        }
        finally
        {
            broker.Refresh();
            if (!broker.HasExited)
            {
                broker.Kill(entireProcessTree: false);
                _ = broker.WaitForExit(5_000);
            }
        }
    }

    [TestMethod]
    public void LauncherInsideSyntheticNonBreakawayKillJob_EscapesViaExplorerParentAndCleansUp()
    {
        using var fixture = CreateFixture();
        var nameParts = fixture.Names.JobObjectName.Split('.');
        var startInfo = new ProcessStartInfo
        {
            FileName = GetTestHostExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--outer-job-startbroker-probe");
        startInfo.ArgumentList.Add(fixture.Manager.BrokerExecutablePath);
        startInfo.ArgumentList.Add(nameParts[^2]);
        startInfo.ArgumentList.Add(nameParts[^1]);

        using var probe = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法启动 outer Job probe helper。");
        Assert.IsTrue(probe.WaitForExit(20_000));
        Assert.AreEqual(0, probe.ExitCode);
        // Probe must fully clean the named Job/events after the explorer-parent fallback.
        Assert.IsFalse(fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists);
        Assert.IsFalse(fixture.Manager.InspectReadyEvent(fixture.Names.ReadyEventName).Exists);
        Assert.IsFalse(fixture.Manager.InspectCancelEvent(fixture.Names.CancelEventName).Exists);
    }

    [TestMethod]
    public void LauncherInsideSyntheticNonBreakawayKillJob_CompatibilityModeStartsInsideContainment()
    {
        using var fixture = CreateFixture();
        var startInfo = new ProcessStartInfo
        {
            FileName = GetTestHostExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--outer-job-compatibility-process-probe");
        startInfo.ArgumentList.Add(fixture.Manager.BrokerExecutablePath);

        using var probe = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法启动 compatibility outer Job probe helper。");
        Assert.IsTrue(probe.WaitForExit(20_000));
        Assert.AreEqual(0, probe.ExitCode);
    }

    [TestMethod]
    public async Task LauncherInsideSyntheticBreakawayKillJob_DetachesBrokerAndRootFromOuterLifetime()
    {
        using var fixture = CreateFixture();
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"cpl-breakaway-{Guid.NewGuid():N}.txt");
        fixture.TemporaryFiles.Add(markerPath);
        var nameParts = fixture.Names.JobObjectName.Split('.');
        var startInfo = new ProcessStartInfo
        {
            FileName = GetTestHostExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--outer-job-detached-lifecycle-probe");
        startInfo.ArgumentList.Add(fixture.Manager.BrokerExecutablePath);
        startInfo.ArgumentList.Add(nameParts[^2]);
        startInfo.ArgumentList.Add(nameParts[^1]);
        startInfo.ArgumentList.Add(markerPath);

        using var probe = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法启动 breakaway outer Job probe helper。");
        Assert.IsTrue(probe.WaitForExit(20_000));
        Assert.AreEqual(0, probe.ExitCode);
        Assert.IsTrue(File.Exists(markerPath), "helper 未写出 detached lifecycle marker。");
        var processIds = (await File.ReadAllTextAsync(markerPath))
            .Split('|')
            .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.HasCount(2, processIds);
        var brokerProcessId = processIds[0];
        var rootProcessId = processIds[1];
        fixture.FallbackProcessIds.Add(brokerProcessId);
        fixture.FallbackProcessIds.Add(rootProcessId);

        // The helper has exited and its synthetic KILL_ON_JOB_CLOSE outer Job
        // has lost its final handle. Both detached processes must still exist.
        await Task.Delay(300);
        Assert.IsTrue(ProcessExists(brokerProcessId));
        Assert.IsTrue(ProcessExists(rootProcessId));
        var inspection = fixture.Manager.Inspect(fixture.Names.JobObjectName);
        Assert.IsTrue(inspection.Exists);
        Assert.IsTrue(inspection.KillOnJobClose);
        CollectionAssert.Contains(inspection.ProcessIds.ToArray(), rootProcessId);
        CollectionAssert.DoesNotContain(inspection.ProcessIds.ToArray(), brokerProcessId);

        Assert.IsTrue(await fixture.Manager.TerminateAndWaitForStableEmptyAsync(
            fixture.Names.JobObjectName,
            DefaultTimeout));
        Assert.IsTrue(await WaitUntilAsync(
            () => !ProcessExists(rootProcessId) && !ProcessExists(brokerProcessId),
            DefaultTimeout));
        Assert.IsFalse(fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists);
    }

    [TestMethod]
    public async Task LauncherAbruptExit_AfterDetachedBrokerStart_ReclaimsEmptyJobAndBroker()
    {
        using var fixture = CreateFixture();
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"cpl-abrupt-parent-{Guid.NewGuid():N}.txt");
        fixture.TemporaryFiles.Add(markerPath);
        var nameParts = fixture.Names.JobObjectName.Split('.');
        var startInfo = new ProcessStartInfo
        {
            FileName = GetTestHostExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--startbroker-abrupt-parent-exit-probe");
        startInfo.ArgumentList.Add(fixture.Manager.BrokerExecutablePath);
        startInfo.ArgumentList.Add(nameParts[^2]);
        startInfo.ArgumentList.Add(nameParts[^1]);
        startInfo.ArgumentList.Add(markerPath);

        using var probe = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法启动 abrupt parent probe helper。");
        Assert.IsTrue(probe.WaitForExit(15_000));
        Assert.AreEqual(0, probe.ExitCode);
        Assert.IsTrue(File.Exists(markerPath));
        var brokerProcessId = int.Parse(
            await File.ReadAllTextAsync(markerPath),
            System.Globalization.CultureInfo.InvariantCulture);
        fixture.FallbackProcessIds.Add(brokerProcessId);

        Assert.IsTrue(await WaitUntilAsync(
            () => !ProcessExists(brokerProcessId) &&
                !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists &&
                !fixture.Manager.InspectReadyEvent(fixture.Names.ReadyEventName).Exists &&
                !fixture.Manager.InspectCancelEvent(fixture.Names.CancelEventName).Exists,
            DefaultTimeout));
    }

    [TestMethod]
    public async Task LauncherAbruptExit_AfterResumeBeforeDurableCommit_RollsBackWholeJob()
    {
        using var fixture = CreateFixture();
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"cpl-resumed-precommit-{Guid.NewGuid():N}.txt");
        fixture.TemporaryFiles.Add(markerPath);
        var nameParts = fixture.Names.JobObjectName.Split('.');
        var startInfo = new ProcessStartInfo
        {
            FileName = GetTestHostExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--resume-before-durable-exit-probe");
        startInfo.ArgumentList.Add(fixture.Manager.BrokerExecutablePath);
        startInfo.ArgumentList.Add(nameParts[^2]);
        startInfo.ArgumentList.Add(nameParts[^1]);
        startInfo.ArgumentList.Add(markerPath);

        using var probe = Process.Start(startInfo)
            ?? throw new AssertFailedException("无法启动 precommit abrupt-exit probe helper。");
        Assert.IsTrue(probe.WaitForExit(20_000));
        Assert.AreEqual(0, probe.ExitCode);
        Assert.IsTrue(File.Exists(markerPath));
        var processIds = (await File.ReadAllTextAsync(markerPath))
            .Split('|')
            .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.HasCount(2, processIds);
        fixture.FallbackProcessIds.AddRange(processIds);

        Assert.IsTrue(await WaitUntilAsync(
            () => processIds.All(processId => !ProcessExists(processId)) &&
                !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists &&
                !fixture.Manager.InspectReadyEvent(fixture.Names.ReadyEventName).Exists &&
                !fixture.Manager.InspectCancelEvent(fixture.Names.CancelEventName).Exists,
            DefaultTimeout));
    }

    [TestMethod]
    public async Task BrokerProtocol_OversizedFrameFailsClosedWithoutCreatingRoot()
    {
        using var fixture = CreateFixture();
        using var broker = fixture.Manager.StartBroker(fixture.Names);
        using var pipe = WindowsJobBrokerProtocol.CreateClient(
            WindowsJobObjectManager.CreateControlPipeName(fixture.Names.JobObjectName));
        pipe.Connect(5_000);
        Assert.IsTrue(WindowsJobBrokerProtocol.IsExpectedServer(
            pipe,
            broker.BrokerProcessId,
            out var serverDetails), serverDetails);

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, 8 * 1024 * 1024 + 1);
        await pipe.WriteAsync(header);
        await pipe.FlushAsync();

        Assert.IsTrue(await WaitUntilAsync(
            () => !ProcessExists(broker.BrokerProcessId) &&
                !fixture.Manager.Inspect(fixture.Names.JobObjectName).Exists,
            DefaultTimeout));
        pipe.Dispose();
        broker.Dispose();
        Assert.IsFalse(fixture.Manager.InspectReadyEvent(fixture.Names.ReadyEventName).Exists);
        Assert.IsFalse(fixture.Manager.InspectCancelEvent(fixture.Names.CancelEventName).Exists);
    }

    [TestMethod]
    public void MalformedBrokerCommand_IsHandledFailClosedAndNamesRejectWrongNamespace()
    {
        Assert.IsTrue(WindowsJobBroker.TryRun(["--job-broker", "--job-name"], out var exitCode));
        Assert.AreNotEqual(0, exitCode);

        using var fixture = CreateFixture();
        var localNames = fixture.Names with
        {
            JobObjectName = fixture.Names.JobObjectName.Replace("Global\\", "Local\\", StringComparison.Ordinal),
        };
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Manager.StartBroker(localNames));
    }

    private static TestFixture CreateFixture()
    {
        var brokerExecutable = BuildOutputPaths.RequireBrokerExecutable();
        var dotnetRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)
                ?? throw new AssertFailedException("无法定位测试 .NET runtime。"),
            "..",
            "..",
            ".."));
        var manager = new WindowsJobObjectManager(
            brokerExecutable,
            new Dictionary<string, string?>
            {
                ["DOTNET_ROOT"] = dotnetRoot,
                ["DOTNET_ROOT_X64"] = dotnetRoot,
            });
        return new(manager, manager.CreateNames(Guid.NewGuid(), Guid.NewGuid()));
    }

    private static TestFixture CreateTestHostFixture()
    {
        var testHostExecutable = GetTestHostExecutablePath();
        var manager = new WindowsJobObjectManager(testHostExecutable);
        return new(manager, manager.CreateNames(Guid.NewGuid(), Guid.NewGuid()));
    }

    private static string GetTestHostExecutablePath() =>
        BuildOutputPaths.RequireTestHostExecutable();

    private static (string Reached, string Release) CreatePauseNames(TestFixture fixture)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new AssertFailedException("无法读取测试用户 SID。");
        var launchIdText = fixture.Names.JobObjectName[
            (fixture.Names.JobObjectName.LastIndexOf('.') + 1)..];
        return (
            $@"Global\CodexProfileLauncher.JobTestPauseReached.v1.{sid}.{launchIdText}",
            $@"Global\CodexProfileLauncher.JobTestPauseRelease.v1.{sid}.{launchIdText}");
    }

    private static ProcessStartInfo ControlledUnreadyBrokerStartInfo(
        TestFixture fixture,
        string pauseReachedName,
        string pauseReleaseName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fixture.Manager.BrokerExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fixture.Manager.BrokerExecutablePath),
        };
        startInfo.Environment["CODEX_PROFILE_LAUNCHER_TEST_PAUSE_REACHED_EVENT"] =
            pauseReachedName;
        startInfo.Environment["CODEX_PROFILE_LAUNCHER_TEST_PAUSE_RELEASE_EVENT"] =
            pauseReleaseName;
        foreach (var argument in new[]
                 {
                     "--job-broker",
                     "--job-name",
                     fixture.Names.JobObjectName,
                     "--ready-event",
                     fixture.Names.ReadyEventName,
                     "--cancel-event",
                     fixture.Names.CancelEventName,
                     "--parent-pid",
                     int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--parent-start-ticks",
                     "1",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string GetWorkspaceRoot() => BuildOutputPaths.GetWorkspaceRoot();

    private static ProcessStartInfo SleepProcessStartInfo(int seconds)
    {
        var startInfo = PowerShellStartInfo();
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"Start-Sleep -Seconds {seconds}");
        return startInfo;
    }

    private static RunningInstanceReceipt CreatePendingReceipt(
        TestFixture fixture,
        WindowsJobObjectManager.JobBrokerConnection? broker)
    {
        var nameParts = fixture.Names.JobObjectName.Split('.');
        return new()
        {
            ProfileId = Guid.ParseExact(nameParts[^2], "N"),
            LaunchId = Guid.ParseExact(nameParts[^1], "N"),
            OwnershipMode = ProcessOwnershipModes.WindowsJob,
            OwnershipVersion = ProcessOwnershipModes.WindowsJobVersion,
            LaunchPhase = JobLaunchPhases.PendingIntent,
            JobObjectName = fixture.Names.JobObjectName,
            ReadyEventName = fixture.Names.ReadyEventName,
            BrokerProcessId = broker?.BrokerProcessId ?? 0,
            BrokerProcessStartUtcTicks = broker?.BrokerProcessStartUtcTicks ?? 0,
            WindowsSessionId = fixture.Manager.CurrentWindowsSessionId,
            IsLaunchPending = true,
        };
    }

    private static ProcessStartInfo ChildSpawnerStartInfo(string markerPath)
    {
        var escapedMarker = markerPath.Replace("'", "''", StringComparison.Ordinal);
        var startInfo = PowerShellStartInfo();
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$child = Start-Process -FilePath $PSHOME\\powershell.exe " +
            "-ArgumentList '-NoLogo','-NoProfile','-Command','Start-Sleep -Seconds 60' -PassThru; " +
            $"[IO.File]::WriteAllText('{escapedMarker}', [string]$child.Id)");
        return startInfo;
    }

    private static ProcessStartInfo PowerShellStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        return startInfo;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return predicate();
    }

    private static bool IsExactProcessAlive(int processId, long processStartUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == processStartUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class TestFixture : IDisposable
    {
        public TestFixture(WindowsJobObjectManager manager, WindowsJobNames names)
        {
            Manager = manager;
            Names = names;
        }

        public WindowsJobObjectManager Manager { get; }

        public WindowsJobNames Names { get; }

        public List<int> FallbackProcessIds { get; } = [];

        public List<string> TemporaryFiles { get; } = [];

        public void Dispose()
        {
            try
            {
                if (Manager.Inspect(Names.JobObjectName).Exists)
                {
                    _ = Manager.TerminateAndWaitForStableEmptyAsync(
                            Names.JobObjectName,
                            TimeSpan.FromSeconds(5))
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch (WindowsJobObjectException)
            {
                // The broker may already have closed the final handle.
            }

            foreach (var processId in FallbackProcessIds)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        _ = process.WaitForExit(5_000);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // Already gone.
                }
            }

            foreach (var path in TemporaryFiles)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Test cleanup is best effort after process cleanup.
                }
            }
        }
    }
}
