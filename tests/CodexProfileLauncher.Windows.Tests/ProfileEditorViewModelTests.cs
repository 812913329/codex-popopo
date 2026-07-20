using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
public sealed class ProfileEditorViewModelTests
{
    [TestMethod]
    public async Task NewProfile_NonEmptySelectionBuildsProfileWithPreviewedManagedChild()
    {
        using var temp = new ProfileEditorTemporaryDirectory();
        var parent = temp.Combine("selected-parent");
        Directory.CreateDirectory(parent);
        var sentinel = Path.Combine(parent, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var id = Guid.NewGuid();
        var editor = NewEditor(existing: null, id, parent, temp.Path);

        Assert.IsTrue(editor.UsesManagedDataRootChild);
        Assert.AreEqual(Path.Combine(parent, $"CodexProfile-{id:N}"), editor.ActualDataRoot);
        Assert.IsTrue(editor.Validate(), editor.ValidationMessage);

        var profile = editor.BuildProfile();
        Assert.AreEqual(editor.ActualDataRoot, profile.DataRoot);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(sentinel));
        Assert.IsFalse(Directory.Exists(profile.DataRoot));
    }

    [TestMethod]
    public async Task EditProfile_SameNonEmptyRootDoesNotBecomeMigration()
    {
        using var temp = new ProfileEditorTemporaryDirectory();
        var root = temp.Combine("existing-root");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "existing.txt"), "data");
        var existing = NewProfile(Guid.NewGuid(), root, temp.Path);

        var editor = NewEditor(existing, Guid.NewGuid(), root, temp.Path);

        Assert.IsFalse(editor.UsesManagedDataRootChild);
        Assert.AreEqual(Path.GetFullPath(root), editor.ActualDataRoot);
        Assert.IsFalse(editor.IsDataRootChanged);
        Assert.AreEqual(Path.GetFullPath(root), editor.BuildProfile().DataRoot);
    }

    [TestMethod]
    public async Task EditProfile_NonEmptyDestinationParentPreviewsManagedMigrationTarget()
    {
        using var temp = new ProfileEditorTemporaryDirectory();
        var source = temp.Combine("source");
        var destinationParent = temp.Combine("destination-parent");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationParent);
        var sentinel = Path.Combine(destinationParent, "sibling.txt");
        await File.WriteAllTextAsync(sentinel, "sibling");
        var existing = NewProfile(Guid.NewGuid(), source, temp.Path);
        var editor = NewEditor(existing, Guid.NewGuid(), source, temp.Path);

        editor.DataRoot = destinationParent;

        var expected = Path.Combine(destinationParent, $"CodexProfile-{existing.Id:N}");
        Assert.IsTrue(editor.UsesManagedDataRootChild);
        Assert.AreEqual(expected, editor.ActualDataRoot);
        Assert.IsTrue(editor.IsDataRootChanged);
        Assert.AreEqual(expected, editor.BuildProfile().DataRoot);
        Assert.AreEqual("sibling", await File.ReadAllTextAsync(sentinel));
    }

    [TestMethod]
    public void EditProfile_SelectingParentOfCurrentGuidRootDoesNotTriggerMigration()
    {
        using var temp = new ProfileEditorTemporaryDirectory();
        var id = Guid.NewGuid();
        var parent = temp.Combine("parent");
        var source = Path.Combine(parent, id.ToString("N"));
        Directory.CreateDirectory(source);
        var existing = NewProfile(id, source, temp.Path);
        var editor = NewEditor(existing, Guid.NewGuid(), source, temp.Path);

        editor.DataRoot = parent;

        Assert.IsTrue(editor.UsesManagedDataRootChild);
        Assert.AreEqual(Path.GetFullPath(source), editor.ActualDataRoot);
        Assert.IsFalse(editor.IsDataRootChanged);
    }

    private static ProfileEditorViewModel NewEditor(
        CodexProfile? existing,
        Guid newProfileId,
        string dataRoot,
        string workingDirectory) => new(
            existing,
            existing is null ? [] : [existing],
            newProfileId,
            "测试环境",
            dataRoot,
            workingDirectory);

    private static CodexProfile NewProfile(Guid id, string dataRoot, string workingDirectory) => new()
    {
        Id = id,
        Name = "现有环境",
        DataRoot = dataRoot,
        WorkingDirectory = workingDirectory,
    };

    private sealed class ProfileEditorTemporaryDirectory : IDisposable
    {
        public ProfileEditorTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexProfileLauncher.ProfileEditor.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(string child) => System.IO.Path.Combine(Path, child);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
