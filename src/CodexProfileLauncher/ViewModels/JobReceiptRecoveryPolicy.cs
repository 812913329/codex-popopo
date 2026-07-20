namespace CodexProfileLauncher.ViewModels;

internal enum JobReadySignalState
{
    Missing,
    PresentUnsignaled,
    PresentSignaled,
    InspectionError,
}

internal enum PendingJobRecoveryAction
{
    Wait,
    AbortOwnedJob,
    ReclaimExpiredUnready,
    ClearReceipt,
    Block,
}

internal enum MissingJobCompletionAction
{
    Wait,
    ClearReceipt,
    Block,
}

internal readonly record struct JobAbsenceEvidence(
    int StableMissingSnapshots,
    bool BrokerDefinitivelyAbsent,
    bool RootAndObservedDefinitivelyAbsent,
    bool ProfileScanDefinitivelyEmpty,
    bool HasInspectionError);

internal static class JobReceiptRecoveryPolicy
{
    internal static readonly TimeSpan PendingRecoveryWindow = TimeSpan.FromMinutes(2);

    public static PendingJobRecoveryAction DecidePending(
        bool jobExists,
        JobReadySignalState readySignal,
        TimeSpan receiptAge,
        JobAbsenceEvidence absence)
    {
        if (receiptAge < TimeSpan.Zero || absence.HasInspectionError)
        {
            return PendingJobRecoveryAction.Block;
        }

        if (jobExists)
        {
            return readySignal switch
            {
                JobReadySignalState.PresentSignaled => PendingJobRecoveryAction.AbortOwnedJob,
                JobReadySignalState.PresentUnsignaled
                    when receiptAge < PendingRecoveryWindow => PendingJobRecoveryAction.Wait,
                JobReadySignalState.PresentUnsignaled => PendingJobRecoveryAction.ReclaimExpiredUnready,
                _ => PendingJobRecoveryAction.Block,
            };
        }

        if (readySignal != JobReadySignalState.Missing)
        {
            // A one-sided name loss is not a valid ownership transition.
            return PendingJobRecoveryAction.Block;
        }

        if (receiptAge < PendingRecoveryWindow || absence.StableMissingSnapshots < 2)
        {
            return PendingJobRecoveryAction.Wait;
        }

        return HasDefinitiveIdentityDrain(absence)
            ? PendingJobRecoveryAction.ClearReceipt
            : PendingJobRecoveryAction.Block;
    }

    public static MissingJobCompletionAction DecideResumedMissing(
        JobAbsenceEvidence absence)
    {
        if (absence.HasInspectionError)
        {
            return MissingJobCompletionAction.Block;
        }

        if (absence.StableMissingSnapshots < 2)
        {
            return MissingJobCompletionAction.Wait;
        }

        return HasDefinitiveIdentityDrain(absence)
            ? MissingJobCompletionAction.ClearReceipt
            : MissingJobCompletionAction.Block;
    }

    public static bool CanUseInteractiveWindowControl(
        int receiptWindowsSessionId,
        int currentWindowsSessionId) =>
        receiptWindowsSessionId >= 0 &&
        receiptWindowsSessionId == currentWindowsSessionId;

    private static bool HasDefinitiveIdentityDrain(JobAbsenceEvidence absence) =>
        absence.BrokerDefinitivelyAbsent &&
        absence.RootAndObservedDefinitivelyAbsent &&
        absence.ProfileScanDefinitivelyEmpty;
}
