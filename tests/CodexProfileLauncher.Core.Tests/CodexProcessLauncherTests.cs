using System.Diagnostics;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class CodexProcessLauncherTests
{
    [TestMethod]
    [DoNotParallelize]
    public void BuildStartInfo_UsesIsolatedPathsAndRemovesParentApiCredentials()
    {
        using var temp = new TemporaryDirectory();
        var profile = new CodexProfile
        {
            Name = "中文 环境",
            DataRoot = temp.Combine("profile with spaces"),
            WorkingDirectory = temp.Combine("工作 目录"),
        };
        var executable = temp.Combine("app", "ChatGPT.exe");
        var installation = new CodexInstallation(
            "OpenAI.Codex_1.0.0.0_x64__test",
            "OpenAI.Codex_test",
            new Version(1, 0, 0, 0),
            temp.Combine("app"),
            executable);
        var parentCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var parentSqliteHome = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
        var parentOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var parentCodexApiKey = Environment.GetEnvironmentVariable("CODEX_API_KEY");
        var parentCodexAccessToken = Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN");
        var parentProfileApiKey = Environment.GetEnvironmentVariable(ProfileAiLaunchConfiguration.ApiKeyEnvironmentVariable);
        var parentUnrelatedValue = Environment.GetEnvironmentVariable("CODEX_PROFILE_LAUNCHER_TEST_SHARED_ENV");

        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "parent-openai-key");
            Environment.SetEnvironmentVariable("CODEX_API_KEY", "parent-codex-key");
            Environment.SetEnvironmentVariable("CODEX_ACCESS_TOKEN", "parent-codex-access-token");
            Environment.SetEnvironmentVariable(ProfileAiLaunchConfiguration.ApiKeyEnvironmentVariable, "parent-profile-key");
            Environment.SetEnvironmentVariable("CODEX_PROFILE_LAUNCHER_TEST_SHARED_ENV", "preserved");

            var startInfo = CodexProcessLauncher.BuildStartInfo(
                profile,
                installation,
                ProfileAiLaunchConfiguration.Disabled);

            var paths = ProfilePaths.FromRoot(profile.DataRoot);
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.AreEqual(executable, startInfo.FileName);
            Assert.AreEqual(profile.WorkingDirectory, startInfo.WorkingDirectory);
            CollectionAssert.AreEqual(
                new[] { $"--user-data-dir={paths.AppData}", "--new-window" },
                startInfo.ArgumentList.ToArray());
            Assert.AreEqual(paths.CodexHome, startInfo.Environment["CODEX_HOME"]);
            Assert.AreEqual(paths.CodexHome, startInfo.Environment["CODEX_SQLITE_HOME"]);
            Assert.AreEqual(paths.AppData, startInfo.Environment["CODEX_ELECTRON_USER_DATA_PATH"]);
            Assert.IsFalse(startInfo.Environment.ContainsKey("OPENAI_API_KEY"));
            Assert.IsFalse(startInfo.Environment.ContainsKey("CODEX_API_KEY"));
            Assert.IsFalse(startInfo.Environment.ContainsKey("CODEX_ACCESS_TOKEN"));
            Assert.IsFalse(startInfo.Environment.ContainsKey(ProfileAiLaunchConfiguration.ApiKeyEnvironmentVariable));
            Assert.AreEqual("preserved", startInfo.Environment["CODEX_PROFILE_LAUNCHER_TEST_SHARED_ENV"]);
            Assert.AreEqual(parentCodexHome, Environment.GetEnvironmentVariable("CODEX_HOME"));
            Assert.AreEqual(parentSqliteHome, Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", parentOpenAiApiKey);
            Environment.SetEnvironmentVariable("CODEX_API_KEY", parentCodexApiKey);
            Environment.SetEnvironmentVariable("CODEX_ACCESS_TOKEN", parentCodexAccessToken);
            Environment.SetEnvironmentVariable(ProfileAiLaunchConfiguration.ApiKeyEnvironmentVariable, parentProfileApiKey);
            Environment.SetEnvironmentVariable("CODEX_PROFILE_LAUNCHER_TEST_SHARED_ENV", parentUnrelatedValue);
        }
    }

    [TestMethod]
    public void BuildStartInfo_InjectsOnlyExplicitProfileKey()
    {
        using var temp = new TemporaryDirectory();
        var profile = new CodexProfile { Name = "A", DataRoot = temp.Combine("profile") };
        var installation = new CodexInstallation(
            "package",
            "family",
            new Version(1, 0),
            temp.Combine("app"),
            temp.Combine("app", "ChatGPT.exe"));

        var startInfo = CodexProcessLauncher.BuildStartInfo(
            profile,
            installation,
            new ProfileAiLaunchConfiguration(true, "plain-test-key"));

        Assert.AreEqual(
            "plain-test-key",
            startInfo.Environment[ProfileAiLaunchConfiguration.ApiKeyEnvironmentVariable]);
        Assert.IsFalse(startInfo.Environment.ContainsKey("OPENAI_API_KEY"));
        Assert.IsFalse(startInfo.Environment.ContainsKey("CODEX_API_KEY"));
        Assert.IsFalse(startInfo.ArgumentList.Any(argument => argument.Contains("plain-test-key", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ReceiptVerifier_UsesPidStartTimeAndExecutableTogether()
    {
        using var current = Process.GetCurrentProcess();
        var path = current.MainModule!.FileName;
        var receipt = new RunningInstanceReceipt
        {
            RootProcessId = current.Id,
            ProcessStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = path,
        };

        var verified = ProcessReceiptVerifier.Check(receipt);
        using var verifiedProcess = verified.Process;
        Assert.AreEqual(ProcessReceiptStatus.VerifiedRunning, verified.Status);

        receipt.ProcessStartUtcTicks -= 1;
        var reused = ProcessReceiptVerifier.Check(receipt);
        reused.Process?.Dispose();
        Assert.AreEqual(ProcessReceiptStatus.Stopped, reused.Status);

        receipt.ProcessStartUtcTicks = current.StartTime.ToUniversalTime().Ticks;
        receipt.ExecutablePath = Path.Combine(Path.GetDirectoryName(path)!, "different.exe");
        var differentPath = ProcessReceiptVerifier.Check(receipt);
        differentPath.Process?.Dispose();
        Assert.AreEqual(ProcessReceiptStatus.Stopped, differentPath.Status);
        Assert.IsFalse(current.HasExited);
    }

}
