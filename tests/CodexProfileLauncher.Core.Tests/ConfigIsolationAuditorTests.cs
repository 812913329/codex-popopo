using CodexProfileLauncher.Core.Configuration;
using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class ConfigIsolationAuditorTests
{
    [TestMethod]
    public void DefaultConfig_PassesStrictIsolationAudit()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));

        var report = ConfigIsolationAuditor.AuditText(
            ConfigIsolationAuditor.CreateDefaultConfig(),
            paths,
            paths.DataRoot);

        Assert.IsTrue(report.IsIsolated, Format(report));
        StringAssert.Contains(
            ConfigIsolationAuditor.CreateDefaultConfig(),
            "desktop.runCodexInWindowsSubsystemForLinux = false");
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow("\"false\"")]
    [DataRow("1")]
    public void WslBackendEnabledOrInvalid_IsBlocked(string value)
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var text = CredentialsOnlyConfig() +
            $"desktop.runCodexInWindowsSubsystemForLinux = {value}{Environment.NewLine}";

        var report = ConfigIsolationAuditor.AuditText(text, paths, paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "PROFILE_WSL_BACKEND_UNSUPPORTED"));
    }

    [TestMethod]
    public void ExplicitNativeWindowsBackend_IsAllowed()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var text = SafeConfig();

        var report = ConfigIsolationAuditor.AuditText(text, paths, paths.DataRoot);

        Assert.IsTrue(report.IsIsolated, Format(report));
    }

    [TestMethod]
    public void MissingExplicitNativeWindowsBackend_IsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));

        var report = ConfigIsolationAuditor.AuditText(
            "cli_auth_credentials_store = \"file\"\nmcp_oauth_credentials_store = \"file\"\n",
            paths,
            paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "PROFILE_WSL_BACKEND_UNSUPPORTED"));
    }

    [TestMethod]
    public async Task EnsureInitialized_CreatesAuditableManagedIsolationLayer()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));

        await ConfigIsolationAuditor.EnsureInitializedAsync(paths);
        var report = await ConfigIsolationAuditor.AuditManagedConfigAsync(paths);

        Assert.IsTrue(File.Exists(paths.ManagedConfigFile));
        Assert.IsTrue(report.IsIsolated, Format(report));
        var managedText = await File.ReadAllTextAsync(paths.ManagedConfigFile);
        StringAssert.Contains(managedText, $"sqlite_home = \"{paths.CodexHome.Replace("\\", "/")}\"");
        StringAssert.Contains(managedText, $"log_dir = \"{paths.LogDirectory.Replace("\\", "/")}\"");
    }

    [TestMethod]
    public async Task ManagedIsolationLayerPointingOutsideProfile_IsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        await ConfigIsolationAuditor.EnsureInitializedAsync(paths);
        await File.WriteAllTextAsync(
            paths.ManagedConfigFile,
            SafeConfig() +
            $"sqlite_home = \"{temp.Combine("shared").Replace("\\", "/")}\"\n" +
            $"log_dir = \"{paths.LogDirectory.Replace("\\", "/")}\"\n");

        var report = await ConfigIsolationAuditor.AuditManagedConfigAsync(paths);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "PROFILE_MANAGED_CONFIG_NOT_ISOLATED"));
    }

    [TestMethod]
    public async Task ManagedIsolationLayerThroughReparsePoint_IsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        Directory.CreateDirectory(paths.CodexHome);
        TemporaryDirectory.CreateDirectoryLink(paths.LogDirectory, temp.Combine("shared-log"));
        await File.WriteAllTextAsync(paths.ManagedConfigFile, ConfigIsolationAuditor.CreateManagedConfig(paths));

        var report = await ConfigIsolationAuditor.AuditManagedConfigAsync(paths);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue =>
            issue.Code == "PROFILE_MANAGED_CONFIG_NOT_ISOLATED" &&
            issue.Details.Contains("reparse point/junction", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("cli_auth_credentials_store = \"auto\"")]
    [DataRow("cli_auth_credentials_store = \"keyring\"")]
    [DataRow("cli_auth_credentials_store = \"FILE\"")]
    public void NonFileCliCredentialStore_IsBlocked(string cliSetting)
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var text = $"{cliSetting}{Environment.NewLine}mcp_oauth_credentials_store = \"file\"";

        var report = ConfigIsolationAuditor.AuditText(text, paths, paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "PROFILE_AUTH_NOT_ISOLATED"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("mcp_oauth_credentials_store = \"auto\"")]
    [DataRow("mcp_oauth_credentials_store = \"keyring\"")]
    [DataRow("mcp_oauth_credentials_store = \"File\"")]
    public void NonFileMcpCredentialStore_IsBlocked(string mcpSetting)
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var text = $"cli_auth_credentials_store = \"file\"{Environment.NewLine}{mcpSetting}";

        var report = ConfigIsolationAuditor.AuditText(text, paths, paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "PROFILE_AUTH_NOT_ISOLATED"));
    }

    [TestMethod]
    public void SyntaxError_IsReportedAndLaunchMustBeBlocked()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));

        var report = ConfigIsolationAuditor.AuditText(
            "cli_auth_credentials_store = [",
            paths,
            paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.AreEqual("PROFILE_CONFIG_INVALID", report.Issues[0].Code);
    }

    [TestMethod]
    public void SqliteHomeOutsideProfile_IsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var outside = temp.Combine("shared").Replace("\\", "/");
        var text = SafeConfig() + $"sqlite_home = \"{outside}\"{Environment.NewLine}";

        var report = ConfigIsolationAuditor.AuditText(text, paths, paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "PROFILE_STATE_ESCAPES_ROOT"));
    }

    [TestMethod]
    [DataRow("sqlite_home")]
    [DataRow("log_dir")]
    public void RelativeStateDirectory_IsBlocked(string key)
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var text = SafeConfig() + $"{key} = \"state\"{Environment.NewLine}";

        var report = ConfigIsolationAuditor.AuditText(text, paths, paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "PROFILE_STATE_PATH_INVALID"));
    }

    [TestMethod]
    public void LogDirectoryOutsideProfile_IsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var outside = temp.Combine("logs").Replace("\\", "/");
        var text = SafeConfig() + $"log_dir = \"{outside}\"{Environment.NewLine}";

        var report = ConfigIsolationAuditor.AuditText(text, paths, paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "PROFILE_STATE_ESCAPES_ROOT"));
    }

    [TestMethod]
    [DataRow("sqlite_home")]
    [DataRow("log_dir")]
    public void StateDirectoryReparsePoint_IsBlocked(string key)
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        Directory.CreateDirectory(paths.DataRoot);
        TemporaryDirectory.CreateDirectoryLink(
            System.IO.Path.Combine(paths.DataRoot, "state-link"),
            temp.Combine("shared-state"));
        var linkedPath = System.IO.Path.Combine(paths.DataRoot, "state-link").Replace("\\", "/");
        var text = SafeConfig() + $"{key} = \"{linkedPath}\"{Environment.NewLine}";

        var report = ConfigIsolationAuditor.AuditText(text, paths, paths.DataRoot);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue =>
            issue.Code == "PROFILE_STATE_REPARSE_POINT_UNSUPPORTED"));
    }

    [TestMethod]
    public async Task SaveValidatedAsync_WritesAtomicallyAndKeepsBackup()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        await ConfigIsolationAuditor.EnsureInitializedAsync(paths);
        var first = SafeConfig() + "model = \"first\"\n";
        var second = SafeConfig() + "model = \"second\"\n";

        await ConfigIsolationAuditor.SaveValidatedAsync(first, paths, paths.DataRoot);
        await ConfigIsolationAuditor.SaveValidatedAsync(second, paths, paths.DataRoot);

        Assert.AreEqual(second, await File.ReadAllTextAsync(paths.ConfigFile));
        Assert.AreEqual(first, await File.ReadAllTextAsync(paths.ConfigFile + ".bak"));
    }

    [TestMethod]
    [DataRow("cli_auth_credentials_store")]
    [DataRow("mcp_oauth_credentials_store")]
    [DataRow("sqlite_home")]
    [DataRow("log_dir")]
    public async Task ProjectLayerIsolationOverride_IsBlocked(string key)
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = temp.Combine("project");
        var workingDirectory = System.IO.Path.Combine(projectRoot, "src", "feature");
        var dotCodex = System.IO.Path.Combine(projectRoot, ".codex");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(dotCodex);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(dotCodex, "config.toml"),
            $"{key} = \"override\"{Environment.NewLine}");

        var report = await ConfigIsolationAuditor.AuditProjectConfigLayersAsync(workingDirectory);

        Assert.IsFalse(report.IsIsolated);
        Assert.IsTrue(report.Issues.Any(issue =>
            issue.Code == "PROJECT_CONFIG_OVERRIDES_ISOLATION" &&
            issue.Details.Contains(key, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WorkingDirectoryConfigIsolationOverride_IsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var workingDirectory = temp.Combine("project");
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(workingDirectory, "config.toml"),
            "log_dir = \"C:/shared/logs\"\n");

        var report = await ConfigIsolationAuditor.AuditProjectConfigLayersAsync(workingDirectory);

        Assert.IsFalse(report.IsIsolated);
        Assert.AreEqual("PROJECT_CONFIG_OVERRIDES_ISOLATION", report.Issues.Single().Code);
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow("\"false\"")]
    public async Task ProjectLayerWslBackendEnabledOrInvalid_IsBlocked(string value)
    {
        using var temp = new TemporaryDirectory();
        var workingDirectory = temp.Combine("project");
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(workingDirectory, "config.toml"),
            $"desktop.runCodexInWindowsSubsystemForLinux = {value}\n");

        var report = await ConfigIsolationAuditor.AuditProjectConfigLayersAsync(workingDirectory);

        Assert.IsFalse(report.IsIsolated);
        Assert.AreEqual("PROJECT_CONFIG_ENABLES_WSL_UNSUPPORTED", report.Issues.Single().Code);
    }

    [TestMethod]
    public async Task ProjectLayerExplicitNativeWindowsBackend_IsAllowed()
    {
        using var temp = new TemporaryDirectory();
        var workingDirectory = temp.Combine("project");
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(workingDirectory, "config.toml"),
            "desktop.runCodexInWindowsSubsystemForLinux = false\n");

        var report = await ConfigIsolationAuditor.AuditProjectConfigLayersAsync(workingDirectory);

        Assert.IsTrue(report.IsIsolated, Format(report));
    }

    [TestMethod]
    public async Task SafeProjectLayer_IsAllowed()
    {
        using var temp = new TemporaryDirectory();
        var workingDirectory = temp.Combine("project");
        var dotCodex = System.IO.Path.Combine(workingDirectory, ".codex");
        Directory.CreateDirectory(dotCodex);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(dotCodex, "config.toml"),
            "model = \"gpt-5\"\n");

        var report = await ConfigIsolationAuditor.AuditProjectConfigLayersAsync(workingDirectory);

        Assert.IsTrue(report.IsIsolated, Format(report));
    }

    [TestMethod]
    public async Task MalformedProjectLayer_IsBlockedFailClosed()
    {
        using var temp = new TemporaryDirectory();
        var workingDirectory = temp.Combine("project");
        var dotCodex = System.IO.Path.Combine(workingDirectory, ".codex");
        Directory.CreateDirectory(dotCodex);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(dotCodex, "config.toml"),
            "sqlite_home = [\n");

        var report = await ConfigIsolationAuditor.AuditProjectConfigLayersAsync(workingDirectory);

        Assert.IsFalse(report.IsIsolated);
        Assert.AreEqual("PROJECT_CONFIG_UNVERIFIABLE", report.Issues.Single().Code);
    }

    private static string SafeConfig() =>
        CredentialsOnlyConfig() +
        "desktop.runCodexInWindowsSubsystemForLinux = false\n";

    private static string CredentialsOnlyConfig() =>
        "cli_auth_credentials_store = \"file\"\n" +
        "mcp_oauth_credentials_store = \"file\"\n";

    private static string Format(IsolationReport report) =>
        string.Join("; ", report.Issues.Select(issue => $"{issue.Code}: {issue.Details}"));
}
