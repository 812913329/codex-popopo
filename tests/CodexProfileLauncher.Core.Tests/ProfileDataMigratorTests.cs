using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class ProfileDataMigratorTests
{
    [TestMethod]
    public async Task MigrateDataRoot_MovesTreeAndRewritesAbsolutePaths()
    {
        using var temp = new TemporaryDirectory();
        var source = temp.Combine("old-root");
        var dest = temp.Combine("new-root");
        Directory.CreateDirectory(Path.Combine(source, "codex-home"));
        Directory.CreateDirectory(Path.Combine(source, ".launcher"));
        Directory.CreateDirectory(Path.Combine(source, "app-data"));

        var oldManaged = $"""
            sqlite_home = "{source.Replace('\\', '/')}/codex-home"
            log_dir = "{source.Replace('\\', '/')}/codex-home/log"
            model_catalog_json = "{source.Replace('\\', '/')}/.launcher/model-catalog.json"
            """;
        await File.WriteAllTextAsync(Path.Combine(source, "codex-home", "managed_config.toml"), oldManaged);
        await File.WriteAllTextAsync(Path.Combine(source, "codex-home", "config.toml"), "desktop.runCodexInWindowsSubsystemForLinux = false\n");
        await File.WriteAllTextAsync(Path.Combine(source, ".launcher", "ai-settings.toml"), "schema_version = 1\n");
        await File.WriteAllTextAsync(
            Path.Combine(source, ".codex-profile.json"),
            """{"schemaVersion":1,"profileId":"11111111-1111-1111-1111-111111111111"}""");

        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await ProfileDataMigrator.MigrateDataRootAsync(id, source, dest);

        Assert.IsFalse(Directory.Exists(source));
        Assert.IsTrue(Directory.Exists(dest));
        Assert.IsTrue(File.Exists(Path.Combine(dest, "codex-home", "managed_config.toml")));

        var managed = await File.ReadAllTextAsync(Path.Combine(dest, "codex-home", "managed_config.toml"));
        StringAssert.Contains(managed.Replace('\\', '/'), dest.Replace('\\', '/'));
        Assert.IsFalse(managed.Contains(source, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task MigrateDataRoot_RejectsNonEmptyDestination()
    {
        using var temp = new TemporaryDirectory();
        var source = temp.Combine("src");
        var dest = temp.Combine("dst");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(dest, "b.txt"), "y");

        try
        {
            await ProfileDataMigrator.MigrateDataRootAsync(Guid.NewGuid(), source, dest);
            Assert.Fail("expected ProfileDataMigrationException");
        }
        catch (ProfileDataMigrationException)
        {
            // expected
        }
    }
}
