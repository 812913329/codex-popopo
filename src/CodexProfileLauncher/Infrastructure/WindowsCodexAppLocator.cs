using System.Xml.Linq;
using CodexProfileLauncher.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace CodexProfileLauncher.Infrastructure;

public sealed class WindowsCodexAppLocator
{
    public const string PackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";

    public Task<CodexInstallation> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var manager = new PackageManager();
            var candidates = manager
                .FindPackagesForUser(string.Empty, PackageFamilyName)
                .Where(IsUsablePackage)
                .OrderByDescending(package => ToVersion(package.Id.Version))
                .ToList();

            foreach (var package in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installRoot = package.InstalledLocation.Path;
                var executable = ResolveExecutableFromManifest(installRoot);
                if (executable is null)
                {
                    continue;
                }

                return Task.FromResult(new CodexInstallation(
                    package.Id.FullName,
                    package.Id.FamilyName,
                    ToVersion(package.Id.Version),
                    installRoot,
                    executable));
            }
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or System.Runtime.InteropServices.COMException)
        {
            throw new CodexAppLocatorException(
                "APP_QUERY_UNAVAILABLE",
                "无法查询已安装的 Codex。",
                ex.Message,
                ex);
        }

        throw new CodexAppLocatorException(
            "APP_NOT_FOUND",
            "未找到可用的 Codex 应用。",
            "请先从 Microsoft Store 安装 Codex，然后点击重新检测。");
    }

    private static bool IsUsablePackage(Package package)
    {
        try
        {
            return !package.IsFramework &&
                   !package.IsResourcePackage &&
                   package.Status.VerifyIsOK() &&
                   package.Id.Name.Equals("OpenAI.Codex", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveExecutableFromManifest(string installRoot)
    {
        var manifestPath = Path.Combine(installRoot, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var document = XDocument.Load(manifestPath, LoadOptions.None);
        var applications = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("Application", StringComparison.Ordinal))
            .ToList();

        var application = applications.FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("Id"), "App", StringComparison.OrdinalIgnoreCase))
            ?? applications.FirstOrDefault(element =>
                ((string?)element.Attribute("EntryPoint"))?.Contains(
                    "Windows.FullTrustApplication",
                    StringComparison.OrdinalIgnoreCase) == true);

        var relativeExecutable = (string?)application?.Attribute("Executable");
        if (string.IsNullOrWhiteSpace(relativeExecutable))
        {
            return null;
        }

        var candidate = Path.GetFullPath(
            Path.Combine(installRoot, relativeExecutable.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathUtilities.IsSameOrNested(candidate, installRoot) || !File.Exists(candidate))
        {
            return null;
        }

        return candidate;
    }

    private static Version ToVersion(PackageVersion version) =>
        new(version.Major, version.Minor, version.Build, version.Revision);
}

public sealed class CodexAppLocatorException : Exception
{
    public CodexAppLocatorException(
        string code,
        string message,
        string details,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public string Details { get; }
}
