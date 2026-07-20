using System.Diagnostics;
using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Services;

public sealed class CodexProcessLauncher
{
    private static readonly string[] ParentApiCredentialVariables =
    [
        "OPENAI_API_KEY",
        "CODEX_API_KEY",
        "CODEX_ACCESS_TOKEN",
    ];

    public static ProcessStartInfo BuildStartInfo(
        CodexProfile profile,
        CodexInstallation installation,
        ProfileAiLaunchConfiguration aiConfiguration)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(aiConfiguration);

        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        var workingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
            ? paths.DataRoot
            : PathUtilities.Normalize(profile.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = PathUtilities.Normalize(installation.ExecutablePath),
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = false,
        };

        startInfo.ArgumentList.Add($"--user-data-dir={paths.AppData}");
        startInfo.ArgumentList.Add("--new-window");
        foreach (var variable in ParentApiCredentialVariables)
        {
            _ = startInfo.Environment.Remove(variable);
        }

        _ = startInfo.Environment.Remove(ProfileAiLaunchConfiguration.ApiKeyEnvironmentVariable);
        if (aiConfiguration.ApiEnabled)
        {
            if (string.IsNullOrWhiteSpace(aiConfiguration.ApiKey))
            {
                throw new ArgumentException("启用 API 时 Key 不能为空。", nameof(aiConfiguration));
            }

            // Custom provider uses env_key only — do not inject OPENAI_API_KEY
            // (that can pull Desktop into ChatGPT login / official provider flow).
            startInfo.Environment[ProfileAiLaunchConfiguration.ApiKeyEnvironmentVariable] = aiConfiguration.ApiKey;
        }

        startInfo.Environment["CODEX_HOME"] = paths.CodexHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = paths.CodexHome;
        startInfo.Environment["CODEX_ELECTRON_USER_DATA_PATH"] = paths.AppData;
        return startInfo;
    }

}

public enum ProcessReceiptStatus
{
    Stopped,
    VerifiedRunning,
    Unknown,
}

public sealed record ProcessReceiptCheck(
    ProcessReceiptStatus Status,
    string Message,
    string Details,
    Process? Process = null);

public sealed class ProcessReceiptVerifier
{
    public static ProcessReceiptCheck Check(RunningInstanceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (receipt.IsLaunchPending || receipt.RootProcessId <= 0 || receipt.ProcessStartUtcTicks <= 0)
        {
            return new(
                ProcessReceiptStatus.Unknown,
                "启动状态尚未提交。",
                "启动器已保存启动意图，但尚未确认根进程身份；已阻止重复启动。");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(receipt.RootProcessId);
        }
        catch (ArgumentException)
        {
            return new(ProcessReceiptStatus.Stopped, "Codex 已停止。", "记录的进程已不存在。");
        }

        try
        {
            if (process.HasExited)
            {
                process.Dispose();
                return new(ProcessReceiptStatus.Stopped, "Codex 已停止。", "记录的进程已经退出。");
            }

            var actualStartTicks = process.StartTime.ToUniversalTime().Ticks;
            if (actualStartTicks != receipt.ProcessStartUtcTicks)
            {
                process.Dispose();
                return new(
                    ProcessReceiptStatus.Stopped,
                    "Codex 已停止。",
                    "记录的进程身份已不存在；该 PID 已被另一代进程复用。");
            }

            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath))
            {
                process.Dispose();
                return new(
                    ProcessReceiptStatus.Unknown,
                    "运行状态无法确认。",
                    "无法读取当前进程的可执行文件路径。");
            }

            if (!PathUtilities.Normalize(actualPath).Equals(
                    PathUtilities.Normalize(receipt.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return new(
                    ProcessReceiptStatus.Stopped,
                    "Codex 已停止。",
                    "记录的进程身份已不存在；当前 PID 的可执行文件路径不同。");
            }

            return new(ProcessReceiptStatus.VerifiedRunning, "Codex 正在运行。", $"PID={process.Id}", process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            return new(
                ProcessReceiptStatus.Unknown,
                "运行状态无法确认。",
                ex.Message);
        }
    }
}
