using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace CodexProfileLauncher.Infrastructure;

public sealed record WindowsJobBrokerRequest(
    string JobObjectName,
    string ReadyEventName,
    string CancelEventName,
    int ParentProcessId,
    long ParentProcessStartUtcTicks);

/// <summary>
/// Hidden same-executable mode that owns the last durable Job handle. App
/// startup must call <see cref="TryRun"/> before creating WPF state or mutexes.
/// A malformed --job-broker invocation is recognized and exits fail-closed.
/// </summary>
public static class WindowsJobBroker
{
    internal const string BrokerSwitch = "--job-broker";
    private const string JobNameSwitch = "--job-name";
    private const string ReadyEventSwitch = "--ready-event";
    private const string CancelEventSwitch = "--cancel-event";
    private const string ParentPidSwitch = "--parent-pid";
    private const string ParentStartSwitch = "--parent-start-ticks";
    private static readonly TimeSpan SetupLifetimeLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SampleDelay = TimeSpan.FromMilliseconds(200);
    private const int StableEmptySamples = 3;

    public static bool TryRun(string[] arguments, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0 ||
            !arguments[0].Equals(BrokerSwitch, StringComparison.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        exitCode = TryParseRequest(arguments, out var request) && request is not null
            ? Run(request)
            : 20;
        return true;
    }

    public static bool TryParseRequest(
        IReadOnlyList<string> arguments,
        out WindowsJobBrokerRequest? request)
    {
        request = null;
        if (arguments.Count == 0 ||
            !arguments[0].Equals(BrokerSwitch, StringComparison.Ordinal) ||
            (arguments.Count - 1) % 2 != 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index += 2)
        {
            var key = arguments[index];
            if (index + 1 >= arguments.Count ||
                !IsKnownValueSwitch(key) ||
                !values.TryAdd(key, arguments[index + 1]))
            {
                return false;
            }
        }

        if (values.Count != 5 ||
            !values.TryGetValue(JobNameSwitch, out var jobObjectName) ||
            !values.TryGetValue(ReadyEventSwitch, out var readyEventName) ||
            !values.TryGetValue(CancelEventSwitch, out var cancelEventName) ||
            !values.TryGetValue(ParentPidSwitch, out var parentPidText) ||
            !values.TryGetValue(ParentStartSwitch, out var parentStartText) ||
            !int.TryParse(
                parentPidText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentProcessId) ||
            !long.TryParse(
                parentStartText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentProcessStartUtcTicks) ||
            parentProcessId <= 0 ||
            parentProcessStartUtcTicks <= 0)
        {
            return false;
        }

        try
        {
            WindowsJobObjectManager.ValidateNames(
                new(jobObjectName, readyEventName, cancelEventName),
                WindowsJobObjectManager.CurrentUserSid());

            request = new(
                jobObjectName,
                readyEventName,
                cancelEventName,
                parentProcessId,
                parentProcessStartUtcTicks);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return true;
    }

    internal static void AppendBrokerArguments(
        ProcessStartInfo startInfo,
        WindowsJobBrokerRequest request)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(request);
        startInfo.ArgumentList.Add(BrokerSwitch);
        AddPair(startInfo, JobNameSwitch, request.JobObjectName);
        AddPair(startInfo, ReadyEventSwitch, request.ReadyEventName);
        AddPair(startInfo, CancelEventSwitch, request.CancelEventName);
        AddPair(
            startInfo,
            ParentPidSwitch,
            request.ParentProcessId.ToString(CultureInfo.InvariantCulture));
        AddPair(
            startInfo,
            ParentStartSwitch,
            request.ParentProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    private static int Run(WindowsJobBrokerRequest request)
    {
        try
        {
            return RunAsync(request).GetAwaiter().GetResult();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return 21;
        }
        catch (UnauthorizedAccessException)
        {
            return 22;
        }
        catch (WindowsJobObjectException)
        {
            return 23;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or OperationCanceledException or TimeoutException)
        {
            return 23;
        }
    }

    private static async Task<int> RunAsync(WindowsJobBrokerRequest request)
    {
        using var readyEvent = EventWaitHandle.OpenExisting(request.ReadyEventName);
        using var cancelEvent = EventWaitHandle.OpenExisting(request.CancelEventName);
        using var jobHandle = WindowsJobObjectManager.CreateFreshBrokerJob(request.JobObjectName);
        WindowsJobObjectManager.ConfigureAndVerifyKillOnClose(jobHandle);
        if (WindowsJobObjectManager.QueryProcessIds(jobHandle).Count != 0)
        {
            return 24;
        }

        var setupStopwatch = Stopwatch.StartNew();
        var committed = false;
        using (var pipe = WindowsJobBrokerProtocol.CreateServer(
                   WindowsJobObjectManager.CreateControlPipeName(request.JobObjectName)))
        {
            if (!readyEvent.Set())
            {
                return 25;
            }

            if (!await WaitForExpectedClientAsync(
                    pipe,
                    request,
                    cancelEvent,
                    setupStopwatch).ConfigureAwait(false))
            {
                return 0;
            }

            BrokerCreateProcessRequest createRequest;
            try
            {
                createRequest = await ReadWithTimeoutAsync<BrokerCreateProcessRequest>(
                        pipe,
                        TimeSpan.FromSeconds(20))
                    .ConfigureAwait(false);
                EnsurePreCommitOwner(request, cancelEvent, setupStopwatch);
            }
            catch (Exception ex)
            {
                await TryWriteFailureAsync(pipe, ex).ConfigureAwait(false);
                return 23;
            }

            WindowsJobObjectManager.BrokerSuspendedProcessTransfer transfer;
            try
            {
                transfer = WindowsJobObjectManager.CreateSuspendedForBroker(
                    jobHandle,
                    createRequest,
                    request.ParentProcessId,
                    request.ParentProcessStartUtcTicks);
            }
            catch (Exception ex)
            {
                await TryWriteFailureAsync(pipe, ex).ConfigureAwait(false);
                return 23;
            }

            using (transfer)
            {
                try
                {
                    await WriteWithTimeoutAsync(
                            pipe,
                            transfer.Response,
                            TimeSpan.FromSeconds(20))
                        .ConfigureAwait(false);
                    transfer.MarkResponseDelivered();

                    var resume = await ReadWithTimeoutAsync<BrokerCreateProcessControl>(
                            pipe,
                            TimeSpan.FromSeconds(20))
                        .ConfigureAwait(false);
                    EnsurePreCommitOwner(request, cancelEvent, setupStopwatch);
                    if (IsAbort(resume))
                    {
                        WindowsJobObjectManager.TerminateBrokerJob(jobHandle);
                        return 0;
                    }

                    ValidateControl(resume, WindowsJobBrokerProtocol.ResumeAction);
                    transfer.VerifyLiveMember();
                    await WriteWithTimeoutAsync(
                            pipe,
                            new BrokerCreateProcessControl(
                                WindowsJobBrokerProtocol.Version,
                                WindowsJobBrokerProtocol.ResumeAction),
                            TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);

                    var durableCommit = await ReadWithTimeoutAsync<BrokerCreateProcessControl>(
                            pipe,
                            TimeSpan.FromSeconds(30))
                        .ConfigureAwait(false);
                    EnsurePreCommitOwner(request, cancelEvent, setupStopwatch);
                    if (IsAbort(durableCommit))
                    {
                        WindowsJobObjectManager.TerminateBrokerJob(jobHandle);
                        return 0;
                    }

                    ValidateControl(durableCommit, WindowsJobBrokerProtocol.CommitAction);
                    transfer.VerifyLiveMember();
                    // A valid CommitAction is emitted only after the Resumed
                    // receipt is durably saved. From this point the broker is
                    // the keeper even if the acknowledgement cannot be read.
                    committed = true;
                    try
                    {
                        await WriteWithTimeoutAsync(
                                pipe,
                                new BrokerCreateProcessControl(
                                    WindowsJobBrokerProtocol.Version,
                                    WindowsJobBrokerProtocol.CommitAction),
                                TimeSpan.FromSeconds(10))
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is IOException or InvalidDataException or
                        OperationCanceledException or TimeoutException)
                    {
                        // The durable receipt is already the authority. The
                        // launcher will either accept the ACK or terminate the
                        // same pinned Job, both of which converge safely.
                    }
                }
                catch
                {
                    if (!committed)
                    {
                        WindowsJobObjectManager.TerminateBrokerJob(jobHandle);
                    }

                    if (!committed)
                    {
                        throw;
                    }
                }
            }
        }

        return RunCommittedKeeper(jobHandle);
    }

    private static async Task<bool> WaitForExpectedClientAsync(
        NamedPipeServerStream pipe,
        WindowsJobBrokerRequest request,
        EventWaitHandle cancelEvent,
        Stopwatch setupStopwatch)
    {
        const int maximumPeerAttempts = 4;
        for (var attempt = 1; attempt <= maximumPeerAttempts; attempt++)
        {
            while (!pipe.IsConnected)
            {
                if (cancelEvent.WaitOne(0) ||
                    !WindowsJobObjectManager.IsExactProcessAlive(
                        request.ParentProcessId,
                        request.ParentProcessStartUtcTicks) ||
                    setupStopwatch.Elapsed >= SetupLifetimeLimit)
                {
                    return false;
                }

                using var slice = new CancellationTokenSource(SampleDelay);
                try
                {
                    await pipe.WaitForConnectionAsync(slice.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (slice.IsCancellationRequested)
                {
                    continue;
                }
            }

            if (WindowsJobBrokerProtocol.IsExpectedClient(
                    pipe,
                    request.ParentProcessId,
                    out _) &&
                WindowsJobObjectManager.IsExactProcessAlive(
                    request.ParentProcessId,
                    request.ParentProcessStartUtcTicks))
            {
                return true;
            }

            pipe.Disconnect();
        }

        throw new WindowsJobObjectException(
            "JOB_BROKER_PIPE_PEER_REJECTED",
            "broker 原子创建通道连续收到非预期客户端。",
            $"ExpectedPID={request.ParentProcessId}, Attempts={maximumPeerAttempts}。");
    }

    private static void EnsurePreCommitOwner(
        WindowsJobBrokerRequest request,
        EventWaitHandle cancelEvent,
        Stopwatch setupStopwatch)
    {
        if (cancelEvent.WaitOne(0) ||
            setupStopwatch.Elapsed >= SetupLifetimeLimit ||
            !WindowsJobObjectManager.IsExactProcessAlive(
                request.ParentProcessId,
                request.ParentProcessStartUtcTicks))
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_SETUP_OWNER_LOST",
                "launcher 在 Job 创建事务提交前退出、取消或超时。",
                $"PID={request.ParentProcessId}, Elapsed={setupStopwatch.Elapsed}。");
        }
    }

    private static void ValidateControl(
        BrokerCreateProcessControl control,
        string expectedAction)
    {
        if (control.ProtocolVersion != WindowsJobBrokerProtocol.Version ||
            !control.Action.Equals(expectedAction, StringComparison.Ordinal))
        {
            throw new WindowsJobObjectException(
                "JOB_BROKER_CONTROL_INVALID",
                "broker 原子创建事务收到乱序或未知控制消息。",
                $"Expected={expectedAction}, Protocol={control.ProtocolVersion}, " +
                $"Actual={control.Action}。");
        }
    }

    private static bool IsAbort(BrokerCreateProcessControl control) =>
        control.ProtocolVersion == WindowsJobBrokerProtocol.Version &&
        control.Action.Equals(WindowsJobBrokerProtocol.AbortAction, StringComparison.Ordinal);

    private static async Task<T> ReadWithTimeoutAsync<T>(Stream stream, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return await WindowsJobBrokerProtocol
            .ReadAsync<T>(stream, cancellation.Token)
            .ConfigureAwait(false);
    }

    private static async Task WriteWithTimeoutAsync<T>(
        Stream stream,
        T value,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await WindowsJobBrokerProtocol
            .WriteAsync(stream, value, cancellation.Token)
            .ConfigureAwait(false);
    }

    private static async Task TryWriteFailureAsync(Stream stream, Exception exception)
    {
        var response = exception is WindowsJobObjectException windowsJobException
            ? new BrokerCreateProcessResponse(
                WindowsJobBrokerProtocol.Version,
                Succeeded: false,
                windowsJobException.Code,
                windowsJobException.Message,
                windowsJobException.Details,
                0,
                0,
                0,
                string.Empty,
                0,
                0,
                0)
            : new BrokerCreateProcessResponse(
                WindowsJobBrokerProtocol.Version,
                Succeeded: false,
                "JOB_BROKER_CREATE_REQUEST_FAILED",
                "broker 无法处理原子创建请求。",
                exception.Message,
                0,
                0,
                0,
                string.Empty,
                0,
                0,
                0);
        try
        {
            await WriteWithTimeoutAsync(stream, response, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or OperationCanceledException or TimeoutException)
        {
            // The broker exits fail-closed; the caller observes pipe failure.
        }
    }

    private static int RunCommittedKeeper(WindowsJobObjectManager.SafeJobHandle jobHandle)
    {
        var stableEmptyCount = 0;
        while (true)
        {
            var members = WindowsJobObjectManager.QueryProcessIds(jobHandle);
            stableEmptyCount = members.Count == 0
                ? stableEmptyCount + 1
                : 0;
            if (stableEmptyCount >= StableEmptySamples)
            {
                return 0;
            }

            Thread.Sleep(SampleDelay);
        }
    }

    private static bool IsKnownValueSwitch(string value) =>
        value.Equals(JobNameSwitch, StringComparison.Ordinal) ||
        value.Equals(ReadyEventSwitch, StringComparison.Ordinal) ||
        value.Equals(CancelEventSwitch, StringComparison.Ordinal) ||
        value.Equals(ParentPidSwitch, StringComparison.Ordinal) ||
        value.Equals(ParentStartSwitch, StringComparison.Ordinal);

    private static void AddPair(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }
}
