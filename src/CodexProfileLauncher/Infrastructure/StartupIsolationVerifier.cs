using System.Diagnostics;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;

namespace CodexProfileLauncher.Infrastructure;

public sealed record StartupVerificationResult(
    bool IsVerified,
    string Message,
    string Details,
    IReadOnlyList<ObservedProcessIdentity> ObservedProcesses);

public sealed class StartupIsolationVerifier(WindowsProcessInspector processInspector)
{
    public async Task<StartupVerificationResult> VerifyAsync(
        Process process,
        RunningInstanceReceipt receipt,
        ProfilePaths paths,
        DateTimeOffset launchedUtc,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var lastDetails = "尚未收集到启动证据。";

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited)
            {
                return Failure(
                    "Codex 启动后立即退出。",
                    $"退出码：{process.ExitCode}");
            }

            var receiptCheck = ProcessReceiptVerifier.Check(receipt);
            using var receiptProcess = receiptCheck.Process;
            if (receiptCheck.Status != ProcessReceiptStatus.VerifiedRunning)
            {
                lastDetails = $"根进程身份未通过：{receiptCheck.Details}";
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!processInspector.VerifyProfileRootArguments(process.Id, paths.AppData, out var argumentDetails))
            {
                lastDetails = argumentDetails;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var tree = processInspector.CaptureProcessTree(process.Id);
            var observed = tree.Identities;
            var hasWindow = process.MainWindowHandle != IntPtr.Zero;
            var hasAppDataEvidence = HasRecentAppData(paths.AppData, launchedUtc);
            var hasCodexHomeEvidence = HasRecentCodexHome(paths.CodexHome, launchedUtc);
            var hasAppServer = observed.Any(identity =>
                identity.ProcessId != process.Id &&
                Path.GetFileName(identity.ExecutablePath)
                    .Equals("codex.exe", StringComparison.OrdinalIgnoreCase));

            if (hasWindow &&
                hasAppDataEvidence &&
                hasCodexHomeEvidence &&
                hasAppServer &&
                tree.InspectionErrors.Count == 0)
            {
                return new(
                    true,
                    "Codex 已启动。",
                    $"已核对根进程身份、精确 --user-data-dir、独立窗口、app-data 写入、CODEX_HOME 状态写入与 app-server；{argumentDetails}",
                    observed);
            }

            lastDetails =
                $"窗口={hasWindow}；app-data 写入={hasAppDataEvidence}；CODEX_HOME 写入={hasCodexHomeEvidence}；app-server={hasAppServer}；进程树检查错误={tree.InspectionErrors.Count}。";
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return Failure(
            "Codex 已运行，但隔离状态尚未确认。",
            $"30 秒内未集齐全部启动证据。最后状态：{lastDetails}");
    }

    private static StartupVerificationResult Failure(string message, string details) =>
        new(false, message, details, []);

    private static bool HasRecentAppData(string appDataDirectory, DateTimeOffset launchedUtc) =>
        HasRecentEntry(
            appDataDirectory,
            launchedUtc,
            static _ => true);

    private static bool HasRecentCodexHome(string codexHomeDirectory, DateTimeOffset launchedUtc) =>
        HasRecentEntry(
            codexHomeDirectory,
            launchedUtc,
            static path =>
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                var name = Path.GetFileName(path);
                return name.Equals("installation_id", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals(".personality_migration", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("state_", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("logs_", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("goals_", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("memories_", StringComparison.OrdinalIgnoreCase);
            });

    private static bool HasRecentEntry(
        string directory,
        DateTimeOffset launchedUtc,
        Func<string, bool> include)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            return Directory
                .EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
                .Take(512)
                .Where(include)
                .Any(path =>
                {
                    var creation = File.Exists(path)
                        ? File.GetCreationTimeUtc(path)
                        : Directory.GetCreationTimeUtc(path);
                    var lastWrite = File.Exists(path)
                        ? File.GetLastWriteTimeUtc(path)
                        : Directory.GetLastWriteTimeUtc(path);
                    var threshold = launchedUtc.UtcDateTime.AddSeconds(-2);
                    return creation >= threshold || lastWrite >= threshold;
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
