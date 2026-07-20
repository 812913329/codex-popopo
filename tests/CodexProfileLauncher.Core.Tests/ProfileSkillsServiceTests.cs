using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class ProfileSkillsServiceTests
{
    [TestMethod]
    public void ParseFrontmatter_ReadsNameAndDescription()
    {
        const string md = """
            ---
            name: demo-skill
            description: Does demo things
            ---

            # Body
            """;

        var fm = ProfileSkillsService.ParseFrontmatter(md, "fallback");
        Assert.AreEqual("demo-skill", fm.Name);
        Assert.AreEqual("Does demo things", fm.Description);
    }

    [TestMethod]
    public async Task EnableDisable_IgnoresSystemAndPreservesDisabledCopy()
    {
        using var temp = new TemporaryDirectory();
        var appBase = temp.Combine("app");
        var builtinRoot = Path.Combine(appBase, "skills", "builtin", "demo");
        Directory.CreateDirectory(builtinRoot);
        await File.WriteAllTextAsync(
            Path.Combine(builtinRoot, "SKILL.md"),
            """
            ---
            name: demo
            description: demo skill
            ---
            original
            """);

        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        Directory.CreateDirectory(paths.CodexHome);
        var systemDir = Path.Combine(paths.SkillsDirectory, ".system", "plan");
        Directory.CreateDirectory(systemDir);
        await File.WriteAllTextAsync(Path.Combine(systemDir, "SKILL.md"), "system");

        var service = new ProfileSkillsService();
        await service.SetEnabledAsync(paths, "demo", enabled: true, appBaseDirectory: appBase);

        Assert.IsTrue(File.Exists(Path.Combine(paths.SkillsDirectory, "demo", "SKILL.md")));
        Assert.IsTrue(File.Exists(Path.Combine(systemDir, "SKILL.md")));

        var list = service.List(paths, appBase);
        Assert.AreEqual(1, list.EnabledCount);
        Assert.IsTrue(list.Skills.Any(s => s.Id == "demo" && s.IsEnabled));

        await service.SaveSkillMarkdownAsync(
            paths,
            "demo",
            """
            ---
            name: demo
            description: demo skill
            ---
            customized
            """);

        await service.SetEnabledAsync(paths, "demo", enabled: false, appBaseDirectory: appBase);
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.SkillsDirectory, "demo")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.SkillsDisabledDirectory, "demo", "SKILL.md")));
        var disabledText = await File.ReadAllTextAsync(Path.Combine(paths.SkillsDisabledDirectory, "demo", "SKILL.md"));
        StringAssert.Contains(disabledText, "customized");

        await service.SetEnabledAsync(paths, "demo", enabled: true, appBaseDirectory: appBase);
        var restored = await File.ReadAllTextAsync(Path.Combine(paths.SkillsDirectory, "demo", "SKILL.md"));
        StringAssert.Contains(restored, "customized");

        await service.ResetToBuiltinAsync(paths, "demo", appBase);
        var reset = await File.ReadAllTextAsync(Path.Combine(paths.SkillsDirectory, "demo", "SKILL.md"));
        StringAssert.Contains(reset, "original");

        // .system untouched
        Assert.IsTrue(File.Exists(Path.Combine(systemDir, "SKILL.md")));
    }

    [TestMethod]
    public async Task InstallAllBuiltin_SkipsExisting()
    {
        using var temp = new TemporaryDirectory();
        var appBase = temp.Combine("app");
        foreach (var id in new[] { "a", "b" })
        {
            var dir = Path.Combine(appBase, "skills", "builtin", id);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "SKILL.md"),
                $"---\nname: {id}\ndescription: d\n---\n");
        }

        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        Directory.CreateDirectory(paths.CodexHome);
        Directory.CreateDirectory(Path.Combine(paths.SkillsDirectory, "a"));
        await File.WriteAllTextAsync(Path.Combine(paths.SkillsDirectory, "a", "SKILL.md"), "keep-me");

        var service = new ProfileSkillsService();
        await service.InstallAllBuiltinAsync(paths, appBase);

        Assert.AreEqual("keep-me", await File.ReadAllTextAsync(Path.Combine(paths.SkillsDirectory, "a", "SKILL.md")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.SkillsDirectory, "b", "SKILL.md")));
    }

    [TestMethod]
    public async Task ImportFromFolder_RequiresSkillMd()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        Directory.CreateDirectory(paths.CodexHome);
        var bad = temp.Combine("empty");
        Directory.CreateDirectory(bad);

        var service = new ProfileSkillsService();
        try
        {
            await service.ImportFromFolderAsync(paths, bad);
            Assert.Fail("expected ProfileSkillsException");
        }
        catch (ProfileSkillsException)
        {
            // expected
        }

        var good = temp.Combine("my-skill");
        Directory.CreateDirectory(good);
        await File.WriteAllTextAsync(Path.Combine(good, "SKILL.md"), "---\nname: my-skill\ndescription: x\n---\n");
        await service.ImportFromFolderAsync(paths, good);
        Assert.IsTrue(File.Exists(Path.Combine(paths.SkillsDirectory, "my-skill", "SKILL.md")));
    }
}
