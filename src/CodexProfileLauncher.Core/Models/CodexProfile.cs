namespace CodexProfileLauncher.Core.Models;

public sealed class CodexProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string DataRoot { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastStartedUtc { get; set; }

    public string? LastVerifiedCodexVersion { get; set; }

    public RunningInstanceReceipt? ActiveInstance { get; set; }
}

public sealed class RunningInstanceReceipt
{
    public int SchemaVersion { get; set; } = 1;

    public Guid ProfileId { get; set; }

    public Guid LaunchId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Empty on receipts written by releases that predate durable ownership.
    /// New Windows launches use <see cref="ProcessOwnershipModes.WindowsJob"/>.
    /// </summary>
    public string OwnershipMode { get; set; } = string.Empty;

    public int OwnershipVersion { get; set; }

    /// <summary>
    /// Durable launch phase for job-backed launches. An empty value denotes a
    /// legacy receipt and must never be interpreted as a Job Object guarantee.
    /// </summary>
    public string LaunchPhase { get; set; } = string.Empty;

    public string JobObjectName { get; set; } = string.Empty;

    /// <summary>
    /// Named event set by the broker only after it owns a fresh, empty Job
    /// Object with the permanent KILL_ON_JOB_CLOSE limit verified. The broker
    /// then creates the root suspended, assigns it to the Job, and transfers
    /// restricted handles so the launcher can verify and resume the thread.
    /// </summary>
    public string ReadyEventName { get; set; } = string.Empty;

    /// <summary>
    /// Terminal Services session that owns the job members. Global\ makes the
    /// ownership object queryable across sessions, but UI activation and
    /// graceful window-close remain valid only in the originating session.
    /// </summary>
    public int WindowsSessionId { get; set; } = -1;

    /// <summary>
    /// Exact identity of the hidden same-executable broker that keeps the
    /// named Job Object alive after the UI launcher closes.
    /// </summary>
    public int BrokerProcessId { get; set; }

    public long BrokerProcessStartUtcTicks { get; set; }

    /// <summary>
    /// True while the durable launch intent has been committed but the child
    /// process identity has not yet been committed. This closes the brokered
    /// CreateProcessW/Job/state-save crash window across launcher processes.
    /// </summary>
    public bool IsLaunchPending { get; set; }

    /// <summary>
    /// True only after this exact PID/start-time launch has passed the full
    /// argv, window, app-data, CODEX_HOME and app-server verification gate.
    /// </summary>
    public bool IsIsolationVerified { get; set; }

    public int RootProcessId { get; set; }

    public long ProcessStartUtcTicks { get; set; }

    public string ExecutablePath { get; set; } = string.Empty;

    public string CodexVersion { get; set; } = string.Empty;

    public string CodexHomePath { get; set; } = string.Empty;

    public string AppDataPath { get; set; } = string.Empty;

    public DateTimeOffset LaunchedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<ObservedProcessIdentity> ObservedProcesses { get; set; } = [];
}

public static class ProcessOwnershipModes
{
    public const string LegacyProcessTree = "legacy-process-tree";

    public const int LegacyProcessTreeVersion = 1;

    public const string WindowsJob = "windows-job";

    public const int WindowsJobVersion = 1;

    public static bool IsWindowsJob(RunningInstanceReceipt receipt) =>
        receipt.OwnershipMode.Equals(WindowsJob, StringComparison.Ordinal) &&
        receipt.OwnershipVersion == WindowsJobVersion;

    public static bool IsLegacy(RunningInstanceReceipt receipt) =>
        string.IsNullOrEmpty(receipt.OwnershipMode) ||
        (receipt.OwnershipMode.Equals(LegacyProcessTree, StringComparison.Ordinal) &&
         receipt.OwnershipVersion is 0 or LegacyProcessTreeVersion);
}

public static class JobLaunchPhases
{
    public const string PendingIntent = "pending-intent";

    public const string Resumed = "resumed";
}

public sealed class ObservedProcessIdentity
{
    public int ProcessId { get; set; }

    public int ParentProcessId { get; set; }

    public long ProcessStartUtcTicks { get; set; }

    public string ExecutablePath { get; set; } = string.Empty;
}

public sealed class LauncherState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public long Revision { get; set; }

    public Guid? SelectedProfileId { get; set; }

    public List<CodexProfile> Profiles { get; set; } = [];
}
