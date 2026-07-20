using System.Text;
using System.Text.Json;

namespace CodexProfileLauncher.Infrastructure;

public sealed class JsonlFileLogger
{
    private readonly object _gate = new();
    private readonly string _logsDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JsonlFileLogger(string logsDirectory)
    {
        _logsDirectory = logsDirectory;
        Directory.CreateDirectory(_logsDirectory);
    }

    public string CurrentLogPath =>
        Path.Combine(_logsDirectory, $"launcher-{DateTimeOffset.Now:yyyyMMdd}.jsonl");

    public void Info(
        string eventId,
        string message,
        Guid? profileId = null,
        object? details = null) =>
        Write("Information", eventId, message, profileId, details, null);

    public void Warning(
        string eventId,
        string message,
        Guid? profileId = null,
        object? details = null) =>
        Write("Warning", eventId, message, profileId, details, null);

    public void Error(
        string eventId,
        string message,
        Exception exception,
        Guid? profileId = null,
        object? details = null) =>
        Write("Error", eventId, message, profileId, details, exception);

    private void Write(
        string level,
        string eventId,
        string message,
        Guid? profileId,
        object? details,
        Exception? exception)
    {
        var record = new
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            EventId = eventId,
            OperationId = Guid.NewGuid(),
            ProfileId = profileId,
            Message = message,
            Details = details,
            ExceptionType = exception?.GetType().FullName,
            ExceptionMessage = exception?.Message,
            StackTrace = exception?.StackTrace,
        };

        var line = JsonSerializer.Serialize(record, _jsonOptions);
        lock (_gate)
        {
            using var stream = new FileStream(
                CurrentLogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                16 * 1024,
                FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }
}
