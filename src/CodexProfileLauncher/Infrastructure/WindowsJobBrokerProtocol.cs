using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexProfileLauncher.Infrastructure;

internal sealed record BrokerCreateProcessRequest(
    int ProtocolVersion,
    string ApplicationPath,
    string WorkingDirectory,
    string[] Arguments,
    Dictionary<string, string> Environment,
    bool CreateNoWindow);

internal sealed record BrokerCreateProcessResponse(
    int ProtocolVersion,
    bool Succeeded,
    string Code,
    string Message,
    string Details,
    int ProcessId,
    int ThreadId,
    long ProcessStartUtcTicks,
    string ExecutablePath,
    int WindowsSessionId,
    long LauncherProcessHandle,
    long LauncherThreadHandle);

internal sealed record BrokerCreateProcessControl(
    int ProtocolVersion,
    string Action);

internal static partial class WindowsJobBrokerProtocol
{
    internal const int Version = 1;
    internal const string ResumeAction = "resumed";
    internal const string CommitAction = "commit-durable";
    internal const string AbortAction = "abort";
    private const int MaximumFrameBytes = 8 * 1024 * 1024;
    private const string PipePrefix = "CodexProfileLauncher.RootCreate.v1.";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string CreatePipeName(string currentUserSid, Guid launchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserSid);
        if (launchId == Guid.Empty)
        {
            throw new ArgumentException("launchId 不能为空。", nameof(launchId));
        }

        return $"{PipePrefix}{currentUserSid}.{launchId:N}";
    }

    internal static NamedPipeServerStream CreateServer(string pipeName)
    {
        ValidatePipeName(pipeName);
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.CurrentUserOnly |
            PipeOptions.FirstPipeInstance,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024);
    }

    internal static NamedPipeClientStream CreateClient(string pipeName)
    {
        ValidatePipeName(pipeName);
        return new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.CurrentUserOnly);
    }

    internal static bool IsExpectedClient(
        NamedPipeServerStream pipe,
        int expectedProcessId,
        out string details)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected)
        {
            details = "Named pipe 尚未连接。";
            return false;
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            details = $"GetNamedPipeClientProcessId Win32={Marshal.GetLastPInvokeError()}。";
            return false;
        }

        details = $"ExpectedPID={expectedProcessId}, ActualPID={processId}。";
        return processId == checked((uint)expectedProcessId);
    }

    internal static bool IsExpectedServer(
        NamedPipeClientStream pipe,
        int expectedProcessId,
        out string details)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected)
        {
            details = "Named pipe 尚未连接。";
            return false;
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            details = $"GetNamedPipeServerProcessId Win32={Marshal.GetLastPInvokeError()}。";
            return false;
        }

        details = $"ExpectedPID={expectedProcessId}, ActualPID={processId}。";
        return processId == checked((uint)expectedProcessId);
    }

    internal static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length <= 0 || payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"Broker IPC frame 大小无效：{payload.Length} bytes。");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"Broker IPC frame 长度越界：{length} bytes。");
        }

        var payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("Broker IPC frame 无法反序列化。");
    }

    private static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!pipeName.StartsWith(PipePrefix, StringComparison.Ordinal) ||
            pipeName.Length > 240 ||
            pipeName.Contains('\\') ||
            pipeName.Contains('/'))
        {
            throw new ArgumentException("Broker IPC pipe 名称无效。", nameof(pipeName));
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipeHandle,
        out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipeHandle,
        out uint serverProcessId);
}
