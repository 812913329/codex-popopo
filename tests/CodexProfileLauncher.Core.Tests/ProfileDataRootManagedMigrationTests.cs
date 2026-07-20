using CodexProfileLauncher.Core.Services;
using CodexProfileLauncher.Core.Validation;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class ProfileDataRootManagedMigrationTests
{
    [TestMethod]
    public async Task MigrateDataRoot_ResolvedNonEmptyParentPreservesParentAndSiblings()
    {
        using var temp = new TemporaryDirectory();
        var profileId = Guid.NewGuid();
        var source = temp.Combine("source");
        var destinationParent = temp.Combine("destination-parent");
        Directory.CreateDirectory(Path.Combine(source, "codex-home"));
        Directory.CreateDirectory(destinationParent);
        await File.WriteAllTextAsync(Path.Combine(source, "codex-home", "config.toml"), "model = \"test\"\n");
        var sibling = Path.Combine(destinationParent, "keep.txt");
        await File.WriteAllTextAsync(sibling, "keep");

        var resolved = ProfileDataRootSelectionResolver.Resolve(destinationParent, profileId);
        Assert.IsTrue(resolved.UsesManagedChild);

        await ProfileDataMigrator.MigrateDataRootAsync(profileId, source, resolved.DataRoot);

        Assert.IsFalse(Directory.Exists(source));
        Assert.IsTrue(Directory.Exists(resolved.DataRoot));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(sibling));
        Assert.IsTrue(File.Exists(Path.Combine(resolved.DataRoot, "codex-home", "config.toml")));
    }
}
