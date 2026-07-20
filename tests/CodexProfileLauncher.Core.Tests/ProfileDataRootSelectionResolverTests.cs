using CodexProfileLauncher.Core.Validation;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class ProfileDataRootSelectionResolverTests
{
    [TestMethod]
    public void Resolve_UsesExistingEmptyDirectoryDirectly()
    {
        using var temp = new TemporaryDirectory();
        var selected = temp.Combine("empty");
        Directory.CreateDirectory(selected);

        var result = ProfileDataRootSelectionResolver.Resolve(selected, Guid.NewGuid());

        Assert.AreEqual(Path.GetFullPath(selected), result.DataRoot);
        Assert.IsFalse(result.UsesManagedChild);
    }

    [TestMethod]
    public async Task Resolve_NonEmptyParentUsesManagedChildWithoutTouchingExistingFile()
    {
        using var temp = new TemporaryDirectory();
        var selected = temp.Combine("parent");
        Directory.CreateDirectory(selected);
        var sentinel = Path.Combine(selected, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep-me");
        var profileId = Guid.NewGuid();

        var result = ProfileDataRootSelectionResolver.Resolve(selected, profileId);

        Assert.IsTrue(result.UsesManagedChild);
        Assert.AreEqual(Path.Combine(selected, $"CodexProfile-{profileId:N}"), result.DataRoot);
        Assert.IsFalse(Directory.Exists(result.DataRoot));
        Assert.AreEqual("keep-me", await File.ReadAllTextAsync(sentinel));
    }

    [TestMethod]
    public async Task Resolve_HiddenSystemEntryUsesManagedChildAndPreservesAttributes()
    {
        using var temp = new TemporaryDirectory();
        var selected = temp.Combine("parent");
        Directory.CreateDirectory(selected);
        var sentinel = Path.Combine(selected, "desktop.ini");
        await File.WriteAllTextAsync(sentinel, "metadata");
        var originalAttributes = File.GetAttributes(sentinel);
        var expectedAttributes = originalAttributes | FileAttributes.Hidden | FileAttributes.System;
        File.SetAttributes(sentinel, expectedAttributes);
        try
        {
            var result = ProfileDataRootSelectionResolver.Resolve(selected, Guid.NewGuid());

            Assert.IsTrue(result.UsesManagedChild);
            Assert.AreEqual(expectedAttributes, File.GetAttributes(sentinel));
            Assert.AreEqual("metadata", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            File.SetAttributes(sentinel, originalAttributes);
        }
    }

    [TestMethod]
    public void Resolve_OnlyEmptySubdirectoryStillUsesManagedChild()
    {
        using var temp = new TemporaryDirectory();
        var selected = temp.Combine("parent");
        var emptyChild = Path.Combine(selected, "keep-empty");
        Directory.CreateDirectory(emptyChild);
        var profileId = Guid.NewGuid();

        var result = ProfileDataRootSelectionResolver.Resolve(selected, profileId);

        Assert.IsTrue(result.UsesManagedChild);
        Assert.AreEqual(Path.Combine(selected, $"CodexProfile-{profileId:N}"), result.DataRoot);
        Assert.IsTrue(Directory.Exists(emptyChild));
    }

    [TestMethod]
    public void Resolve_FileSystemRootStaysExactForPathPolicyRejection()
    {
        var selected = Path.GetPathRoot(Environment.SystemDirectory)!;

        var result = ProfileDataRootSelectionResolver.Resolve(selected, Guid.NewGuid());

        Assert.IsFalse(result.UsesManagedChild);
        Assert.AreEqual(Path.GetFullPath(selected), result.DataRoot);
    }

    [TestMethod]
    public async Task Resolve_LauncherMarkerKeepsExactPathForOwnershipValidation()
    {
        using var temp = new TemporaryDirectory();
        var selected = temp.Combine("owned");
        Directory.CreateDirectory(selected);
        await File.WriteAllTextAsync(Path.Combine(selected, ".codex-profile.json"), "{}");

        var result = ProfileDataRootSelectionResolver.Resolve(selected, Guid.NewGuid());

        Assert.IsFalse(result.UsesManagedChild);
        Assert.AreEqual(Path.GetFullPath(selected), result.DataRoot);
    }

    [TestMethod]
    public async Task Resolve_EditSamePathKeepsExactRootEvenWhenMarkerIsMissing()
    {
        using var temp = new TemporaryDirectory();
        var selected = temp.Combine("existing-profile");
        Directory.CreateDirectory(selected);
        await File.WriteAllTextAsync(Path.Combine(selected, "existing-data.txt"), "data");

        var result = ProfileDataRootSelectionResolver.Resolve(
            selected + Path.DirectorySeparatorChar,
            Guid.NewGuid(),
            selected);

        Assert.IsFalse(result.UsesManagedChild);
        Assert.AreEqual(Path.GetFullPath(selected), result.DataRoot);
    }
}
