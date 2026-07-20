using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Validation;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class ProfilePathPolicyDirectoryEntryTests
{
    [TestMethod]
    public async Task PrepareAsync_AllowsSameOwnedDirectoryWithExistingData()
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile(temp.Combine("owned"));
        var first = await ProfilePathPolicy.PrepareAsync(profile, []);
        Assert.IsTrue(first.IsValid);
        var sentinel = Path.Combine(profile.DataRoot, "codex-home", "existing.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");

        var second = await ProfilePathPolicy.PrepareAsync(profile, []);

        Assert.IsTrue(second.IsValid, string.Join("; ", second.Issues.Select(issue => issue.Details)));
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(sentinel));
    }

    [TestMethod]
    public async Task PrepareAsync_ReportsHiddenSystemBlockingEntryAndAttributes()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.Combine("apparently-empty");
        Directory.CreateDirectory(root);
        var sentinel = Path.Combine(root, "desktop.ini");
        await File.WriteAllTextAsync(sentinel, "metadata");
        var originalAttributes = File.GetAttributes(sentinel);
        File.SetAttributes(
            sentinel,
            originalAttributes | FileAttributes.Hidden | FileAttributes.System);
        try
        {
            var result = await ProfilePathPolicy.PrepareAsync(NewProfile(root), []);

            var issue = result.Issues.Single(item => item.Code == "PROFILE_DIRECTORY_NOT_EMPTY");
            StringAssert.Contains(issue.Details, "desktop.ini");
            StringAssert.Contains(issue.Details, "Hidden");
            StringAssert.Contains(issue.Details, "System");
            Assert.AreEqual("metadata", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            File.SetAttributes(sentinel, originalAttributes);
        }
    }

    [TestMethod]
    public async Task PrepareAsync_ReportsEmptySubdirectoryAsBlockingDirectory()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.Combine("apparently-empty");
        Directory.CreateDirectory(Path.Combine(root, "empty-child"));

        var result = await ProfilePathPolicy.PrepareAsync(NewProfile(root), []);

        var issue = result.Issues.Single(item => item.Code == "PROFILE_DIRECTORY_NOT_EMPTY");
        StringAssert.Contains(issue.Details, "empty-child");
        StringAssert.Contains(issue.Details, "目录");
        Assert.IsTrue(Directory.Exists(Path.Combine(root, "empty-child")));
    }

    private static CodexProfile NewProfile(string root) => new()
    {
        Id = Guid.NewGuid(),
        Name = "测试环境",
        DataRoot = root,
        WorkingDirectory = root,
    };
}
