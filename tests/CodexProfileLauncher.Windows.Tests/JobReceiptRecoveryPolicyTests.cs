using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
public sealed class JobReceiptRecoveryPolicyTests
{
    private static readonly JobAbsenceEvidence DefinitiveAbsence = new(
        StableMissingSnapshots: 2,
        BrokerDefinitivelyAbsent: true,
        RootAndObservedDefinitivelyAbsent: true,
        ProfileScanDefinitivelyEmpty: true,
        HasInspectionError: false);

    [TestMethod]
    public void Pending_BeforeBrokerCreation_WaitsThenClearsOnlyAfterDefinitiveDrain()
    {
        var young = JobReceiptRecoveryPolicy.DecidePending(
            jobExists: false,
            JobReadySignalState.Missing,
            TimeSpan.FromSeconds(10),
            DefinitiveAbsence);
        var expired = JobReceiptRecoveryPolicy.DecidePending(
            jobExists: false,
            JobReadySignalState.Missing,
            TimeSpan.FromMinutes(3),
            DefinitiveAbsence);

        Assert.AreEqual(PendingJobRecoveryAction.Wait, young);
        Assert.AreEqual(PendingJobRecoveryAction.ClearReceipt, expired);
    }

    [TestMethod]
    public void Pending_AfterBrokerReady_AlwaysAbortsOwnedJob()
    {
        var action = JobReceiptRecoveryPolicy.DecidePending(
            jobExists: true,
            JobReadySignalState.PresentSignaled,
            TimeSpan.FromMilliseconds(1),
            default);

        Assert.AreEqual(PendingJobRecoveryAction.AbortOwnedJob, action);
    }

    [TestMethod]
    public void Pending_OneSidedNameLoss_IsBlocked()
    {
        var action = JobReceiptRecoveryPolicy.DecidePending(
            jobExists: false,
            JobReadySignalState.PresentSignaled,
            TimeSpan.FromMinutes(3),
            DefinitiveAbsence);

        Assert.AreEqual(PendingJobRecoveryAction.Block, action);
    }

    [TestMethod]
    public void Pending_UnsignaledReady_WaitsThenRequiresPinnedReclaim()
    {
        var young = JobReceiptRecoveryPolicy.DecidePending(
            jobExists: true,
            JobReadySignalState.PresentUnsignaled,
            TimeSpan.FromSeconds(30),
            default);
        var expired = JobReceiptRecoveryPolicy.DecidePending(
            jobExists: true,
            JobReadySignalState.PresentUnsignaled,
            TimeSpan.FromMinutes(3),
            default);

        Assert.AreEqual(PendingJobRecoveryAction.Wait, young);
        Assert.AreEqual(PendingJobRecoveryAction.ReclaimExpiredUnready, expired);
    }

    [TestMethod]
    public void ResumedMissing_ClearsOnlyAfterTwoMissingAndAllExactIdentitiesAbsent()
    {
        var oneSnapshot = DefinitiveAbsence with { StableMissingSnapshots = 1 };
        var brokerStillAlive = DefinitiveAbsence with { BrokerDefinitivelyAbsent = false };

        Assert.AreEqual(
            MissingJobCompletionAction.Wait,
            JobReceiptRecoveryPolicy.DecideResumedMissing(oneSnapshot));
        Assert.AreEqual(
            MissingJobCompletionAction.Block,
            JobReceiptRecoveryPolicy.DecideResumedMissing(brokerStillAlive));
        Assert.AreEqual(
            MissingJobCompletionAction.ClearReceipt,
            JobReceiptRecoveryPolicy.DecideResumedMissing(DefinitiveAbsence));
    }

    [TestMethod]
    public void InspectionError_NeverProducesClearOrAbort()
    {
        var evidence = DefinitiveAbsence with { HasInspectionError = true };

        Assert.AreEqual(
            PendingJobRecoveryAction.Block,
            JobReceiptRecoveryPolicy.DecidePending(
                jobExists: false,
                JobReadySignalState.Missing,
                TimeSpan.FromMinutes(3),
                evidence));
        Assert.AreEqual(
            MissingJobCompletionAction.Block,
            JobReceiptRecoveryPolicy.DecideResumedMissing(evidence));
    }

    [TestMethod]
    public void InteractiveWindowControl_IsLimitedToOriginatingWindowsSession()
    {
        Assert.IsTrue(JobReceiptRecoveryPolicy.CanUseInteractiveWindowControl(3, 3));
        Assert.IsFalse(JobReceiptRecoveryPolicy.CanUseInteractiveWindowControl(3, 4));
        Assert.IsFalse(JobReceiptRecoveryPolicy.CanUseInteractiveWindowControl(-1, 3));
    }
}
