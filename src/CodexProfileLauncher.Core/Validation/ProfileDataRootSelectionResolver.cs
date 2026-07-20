using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.Core.Validation;

public sealed record ProfileDataRootSelection(
    string SelectedPath,
    string DataRoot,
    bool UsesManagedChild);

/// <summary>
/// Resolves the path entered in the profile editor to the exclusive data root
/// that the launcher may safely own. A non-empty unowned selection is treated
/// as a parent location; its existing contents are never adopted.
/// </summary>
public static class ProfileDataRootSelectionResolver
{
    public static ProfileDataRootSelection Resolve(
        string selectedPath,
        Guid profileId,
        string? originalDataRoot = null)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("环境 ID 不能为空。", nameof(profileId));
        }

        var selected = PathUtilities.Normalize(selectedPath);
        if (!string.IsNullOrWhiteSpace(originalDataRoot) &&
            selected.Equals(PathUtilities.Normalize(originalDataRoot), StringComparison.OrdinalIgnoreCase))
        {
            return new(selected, selected, UsesManagedChild: false);
        }

        // Keep unsupported roots exact so the existing path policy can reject
        // them. Resolving a drive root to C:\<id>, for example, would otherwise
        // bypass the explicit drive-root boundary.
        if (selected.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            PathUtilities.IsFileSystemRoot(selected) ||
            !Directory.Exists(selected))
        {
            return new(selected, selected, UsesManagedChild: false);
        }

        // Preserve both the legacy bare-GUID child name and the readable v1.1.6
        // child name when an existing profile's parent is selected again.
        if (!string.IsNullOrWhiteSpace(originalDataRoot))
        {
            var original = PathUtilities.Normalize(originalDataRoot);
            var originalParent = Path.GetDirectoryName(original);
            var originalName = Path.GetFileName(original);
            var profileKey = profileId.ToString("N");
            if (!string.IsNullOrWhiteSpace(originalParent) &&
                selected.Equals(
                    PathUtilities.Normalize(originalParent),
                    StringComparison.OrdinalIgnoreCase) &&
                (originalName.Equals(profileKey, StringComparison.OrdinalIgnoreCase) ||
                 originalName.Equals(
                     $"CodexProfile-{profileKey}",
                     StringComparison.OrdinalIgnoreCase)))
            {
                return new(selected, original, UsesManagedChild: true);
            }
        }

        // An existing launcher marker is an ownership boundary. Keep the exact
        // path so the existing marker validation can accept the same profile or
        // reject a foreign/corrupt marker; never hide that boundary by nesting.
        var markerFile = ProfilePaths.FromRoot(selected).MarkerFile;
        if (File.Exists(markerFile))
        {
            return new(selected, selected, UsesManagedChild: false);
        }

        using var entries = Directory.EnumerateFileSystemEntries(selected).GetEnumerator();
        if (!entries.MoveNext())
        {
            return new(selected, selected, UsesManagedChild: false);
        }

        var managedChild = PathUtilities.Normalize(
            Path.Combine(selected, $"CodexProfile-{profileId:N}"));
        return new(selected, managedChild, UsesManagedChild: true);
    }
}
