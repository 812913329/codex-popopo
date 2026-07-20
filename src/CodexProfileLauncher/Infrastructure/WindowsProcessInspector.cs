using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CodexProfileLauncher.Core.Models;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("CodexProfileLauncher.Windows.Tests")]

namespace CodexProfileLauncher.Infrastructure;

public sealed record DiscoveredCodexProcess(
    ObservedProcessIdentity Identity,
    string CommandLine);

public sealed record ProcessDiscoveryResult(
    IReadOnlyList<DiscoveredCodexProcess> Matches,
    IReadOnlyList<string> InspectionErrors);

public sealed record ProcessTreeInspectionResult(
    IReadOnlyList<ObservedProcessIdentity> Identities,
    IReadOnlyList<string> InspectionErrors);

public sealed record LiveIdentityInspectionResult(
    IReadOnlyList<ObservedProcessIdentity> LiveIdentities,
    IReadOnlyList<string> InspectionErrors);

public sealed record ProcessTerminationResult(
    IReadOnlyList<int> TerminatedProcessIds,
    IReadOnlyList<string> InspectionErrors,
    IReadOnlyList<ObservedProcessIdentity> ObservedIdentities);

/// <summary>
/// Reads process identity and command-line facts from Windows itself. The
/// launcher uses this both to verify a new child and to recover a durable
/// launch intent after either launcher process exits unexpectedly.
/// </summary>
public sealed partial class WindowsProcessInspector
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint TokenQuery = 0x0008;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const int TokenUserInformationClass = 1;
    private const int ErrorInvalidParameter = 87;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const int MaximumWin32PathCharacters = 32_768;
    private const uint ForcedExitCode = 1;
    private const int MaximumPostExitPasses = 8;
    private const int RequiredStablePostExitPasses = 2;

    private readonly string _currentUserSid =
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");
    private readonly int _currentSessionId = Process.GetCurrentProcess().SessionId;

    public ProcessDiscoveryResult FindProfileRoots(string executablePath, string appDataPath)
    {
        var expectedExecutable = PathUtilities.Normalize(executablePath);
        var processName = Path.GetFileNameWithoutExtension(expectedExecutable);
        var matches = new List<DiscoveredCodexProcess>();
        var errors = new List<string>();

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (!TryGetProcessOwnerSid(process.Id, out var ownerSid))
                {
                    if (TryGetSessionId(process, out var sessionId) && sessionId == _currentSessionId)
                    {
                        errors.Add($"PID={process.Id}: 无法核验同一桌面会话中的进程属主。");
                    }

                    // A process in another session whose token cannot be read is
                    // not attributable to this user's profile. Same-user
                    // cross-session tokens are readable and continue below.
                    continue;
                }

                if (!ownerSid.Equals(_currentUserSid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryCaptureIdentity(process.Id, out var identity, out var identityError, out _))
                {
                    errors.Add($"PID={process.Id}: {identityError}");
                    continue;
                }

                if (!PathUtilities.Normalize(identity.ExecutablePath).Equals(
                        expectedExecutable,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryReadCommandLine(process.Id, out var commandLine, out var commandLineError))
                {
                    errors.Add($"PID={process.Id}: {commandLineError}");
                    continue;
                }

                IReadOnlyList<string> arguments;
                try
                {
                    arguments = ParseCommandLine(commandLine);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    errors.Add($"PID={process.Id}: 命令行解析失败：{ex.Message}");
                    continue;
                }

                if (ArgumentsUseProfile(arguments, appDataPath, requireNewWindow: true))
                {
                    matches.Add(new(identity, commandLine));
                }
            }
        }

        return new(
            matches.OrderBy(match => match.Identity.ProcessStartUtcTicks).ToArray(),
            errors);
    }

    public bool VerifyProfileRootArguments(int processId, string appDataPath, out string details)
    {
        if (!TryReadCommandLine(processId, out var commandLine, out var error))
        {
            details = error;
            return false;
        }

        try
        {
            var arguments = ParseCommandLine(commandLine);
            if (!ArgumentsUseProfile(arguments, appDataPath, requireNewWindow: true))
            {
                details = $"PID={processId} 的命令行没有精确指向当前环境的 --user-data-dir 与 --new-window。";
                return false;
            }

            details = $"PID={processId} 的 --user-data-dir 已精确匹配当前环境。";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            details = $"PID={processId} 的命令行解析失败：{ex.Message}";
            return false;
        }
    }

    public ProcessTreeInspectionResult CaptureProcessTree(int rootProcessId)
    {
        if (!TryCaptureIdentity(rootProcessId, out var rootIdentity, out var error, out var definitelyAbsent))
        {
            return definitelyAbsent
                ? new([], [])
                : new([], [$"PID={rootProcessId}: {error}"]);
        }

        return CaptureProcessTree(rootIdentity);
    }

    public ProcessTreeInspectionResult CaptureProcessTree(ObservedProcessIdentity expectedRoot) =>
        CaptureProcessTree(expectedRoot, trustedMissingAnchors: []);

    private ProcessTreeInspectionResult CaptureProcessTree(
        ObservedProcessIdentity expectedRoot,
        IReadOnlyList<ObservedProcessIdentity> trustedMissingAnchors)
    {
        ArgumentNullException.ThrowIfNull(expectedRoot);
        var candidates = new List<ObservedProcessIdentity>();
        var errors = new List<string>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var isNamedCodexProcess = TryGetProcessName(process, out var processName) &&
                                          IsRelevantCodexProcessName(processName);

                if (!TryGetProcessOwnerSid(process.Id, out var ownerSid))
                {
                    if ((isNamedCodexProcess || process.Id == expectedRoot.ProcessId) &&
                        TryGetSessionId(process, out var sessionId) &&
                        sessionId == _currentSessionId)
                    {
                        errors.Add($"PID={process.Id}: 无法核验同一桌面会话中的进程属主。");
                    }

                    continue;
                }

                if (!ownerSid.Equals(_currentUserSid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryCaptureIdentity(process.Id, out var identity, out var error, out var definitelyAbsent))
                {
                    if (identity.ProcessId == expectedRoot.ProcessId &&
                        !IdentityMatchesExactly(identity, expectedRoot))
                    {
                        // The expected root is gone and the numeric PID belongs
                        // to a later process generation. Treat the old identity
                        // as absent; never let the replacement seed ownership.
                        continue;
                    }

                    candidates.Add(identity);
                }
                else if (!definitelyAbsent && (isNamedCodexProcess || process.Id == expectedRoot.ProcessId))
                {
                    errors.Add($"PID={process.Id}: {error}");
                }
            }
        }

        var identities = SelectStrictProcessTree(expectedRoot, candidates, trustedMissingAnchors);
        return new(identities, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    public LiveIdentityInspectionResult FindLiveIdentities(
        IEnumerable<ObservedProcessIdentity> identities)
    {
        var live = new List<ObservedProcessIdentity>();
        var errors = new List<string>();
        foreach (var expected in identities)
        {
            if (!TryCaptureIdentity(
                    expected.ProcessId,
                    out var actual,
                    out var error,
                    out var definitelyAbsent))
            {
                if (!definitelyAbsent)
                {
                    errors.Add($"PID={expected.ProcessId}: {error}");
                }

                continue;
            }

            if (IdentityMatchesExactly(actual, expected))
            {
                live.Add(actual);
            }
        }

        return new(live, errors);
    }

    public ProcessTerminationResult TerminateVerifiedIdentities(
        ObservedProcessIdentity expectedRoot,
        IEnumerable<ObservedProcessIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(identities);

        var observed = MergeIdentities(identities).ToList();
        var terminated = new List<int>();
        var errors = new List<string>();

        if (!TryOpenVerifiedIdentityHandle(
                expectedRoot,
                ProcessTerminate,
                out var rootHandle,
                out var rootError,
                out var rootDefinitelyAbsent))
        {
            if (!rootDefinitelyAbsent)
            {
                errors.Add($"PID={expectedRoot.ProcessId}: {rootError}");
                return new(terminated, errors, observed);
            }

            return TerminateWithAbsentRoot(expectedRoot, observed);
        }

        var leases = new List<VerifiedProcessLease>
        {
            new(expectedRoot, rootHandle),
        };
        try
        {
            var beforeTermination = CaptureProcessTree(expectedRoot, trustedMissingAnchors: []);
            if (beforeTermination.InspectionErrors.Count > 0)
            {
                errors.AddRange(beforeTermination.InspectionErrors);
                return new(terminated, errors, observed);
            }

            observed = MergeIdentities(observed, beforeTermination.Identities).ToList();
            foreach (var identity in beforeTermination.Identities)
            {
                if (IdentityMatchesExactly(identity, expectedRoot))
                {
                    continue;
                }

                if (!TryOpenVerifiedIdentityHandle(
                        identity,
                        ProcessTerminate,
                        out var handle,
                        out var error,
                        out _))
                {
                    errors.Add(
                        $"PID={identity.ProcessId}: 无法建立完整所有权租约：{error}");
                    return new(terminated, errors, observed);
                }

                leases.Add(new(identity, handle));
            }

            TerminateLeases(expectedRoot, leases, terminated, errors);

            var rootWait = WaitForSingleObject(rootHandle, 5_000);
            if (rootWait != WaitObject0 && rootWait != WaitTimeout)
            {
                errors.Add(
                    $"PID={expectedRoot.ProcessId}: 等待根进程退出失败：" +
                    new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }
            else if (rootWait == WaitTimeout)
            {
                errors.Add($"PID={expectedRoot.ProcessId}: 根进程在 5 秒内未退出。");
            }

            RunLeasedPostExitQuiescence(
                expectedRoot,
                leases,
                ref observed,
                terminated,
                errors);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        return new(
            terminated.Distinct().ToArray(),
            errors.Distinct(StringComparer.Ordinal).ToArray(),
            observed);
    }

    private ProcessTerminationResult TerminateWithAbsentRoot(
        ObservedProcessIdentity expectedRoot,
        List<ObservedProcessIdentity> observed)
    {
        var terminated = new List<int>();
        var errors = new List<string>();
        var liveCheck = FindLiveIdentities(observed);
        if (liveCheck.InspectionErrors.Count > 0)
        {
            return new(terminated, liveCheck.InspectionErrors, observed);
        }

        var liveDescendants = liveCheck.LiveIdentities
            .Where(identity => !IdentityMatchesExactly(identity, expectedRoot))
            .ToArray();
        if (liveDescendants.Length == 0)
        {
            return new(terminated, errors, observed);
        }

        var authorized = SelectStrictProcessTree(expectedRoot, observed);
        if (!TryValidateLiveLineage(
                expectedRoot,
                authorized,
                liveDescendants,
                out var lineageError))
        {
            errors.Add(lineageError);
            return new(terminated, errors, observed);
        }

        var leases = new List<VerifiedProcessLease>();
        try
        {
            foreach (var identity in liveDescendants)
            {
                if (!TryOpenVerifiedIdentityHandle(
                        identity,
                        ProcessTerminate,
                        out var handle,
                        out var error,
                        out _))
                {
                    errors.Add(
                        $"PID={identity.ProcessId}: root 已退出时无法建立完整后代租约：{error}");
                    return new(terminated, errors, observed);
                }

                leases.Add(new(identity, handle));
            }

            TerminateLeases(expectedRoot, leases, terminated, errors);
            RunLeasedPostExitQuiescence(
                expectedRoot,
                leases,
                ref observed,
                terminated,
                errors);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        return new(
            terminated.Distinct().ToArray(),
            errors.Distinct(StringComparer.Ordinal).ToArray(),
            observed);
    }

    internal static bool TryValidateLiveLineage(
        ObservedProcessIdentity expectedRoot,
        IReadOnlyList<ObservedProcessIdentity> authorized,
        IReadOnlyList<ObservedProcessIdentity> liveDescendants,
        out string error)
    {
        foreach (var identity in liveDescendants)
        {
            if (!authorized.Any(candidate => IdentityMatchesExactly(candidate, identity)))
            {
                error = $"PID={identity.ProcessId}: live 后代不在持久化授权链中。";
                return false;
            }

            var current = identity;
            while (current.ParentProcessId != expectedRoot.ProcessId)
            {
                var parent = authorized
                    .Where(candidate =>
                        candidate.ProcessId == current.ParentProcessId &&
                        candidate.ProcessStartUtcTicks < current.ProcessStartUtcTicks)
                    .OrderByDescending(candidate => candidate.ProcessStartUtcTicks)
                    .FirstOrDefault();
                if (parent is null ||
                    !liveDescendants.Any(candidate => IdentityMatchesExactly(candidate, parent)))
                {
                    error =
                        $"PID={identity.ProcessId}: root 已退出且关键父代身份不再存活，" +
                        "无法证明完整 lineage。";
                    return false;
                }

                current = parent;
            }
        }

        error = string.Empty;
        return true;
    }

    private void RunLeasedPostExitQuiescence(
        ObservedProcessIdentity expectedRoot,
        List<VerifiedProcessLease> leases,
        ref List<ObservedProcessIdentity> observed,
        List<int> terminated,
        List<string> errors)
    {
        // Every trusted missing anchor below still owns an open handle, so its
        // PID cannot be reused while post-exit descendants are linked.
        var stablePostExitPasses = 0;
        for (var pass = 0;
             pass < MaximumPostExitPasses && errors.Count == 0;
             pass++)
        {
            var trustedAnchors = leases.Select(lease => lease.Identity).ToArray();
            var postExit = CaptureProcessTree(expectedRoot, trustedAnchors);
            if (postExit.InspectionErrors.Count > 0)
            {
                errors.AddRange(postExit.InspectionErrors);
                break;
            }

            observed = MergeIdentities(observed, postExit.Identities).ToList();
            var newLeases = new List<VerifiedProcessLease>();
            foreach (var identity in postExit.Identities)
            {
                if (leases.Any(lease => IdentityMatchesExactly(lease.Identity, identity)))
                {
                    continue;
                }

                if (!TryOpenVerifiedIdentityHandle(
                        identity,
                        ProcessTerminate,
                        out var handle,
                        out var error,
                        out _))
                {
                    errors.Add(
                        $"PID={identity.ProcessId}: post-exit 所有权租约失败：{error}");
                    break;
                }

                newLeases.Add(new(identity, handle));
            }

            if (errors.Count > 0)
            {
                foreach (var lease in newLeases)
                {
                    lease.Dispose();
                }

                break;
            }

            leases.AddRange(newLeases);
            TerminateLeases(expectedRoot, newLeases, terminated, errors);
            if (!TryCheckAllLeasesExited(leases, out var allLeasesExited, out var leaseError))
            {
                errors.Add(leaseError);
                break;
            }

            stablePostExitPasses = NextStablePostExitPassCount(
                stablePostExitPasses,
                discoveredNewIdentity: newLeases.Count > 0,
                allLeasesExited);
            if (stablePostExitPasses >= RequiredStablePostExitPasses)
            {
                break;
            }

            Thread.Sleep(100);
        }

        if (errors.Count == 0 && stablePostExitPasses < RequiredStablePostExitPasses)
        {
            errors.Add(
                $"post-exit 进程树在 {MaximumPostExitPasses} 轮内未达到连续" +
                $" {RequiredStablePostExitPasses} 轮稳定空状态。");
        }
    }

    private static void TerminateLeases(
        ObservedProcessIdentity expectedRoot,
        IReadOnlyList<VerifiedProcessLease> leases,
        List<int> terminated,
        List<string> errors)
    {
        foreach (var identity in OrderForTermination(
                     expectedRoot,
                     leases.Select(lease => lease.Identity)))
        {
            var lease = leases.First(candidate =>
                IdentityMatchesExactly(candidate.Identity, identity));
            if (TryTerminateVerifiedHandle(lease.Handle, out var error, out var definitelyAbsent))
            {
                terminated.Add(identity.ProcessId);
            }
            else if (!definitelyAbsent)
            {
                errors.Add($"PID={identity.ProcessId}: {error}");
            }
        }
    }

    private static bool TryCheckAllLeasesExited(
        IEnumerable<VerifiedProcessLease> leases,
        out bool allExited,
        out string error)
    {
        allExited = true;
        error = string.Empty;
        foreach (var lease in leases)
        {
            var waitResult = WaitForSingleObject(lease.Handle, 0);
            if (waitResult == WaitObject0)
            {
                continue;
            }

            if (waitResult == WaitTimeout)
            {
                allExited = false;
                continue;
            }

            error =
                $"PID={lease.Identity.ProcessId}: 检查租约进程退出状态失败：" +
                new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return false;
        }

        return true;
    }

    internal static int NextStablePostExitPassCount(
        int currentCount,
        bool discoveredNewIdentity,
        bool allLeasesExited) =>
        !discoveredNewIdentity && allLeasesExited
            ? currentCount + 1
            : 0;

    internal static bool HasStablePostExitQuiescence(int stablePassCount) =>
        stablePassCount >= RequiredStablePostExitPasses;

    internal static IReadOnlyList<ObservedProcessIdentity> OrderForTermination(
        ObservedProcessIdentity expectedRoot,
        IEnumerable<ObservedProcessIdentity> identities)
    {
        var materialized = identities.ToArray();
        return materialized
            .OrderBy(identity => IdentityMatchesExactly(identity, expectedRoot) ? 1 : 0)
            .ThenByDescending(identity => GetExpectedDepth(identity, materialized))
            .ThenByDescending(identity => identity.ProcessStartUtcTicks)
            .ToArray();
    }

    internal static IReadOnlyList<ObservedProcessIdentity> SelectStrictProcessTree(
        ObservedProcessIdentity expectedRoot,
        IEnumerable<ObservedProcessIdentity> candidates) =>
        SelectStrictProcessTree(expectedRoot, candidates, trustedMissingAnchors: []);

    internal static IReadOnlyList<ObservedProcessIdentity> SelectStrictProcessTreeWithTrustedAnchors(
        ObservedProcessIdentity expectedRoot,
        IEnumerable<ObservedProcessIdentity> candidates,
        IEnumerable<ObservedProcessIdentity> trustedMissingAnchors) =>
        SelectStrictProcessTree(expectedRoot, candidates, trustedMissingAnchors.ToArray());

    private static ObservedProcessIdentity[] SelectStrictProcessTree(
        ObservedProcessIdentity expectedRoot,
        IEnumerable<ObservedProcessIdentity> candidates,
        IReadOnlyList<ObservedProcessIdentity> trustedMissingAnchors)
    {
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(candidates);

        var remaining = candidates
            .GroupBy(identity => (identity.ProcessId, identity.ProcessStartUtcTicks))
            .Select(group => group.First())
            .Where(identity =>
                identity.ProcessId != expectedRoot.ProcessId ||
                IdentityMatchesExactly(identity, expectedRoot))
            .ToList();
        var selected = new List<ObservedProcessIdentity>();
        var ownershipAnchors = trustedMissingAnchors
            .GroupBy(identity => (identity.ProcessId, identity.ProcessStartUtcTicks))
            .Select(group => group.First())
            .ToList();

        var liveRoot = remaining.FirstOrDefault(identity => IdentityMatchesExactly(identity, expectedRoot));
        if (liveRoot is not null)
        {
            selected.Add(liveRoot);
            ownershipAnchors.Add(liveRoot);
            remaining.Remove(liveRoot);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                var candidate = remaining[index];
                var followsSelectedParent = ownershipAnchors.Concat(selected).Any(parent =>
                    parent.ProcessId == candidate.ParentProcessId &&
                    candidate.ProcessStartUtcTicks > parent.ProcessStartUtcTicks);
                if (!followsSelectedParent)
                {
                    continue;
                }

                selected.Add(candidate);
                remaining.RemoveAt(index);
                changed = true;
            }
        }

        return selected
            .OrderBy(identity => IdentityMatchesExactly(identity, expectedRoot) ? 0 : 1)
            .ThenBy(identity => identity.ProcessStartUtcTicks)
            .ToArray();
    }

    public static IReadOnlyList<ObservedProcessIdentity> MergeIdentities(
        params IEnumerable<ObservedProcessIdentity>[] groups) =>
        groups
            .SelectMany(group => group)
            .GroupBy(identity => (identity.ProcessId, identity.ProcessStartUtcTicks))
            .Select(group => group.First())
            .OrderBy(identity => identity.ProcessStartUtcTicks)
            .ToArray();

    public static IReadOnlyList<string> ParseCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new InvalidOperationException("进程命令行为空。");
        }

        var argv = CommandLineToArgv(commandLine, out var argumentCount);
        if (argv == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CommandLineToArgvW 失败。");
        }

        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                var argumentPointer = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(argumentPointer) ?? string.Empty;
            }

            return arguments;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    public static bool ArgumentsUseProfile(
        IReadOnlyList<string> arguments,
        string appDataPath,
        bool requireNewWindow)
    {
        var expectedAppData = PathUtilities.Normalize(appDataPath);
        var userDataValues = new List<string>();

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            const string prefix = "--user-data-dir=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                userDataValues.Add(argument[prefix.Length..]);
                continue;
            }

            if (argument.Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
            {
                userDataValues.Add(arguments[++index]);
            }
        }

        if (userDataValues.Count != 1)
        {
            return false;
        }

        string actualAppData;
        try
        {
            actualAppData = PathUtilities.Normalize(userDataValues[0]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var hasExpectedUserData = actualAppData.Equals(
            expectedAppData,
            StringComparison.OrdinalIgnoreCase);
        var hasNewWindow = arguments.Any(argument =>
            argument.Equals("--new-window", StringComparison.OrdinalIgnoreCase));
        return hasExpectedUserData && (!requireNewWindow || hasNewWindow);
    }

    private static bool TryCaptureIdentity(
        int processId,
        out ObservedProcessIdentity identity,
        out string error,
        out bool definitelyAbsent)
    {
        identity = new();
        error = string.Empty;
        definitelyAbsent = false;

        using var processHandle = OpenProcess(
            ProcessQueryLimitedInformation | Synchronize,
            false,
            processId);
        if (processHandle.IsInvalid)
        {
            var nativeError = Marshal.GetLastPInvokeError();
            error = new Win32Exception(nativeError).Message;
            definitelyAbsent = nativeError == ErrorInvalidParameter;
            return false;
        }

        var waitResult = WaitForSingleObject(processHandle, 0);
        if (waitResult == WaitObject0)
        {
            error = "进程已经退出。";
            definitelyAbsent = true;
            return false;
        }

        if (waitResult != WaitTimeout)
        {
            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return false;
        }

        return TryReadIdentityFromHandle(processHandle, processId, out identity, out error);
    }

    private static bool TryOpenVerifiedIdentityHandle(
        ObservedProcessIdentity expected,
        uint additionalAccess,
        out SafeProcessHandle processHandle,
        out string error,
        out bool definitelyAbsent)
    {
        error = string.Empty;
        definitelyAbsent = false;
        processHandle = OpenProcess(
            ProcessQueryLimitedInformation | Synchronize | additionalAccess,
            false,
            expected.ProcessId);
        if (processHandle.IsInvalid)
        {
            var nativeError = Marshal.GetLastPInvokeError();
            error = new Win32Exception(nativeError).Message;
            definitelyAbsent = nativeError == ErrorInvalidParameter;
            return false;
        }

        var waitResult = WaitForSingleObject(processHandle, 0);
        if (waitResult == WaitObject0)
        {
            processHandle.Dispose();
            processHandle = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
            error = "进程已经退出。";
            definitelyAbsent = true;
            return false;
        }

        if (waitResult != WaitTimeout)
        {
            processHandle.Dispose();
            processHandle = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return false;
        }

        if (!TryReadIdentityFromHandle(
                processHandle,
                expected.ProcessId,
                out var actual,
                out error))
        {
            processHandle.Dispose();
            processHandle = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
            return false;
        }

        if (!IdentityMatchesExactly(actual, expected))
        {
            processHandle.Dispose();
            processHandle = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
            error = "PID 已复用或进程身份与持久化记录不一致。";
            definitelyAbsent = true;
            return false;
        }

        return true;
    }

    private static bool TryTerminateVerifiedHandle(
        SafeProcessHandle processHandle,
        out string error,
        out bool definitelyAbsent)
    {
        error = string.Empty;
        definitelyAbsent = false;
        if (WaitForSingleObject(processHandle, 0) == WaitObject0)
        {
            error = "进程已经退出。";
            definitelyAbsent = true;
            return false;
        }

        if (!TerminateProcess(processHandle, ForcedExitCode))
        {
            var nativeError = Marshal.GetLastPInvokeError();
            if (WaitForSingleObject(processHandle, 0) == WaitObject0)
            {
                definitelyAbsent = true;
                error = "进程已经退出。";
                return false;
            }

            error = new Win32Exception(nativeError).Message;
            return false;
        }

        return true;
    }

    private static int GetExpectedDepth(
        ObservedProcessIdentity identity,
        IReadOnlyList<ObservedProcessIdentity> identities)
    {
        var depth = 0;
        var seen = new HashSet<(int ProcessId, long StartTicks)>
        {
            (identity.ProcessId, identity.ProcessStartUtcTicks),
        };
        var current = identity;
        while (current.ParentProcessId > 0)
        {
            var parentIdentity = identities
                .Where(candidate =>
                    candidate.ProcessId == current.ParentProcessId &&
                    candidate.ProcessStartUtcTicks < current.ProcessStartUtcTicks)
                .OrderByDescending(candidate => candidate.ProcessStartUtcTicks)
                .FirstOrDefault();
            if (parentIdentity is null ||
                !seen.Add((parentIdentity.ProcessId, parentIdentity.ProcessStartUtcTicks)))
            {
                break;
            }

            depth++;
            current = parentIdentity;
        }

        return depth;
    }

    private static bool IdentityMatchesExactly(
        ObservedProcessIdentity actual,
        ObservedProcessIdentity expected) =>
        actual.ProcessId == expected.ProcessId &&
        actual.ProcessStartUtcTicks == expected.ProcessStartUtcTicks &&
        PathUtilities.Normalize(actual.ExecutablePath).Equals(
            PathUtilities.Normalize(expected.ExecutablePath),
            StringComparison.OrdinalIgnoreCase);

    private static bool TryReadIdentityFromHandle(
        SafeProcessHandle processHandle,
        int processId,
        out ObservedProcessIdentity identity,
        out string error)
    {
        identity = new();
        error = string.Empty;
        if (!GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return false;
        }

        if (!TryReadExecutablePath(processHandle, out var executablePath, out error))
        {
            return false;
        }

        if (!TryGetParentProcessId(processHandle, out var parentProcessId))
        {
            error = "无法读取进程父级。";
            return false;
        }

        try
        {
            identity = new()
            {
                ProcessId = processId,
                ParentProcessId = parentProcessId,
                ProcessStartUtcTicks = DateTime.FromFileTimeUtc(creationTime.ToInt64()).Ticks,
                ExecutablePath = PathUtilities.Normalize(executablePath),
            };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or
                                   NotSupportedException or PathTooLongException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static unsafe bool TryReadExecutablePath(
        SafeProcessHandle processHandle,
        out string executablePath,
        out string error)
    {
        executablePath = string.Empty;
        error = string.Empty;
        Span<char> buffer = stackalloc char[MaximumWin32PathCharacters];
        fixed (char* bufferPointer = buffer)
        {
            var size = (uint)buffer.Length;
            if (!QueryFullProcessImageName(
                    processHandle,
                    0,
                    bufferPointer,
                    ref size))
            {
                error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
                return false;
            }

            executablePath = new string(bufferPointer, 0, checked((int)size));
            return !string.IsNullOrWhiteSpace(executablePath);
        }
    }

    private static bool IsRelevantCodexProcessName(string processName) =>
        processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("codex-code-mode-host", StringComparison.OrdinalIgnoreCase);

    private sealed record VerifiedProcessLease(
        ObservedProcessIdentity Identity,
        SafeProcessHandle Handle) : IDisposable
    {
        public void Dispose() => Handle.Dispose();
    }

    private static bool TryGetProcessName(Process process, out string processName)
    {
        try
        {
            processName = process.ProcessName;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            processName = string.Empty;
            return false;
        }
    }

    private static bool TryGetSessionId(Process process, out int sessionId)
    {
        try
        {
            sessionId = process.SessionId;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            sessionId = -1;
            return false;
        }
    }

    private static bool TryGetParentProcessId(int processId, out int parentProcessId)
    {
        parentProcessId = 0;
        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid)
        {
            return false;
        }

        return TryGetParentProcessId(processHandle, out parentProcessId);
    }

    private static bool TryGetParentProcessId(
        SafeProcessHandle processHandle,
        out int parentProcessId)
    {
        parentProcessId = 0;
        var size = Marshal.SizeOf<ProcessBasicInformationNative>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQueryInformationProcess(
                processHandle,
                ProcessBasicInformation,
                buffer,
                size,
                out _);
            if (status < 0)
            {
                return false;
            }

            var information = Marshal.PtrToStructure<ProcessBasicInformationNative>(buffer);
            parentProcessId = unchecked((int)information.InheritedFromUniqueProcessId.ToInt64());
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadCommandLine(
        int processId,
        out string commandLine,
        out string error)
    {
        commandLine = string.Empty;
        error = string.Empty;
        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        _ = NtQueryInformationProcess(
            processHandle,
            ProcessCommandLineInformation,
            IntPtr.Zero,
            0,
            out var requiredLength);
        if (requiredLength <= Marshal.SizeOf<UnicodeStringNative>())
        {
            error = "Windows 未返回命令行缓冲区长度。";
            return false;
        }

        var buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            var status = NtQueryInformationProcess(
                processHandle,
                ProcessCommandLineInformation,
                buffer,
                requiredLength,
                out _);
            if (status < 0)
            {
                error = $"NtQueryInformationProcess 返回 0x{status:X8}。";
                return false;
            }

            var value = Marshal.PtrToStructure<UnicodeStringNative>(buffer);
            if (value.Buffer == IntPtr.Zero || value.Length == 0)
            {
                error = "Windows 返回了空命令行。";
                return false;
            }

            commandLine = Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char)) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(commandLine);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetProcessOwnerSid(int processId, out string ownerSid)
    {
        ownerSid = string.Empty;
        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid ||
            !OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            return false;
        }

        using (tokenHandle)
        {
            _ = GetTokenInformation(
                tokenHandle,
                TokenUserInformationClass,
                IntPtr.Zero,
                0,
                out var requiredLength);
            if (requiredLength <= 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(
                        tokenHandle,
                        TokenUserInformationClass,
                        buffer,
                        requiredLength,
                        out _))
                {
                    return false;
                }

                var tokenUser = Marshal.PtrToStructure<TokenUserNative>(buffer);
                ownerSid = new SecurityIdentifier(tokenUser.User.Sid).Value;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeStringNative
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        public readonly uint LowDateTime;
        public readonly uint HighDateTime;

        public long ToInt64() =>
            unchecked((long)(((ulong)HighDateTime << 32) | LowDateTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessBasicInformationNative
    {
        public readonly IntPtr Reserved1;
        public readonly IntPtr PebBaseAddress;
        public readonly IntPtr Reserved2A;
        public readonly IntPtr Reserved2B;
        public readonly IntPtr UniqueProcessId;
        public readonly IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributesNative
    {
        public readonly IntPtr Sid;
        public readonly uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenUserNative
    {
        public readonly SidAndAttributesNative User;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle processHandle,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        char* executablePath,
        ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(
        SafeProcessHandle processHandle,
        uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(
        SafeProcessHandle processHandle,
        uint exitCode);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgv(string commandLine, out int argumentCount);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
