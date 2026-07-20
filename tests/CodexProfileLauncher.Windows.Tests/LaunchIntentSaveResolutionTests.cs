using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Infrastructure;
using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
public sealed class LaunchIntentSaveResolutionTests
{
    [TestMethod]
    public void ExactNextRevisionWithOwnLaunchId_ConfirmsCommit()
    {
        var profileId = Guid.NewGuid();
        var launchId = Guid.NewGuid();
        var state = State(12, profileId, launchId);

        var result = MainWindowViewModel.ClassifyLaunchIntentSaveResolution(
            state,
            profileId,
            launchId,
            expectedRevision: 11);

        Assert.AreEqual(LaunchIntentSaveResolution.CommitConfirmed, result);
    }

    [TestMethod]
    public void SameRevisionWithoutOwnLaunchId_ProvesNoCommit()
    {
        var profileId = Guid.NewGuid();
        var state = State(11, profileId, activeLaunchId: null);

        var result = MainWindowViewModel.ClassifyLaunchIntentSaveResolution(
            state,
            profileId,
            Guid.NewGuid(),
            expectedRevision: 11);

        Assert.AreEqual(LaunchIntentSaveResolution.NotCommitted, result);
    }

    [TestMethod]
    public void LaterRevisionEvenWithOwnLaunchId_IsReloadedAsSuperseded()
    {
        var profileId = Guid.NewGuid();
        var launchId = Guid.NewGuid();
        var state = State(13, profileId, launchId);

        var result = MainWindowViewModel.ClassifyLaunchIntentSaveResolution(
            state,
            profileId,
            launchId,
            expectedRevision: 11);

        Assert.AreEqual(LaunchIntentSaveResolution.StateReloaded, result);
    }

    [TestMethod]
    public void ExactBreakawayIncomplete_EnablesCompatibilityLaunch()
    {
        var exception = new WindowsJobObjectException(
            "JOB_BROKER_BREAKAWAY_INCOMPLETE",
            "strict detachment failed",
            "synthetic");

        Assert.IsTrue(MainWindowViewModel.ShouldUseCompatibilityLaunch(exception));
    }

    [TestMethod]
    public void SuspendedCreateAccessDenied_EnablesCompatibilityLaunch()
    {
        var exception = new WindowsJobObjectException(
            "PROCESS_CREATE_SUSPENDED_ACCESS_DENIED",
            "suspended create denied",
            "Win32=5");

        Assert.IsTrue(MainWindowViewModel.ShouldUseCompatibilityLaunch(exception));
    }

    [TestMethod]
    public void GenericSuspendedCreateFailure_DoesNotEnableCompatibilityLaunch()
    {
        var exception = new WindowsJobObjectException(
            "PROCESS_CREATE_SUSPENDED_FAILED",
            "suspended create failed",
            "Win32=193");

        Assert.IsFalse(MainWindowViewModel.ShouldUseCompatibilityLaunch(exception));
    }

    [TestMethod]
    public void JobAssignFailure_DoesNotEnableCompatibilityLaunch()
    {
        var exception = new WindowsJobObjectException(
            "JOB_ASSIGN_SUSPENDED_FAILED",
            "job assign failed",
            "Win32=5");

        Assert.IsFalse(MainWindowViewModel.ShouldUseCompatibilityLaunch(exception));
    }

    [TestMethod]
    public void SuspendedCreateFailureClassification_OnlySpecializesAccessDenied()
    {
        Assert.AreEqual(
            "PROCESS_CREATE_SUSPENDED_ACCESS_DENIED",
            WindowsJobObjectManager.ClassifySuspendedCreateFailure(5));
        Assert.AreEqual(
            "PROCESS_CREATE_SUSPENDED_ACCESS_DENIED",
            WindowsJobObjectManager.ClassifySuspendedCreateFailure(unchecked((int)0x80070005)));
        Assert.AreEqual(
            "PROCESS_CREATE_SUSPENDED_ACCESS_DENIED",
            WindowsJobObjectManager.ClassifySuspendedCreateFailure(unchecked((int)0xC0070005)));
        Assert.AreEqual(
            "PROCESS_CREATE_SUSPENDED_FAILED",
            WindowsJobObjectManager.ClassifySuspendedCreateFailure(2));
        Assert.AreEqual(
            "PROCESS_CREATE_SUSPENDED_FAILED",
            WindowsJobObjectManager.ClassifySuspendedCreateFailure(193));
    }

    [TestMethod]
    public void OtherJobFailure_DoesNotEnableCompatibilityLaunch()
    {
        var exception = new WindowsJobObjectException(
            "JOB_ROOT_ARGUMENTS_UNVERIFIED",
            "root verification failed",
            "synthetic");

        Assert.IsFalse(MainWindowViewModel.ShouldUseCompatibilityLaunch(exception));
    }

    [TestMethod]
    public void CompatibilityIntent_ClearsJobGenerationAndRetainsLaunchIdentity()
    {
        var profileId = Guid.NewGuid();
        var launchId = Guid.NewGuid();
        var receipt = new RunningInstanceReceipt
        {
            ProfileId = profileId,
            LaunchId = launchId,
            OwnershipMode = ProcessOwnershipModes.WindowsJob,
            OwnershipVersion = ProcessOwnershipModes.WindowsJobVersion,
            LaunchPhase = JobLaunchPhases.PendingIntent,
            JobObjectName = @"Global\CodexProfileLauncher.Job.v1.test",
            ReadyEventName = @"Global\CodexProfileLauncher.JobReady.v1.test",
            WindowsSessionId = 4,
            BrokerProcessId = 123,
            BrokerProcessStartUtcTicks = 456,
            IsLaunchPending = false,
            IsIsolationVerified = true,
            RootProcessId = 789,
            ProcessStartUtcTicks = 101112,
            ExecutablePath = @"C:\Codex.exe",
            CodexHomePath = @"C:\profile\codex-home",
            AppDataPath = @"C:\profile\app-data",
            ObservedProcesses =
            [
                new ObservedProcessIdentity
                {
                    ProcessId = 789,
                    ProcessStartUtcTicks = 101112,
                    ExecutablePath = @"C:\Codex.exe",
                },
            ],
        };

        MainWindowViewModel.InitializeCompatibilityLaunchIntent(receipt);

        Assert.AreEqual(profileId, receipt.ProfileId);
        Assert.AreEqual(launchId, receipt.LaunchId);
        Assert.AreEqual(ProcessOwnershipModes.LegacyProcessTree, receipt.OwnershipMode);
        Assert.AreEqual(ProcessOwnershipModes.LegacyProcessTreeVersion, receipt.OwnershipVersion);
        Assert.AreEqual(string.Empty, receipt.LaunchPhase);
        Assert.AreEqual(string.Empty, receipt.JobObjectName);
        Assert.AreEqual(string.Empty, receipt.ReadyEventName);
        Assert.AreEqual(-1, receipt.WindowsSessionId);
        Assert.AreEqual(0, receipt.BrokerProcessId);
        Assert.AreEqual(0L, receipt.BrokerProcessStartUtcTicks);
        Assert.IsTrue(receipt.IsLaunchPending);
        Assert.IsFalse(receipt.IsIsolationVerified);
        Assert.AreEqual(0, receipt.RootProcessId);
        Assert.AreEqual(0L, receipt.ProcessStartUtcTicks);
        Assert.IsEmpty(receipt.ObservedProcesses);
        Assert.AreEqual(@"C:\Codex.exe", receipt.ExecutablePath);
        Assert.AreEqual(@"C:\profile\codex-home", receipt.CodexHomePath);
        Assert.AreEqual(@"C:\profile\app-data", receipt.AppDataPath);
    }

    private static LauncherState State(long revision, Guid profileId, Guid? activeLaunchId) => new()
    {
        Revision = revision,
        Profiles =
        [
            new CodexProfile
            {
                Id = profileId,
                Name = "测试环境",
                DataRoot = @"C:\profiles\test",
                WorkingDirectory = @"C:\workspace",
                ActiveInstance = activeLaunchId is { } launchId
                    ? new RunningInstanceReceipt { ProfileId = profileId, LaunchId = launchId }
                    : null,
            },
        ],
    };
}
