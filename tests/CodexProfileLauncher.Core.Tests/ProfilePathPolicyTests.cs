using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Validation;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class ProfilePathPolicyTests
{
    [TestMethod]
    public void Validate_BlocksOverlappingProfileRoots()
    {
        using var temp = new TemporaryDirectory();
        var existing = NewProfile("现有", temp.Combine("existing"));
        var candidate = NewProfile("新环境", System.IO.Path.Combine(existing.DataRoot, "nested"));

        var result = ProfilePathPolicy.Validate(candidate, [existing]);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "PROFILE_PATH_CONFLICT"));
    }

    [TestMethod]
    public void Validate_BlocksCurrentCodexHome()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        var candidate = NewProfile("危险环境", System.IO.Path.Combine(codexHome, "nested"));

        var result = ProfilePathPolicy.Validate(candidate, []);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "PROFILE_SYSTEM_PATH_BLOCKED"));
    }

    [TestMethod]
    public async Task PrepareAsync_CreatesFixedSubdirectoriesAndMarker()
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile("工作 环境", temp.Combine("中文 数据"));

        var result = await ProfilePathPolicy.PrepareAsync(profile, []);

        Assert.IsTrue(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Details)));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        Assert.IsTrue(Directory.Exists(paths.CodexHome));
        Assert.IsTrue(Directory.Exists(paths.AppData));
        Assert.IsTrue(File.Exists(paths.MarkerFile));
    }

    [TestMethod]
    public async Task PrepareAsync_RejectsNonEmptyUnownedDirectory()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.Combine("existing");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(System.IO.Path.Combine(root, "do-not-touch.txt"), "user-data");
        var profile = NewProfile("冲突", root);

        var result = await ProfilePathPolicy.PrepareAsync(profile, []);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "PROFILE_DIRECTORY_NOT_EMPTY"));
        Assert.AreEqual("user-data", await File.ReadAllTextAsync(System.IO.Path.Combine(root, "do-not-touch.txt")));
    }

    [TestMethod]
    public async Task PrepareAsync_RejectsMarkerOwnedByAnotherProfile()
    {
        using var temp = new TemporaryDirectory();
        var first = NewProfile("一", temp.Combine("root"));
        var firstResult = await ProfilePathPolicy.PrepareAsync(first, []);
        Assert.IsTrue(firstResult.IsValid);
        var second = NewProfile("二", first.DataRoot);

        var secondResult = await ProfilePathPolicy.PrepareAsync(second, []);

        Assert.IsFalse(secondResult.IsValid);
        Assert.IsTrue(secondResult.Issues.Any(issue => issue.Code == "PROFILE_MARKER_MISMATCH"));
    }

    [TestMethod]
    public void Validate_RejectsRelativeDataRoot()
    {
        var candidate = NewProfile("相对路径", "relative-profile");

        var result = ProfilePathPolicy.Validate(candidate, []);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "PROFILE_PATH_INVALID"));
    }

    [TestMethod]
    public void Validate_RejectsDriveRootExplicitly()
    {
        var driveRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!;
        var candidate = NewProfile("磁盘根", driveRoot);

        var result = ProfilePathPolicy.Validate(candidate, []);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "PROFILE_DRIVE_ROOT_BLOCKED"));
    }

    [TestMethod]
    [DataRow("codex-home")]
    [DataRow("app-data")]
    public async Task PrepareAsync_RejectsProfileChildReparsePointOnEveryRun(string childName)
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile("链接环境", temp.Combine("profile"));
        var firstResult = await ProfilePathPolicy.PrepareAsync(profile, []);
        Assert.IsTrue(firstResult.IsValid);

        var childPath = System.IO.Path.Combine(profile.DataRoot, childName);
        Directory.Delete(childPath, recursive: true);
        TemporaryDirectory.CreateDirectoryLink(childPath, temp.Combine("outside", childName));

        var secondResult = await ProfilePathPolicy.PrepareAsync(profile, []);

        Assert.IsFalse(secondResult.IsValid);
        Assert.IsTrue(secondResult.Issues.Any(issue => issue.Code == "PROFILE_REPARSE_POINT_UNSUPPORTED"));
    }

    [TestMethod]
    public async Task PrepareAsync_RejectsReparsePointWorkingDirectory()
    {
        using var temp = new TemporaryDirectory();
        var workingLink = temp.Combine("linked-workspace");
        TemporaryDirectory.CreateDirectoryLink(workingLink, temp.Combine("real-workspace"));
        var profile = NewProfile("工作目录链接", temp.Combine("profile"));
        profile.WorkingDirectory = workingLink;

        var result = await ProfilePathPolicy.PrepareAsync(profile, []);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "PROFILE_REPARSE_POINT_UNSUPPORTED"));
    }

    private static CodexProfile NewProfile(string name, string root) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DataRoot = root,
        WorkingDirectory = root,
    };
}
