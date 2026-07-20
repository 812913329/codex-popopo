using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class PathUtilitiesTests
{
    [TestMethod]
    public void Normalize_PreservesUnicodeAndRemovesTrailingSeparator()
    {
        using var temp = new TemporaryDirectory();
        var input = System.IO.Path.Combine(temp.Path, "中文 环境") + System.IO.Path.DirectorySeparatorChar;

        var actual = PathUtilities.Normalize(input);

        Assert.AreEqual(System.IO.Path.Combine(temp.Path, "中文 环境"), actual);
    }

    [TestMethod]
    public void Overlaps_DetectsSameAndNestedPathsButNotSiblings()
    {
        using var temp = new TemporaryDirectory();
        var first = temp.Combine("first");
        var child = System.IO.Path.Combine(first, "child");
        var sibling = temp.Combine("second");

        Assert.IsTrue(PathUtilities.Overlaps(first, first));
        Assert.IsTrue(PathUtilities.Overlaps(first, child));
        Assert.IsTrue(PathUtilities.Overlaps(child, first));
        Assert.IsFalse(PathUtilities.Overlaps(first, sibling));
    }

    [TestMethod]
    public void IsSameOrNested_DoesNotTreatPrefixOnlyAsChild()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.Combine("profile");
        var prefixOnly = temp.Combine("profile-copy");

        Assert.IsFalse(PathUtilities.IsSameOrNested(prefixOnly, root));
    }

    [TestMethod]
    public void Normalize_PreservesDriveRootSeparator()
    {
        var root = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!;

        var normalized = PathUtilities.Normalize(root);

        Assert.AreEqual(root, normalized);
    }

    [TestMethod]
    public void Normalize_RejectsRelativePath()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(
            () => PathUtilities.Normalize("relative-profile"));

        StringAssert.Contains(exception.Message, "绝对路径");
    }

    [TestMethod]
    public void IsFileSystemRoot_RecognizesDriveRoot()
    {
        var root = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!;

        Assert.IsTrue(PathUtilities.IsFileSystemRoot(root));
        Assert.IsFalse(PathUtilities.IsFileSystemRoot(System.IO.Path.Combine(root, "profile")));
    }
}
