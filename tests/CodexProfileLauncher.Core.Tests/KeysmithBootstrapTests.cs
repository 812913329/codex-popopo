using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class KeysmithBootstrapTests
{
    [TestMethod]
    public void BundledPrompt_IsNonEmptyAndStableSha()
    {
        var prompt = KeysmithBootstrap.GetBundledPrompt();
        Assert.IsFalse(string.IsNullOrWhiteSpace(prompt));
        StringAssert.Contains(prompt, "Local fixture");
        // Source SHA is for LF-normalized content from upstream examples.
        var sha = KeysmithBootstrap.ComputeSha256(prompt.TrimEnd() + "\n");
        Assert.AreEqual(KeysmithBootstrap.ExpectedPromptSha256, sha);
    }

    [TestMethod]
    public async Task ApplyAsync_WritesInstructionsAndIsolatesHooks()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        Directory.CreateDirectory(paths.CodexHome);
        await File.WriteAllTextAsync(Path.Combine(paths.CodexHome, "hooks.json"), "{}\n");

        var settings = new ProfileAiSettings { KeysmithModeEnabled = true };
        var applied = await KeysmithBootstrap.ApplyAsync(paths, settings);

        Assert.IsTrue(applied.SystemPromptEnabled);
        Assert.IsTrue(File.Exists(paths.SystemPromptFile));
        Assert.IsTrue(File.Exists(paths.KeysmithInstructionFile));
        Assert.IsFalse(File.Exists(Path.Combine(paths.CodexHome, "hooks.json")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.CodexHome, "hooks.json.disabled")));

        var managed = await File.ReadAllTextAsync(paths.ManagedConfigFile);
        StringAssert.Contains(managed, "model_instructions_file");
        // Instruction path must resolve under CODEX_HOME, not profile .launcher.
        StringAssert.Contains(managed.Replace('\\', '/'), "/codex-home/gpt-unrestricted.md");
        Assert.IsFalse(managed.Contains(".launcher/system-prompt.md", StringComparison.OrdinalIgnoreCase));

        var config = await File.ReadAllTextAsync(paths.ConfigFile);
        StringAssert.Contains(config, "model_instructions_file = \"./gpt-unrestricted.md\"");
    }
}
