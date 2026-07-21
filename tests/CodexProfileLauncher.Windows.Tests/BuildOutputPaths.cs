namespace CodexProfileLauncher.Windows.Tests;

/// <summary>
/// Resolves built EXE/DLL paths for both classic project <c>bin/</c> layout and
/// <c>UseArtifactsOutput</c> layout used by <c>tools/Verify-Release.ps1</c> / CI.
/// </summary>
internal static class BuildOutputPaths
{
    private const string TargetFrameworkFolder = "net10.0-windows10.0.19041.0";
    private const string RuntimeIdentifier = "win-x64";

    public static string GetWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var solution = Path.Combine(current.FullName, "CodexProfileLauncher.slnx");
            var directoryBuild = Path.Combine(current.FullName, "Directory.Build.props");
            if (File.Exists(solution) || File.Exists(directoryBuild))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        // Fallback: classic test output is five levels below the repo root.
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
    }

    public static string RequireBrokerExecutable()
    {
        return RequireExisting(
            "CodexProfileLauncher.exe",
            EnumerateBrokerExecutableCandidates());
    }

    public static string RequireBrokerManagedEntryPoint()
    {
        return RequireExisting(
            "CodexProfileLauncher.dll",
            EnumerateBrokerManagedEntryPointCandidates());
    }

    public static string RequireTestHostExecutable()
    {
        return RequireExisting(
            "CodexProfileLauncher.JobBroker.TestHost.exe",
            EnumerateTestHostExecutableCandidates());
    }

    private static string RequireExisting(string label, IEnumerable<string> candidates)
    {
        var list = candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var candidate in list)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new AssertFailedException(
            $"{label} 不存在。已搜索：{Environment.NewLine}{string.Join(Environment.NewLine, list)}");
    }

    private static IEnumerable<string> EnumerateBrokerExecutableCandidates()
    {
        var root = GetWorkspaceRoot();
        var preferArtifacts = TryGetArtifactsBinRoot(out var artifactsBinRoot);
        foreach (var config in ConfigurationFolderNames())
        {
            var artifactsPaths = new List<string>();
            if (artifactsBinRoot is not null)
            {
                // UseArtifactsOutput: <ArtifactsPath>/bin/<Project>/<config>[_<rid>]/
                artifactsPaths.Add(Path.Combine(
                    artifactsBinRoot,
                    "CodexProfileLauncher",
                    $"{config}_{RuntimeIdentifier}",
                    "CodexProfileLauncher.exe"));
                artifactsPaths.Add(Path.Combine(
                    artifactsBinRoot,
                    "CodexProfileLauncher",
                    config,
                    "CodexProfileLauncher.exe"));
            }

            // Default Verify-Release path even when the current test DLL is elsewhere.
            artifactsPaths.Add(Path.Combine(
                root,
                "artifacts",
                "build",
                "bin",
                "CodexProfileLauncher",
                $"{config}_{RuntimeIdentifier}",
                "CodexProfileLauncher.exe"));
            artifactsPaths.Add(Path.Combine(
                root,
                "artifacts",
                "build",
                "bin",
                "CodexProfileLauncher",
                config,
                "CodexProfileLauncher.exe"));

            var classicPath = Path.Combine(
                root,
                "src",
                "CodexProfileLauncher",
                "bin",
                ToClassicConfigurationName(config),
                TargetFrameworkFolder,
                RuntimeIdentifier,
                "CodexProfileLauncher.exe");

            if (preferArtifacts)
            {
                foreach (var path in artifactsPaths)
                {
                    yield return path;
                }

                yield return classicPath;
            }
            else
            {
                yield return classicPath;
                foreach (var path in artifactsPaths)
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateBrokerManagedEntryPointCandidates()
    {
        foreach (var exe in EnumerateBrokerExecutableCandidates())
        {
            var directory = Path.GetDirectoryName(exe);
            if (directory is null)
            {
                continue;
            }

            yield return Path.Combine(directory, "CodexProfileLauncher.dll");
        }
    }

    private static IEnumerable<string> EnumerateTestHostExecutableCandidates()
    {
        var root = GetWorkspaceRoot();
        var preferArtifacts = TryGetArtifactsBinRoot(out var artifactsBinRoot);
        foreach (var config in ConfigurationFolderNames())
        {
            var artifactsPaths = new List<string>();
            if (artifactsBinRoot is not null)
            {
                artifactsPaths.Add(Path.Combine(
                    artifactsBinRoot,
                    "CodexProfileLauncher.JobBroker.TestHost",
                    $"{config}_{RuntimeIdentifier}",
                    "CodexProfileLauncher.JobBroker.TestHost.exe"));
                artifactsPaths.Add(Path.Combine(
                    artifactsBinRoot,
                    "CodexProfileLauncher.JobBroker.TestHost",
                    config,
                    "CodexProfileLauncher.JobBroker.TestHost.exe"));
            }

            artifactsPaths.Add(Path.Combine(
                root,
                "artifacts",
                "build",
                "bin",
                "CodexProfileLauncher.JobBroker.TestHost",
                $"{config}_{RuntimeIdentifier}",
                "CodexProfileLauncher.JobBroker.TestHost.exe"));
            artifactsPaths.Add(Path.Combine(
                root,
                "artifacts",
                "build",
                "bin",
                "CodexProfileLauncher.JobBroker.TestHost",
                config,
                "CodexProfileLauncher.JobBroker.TestHost.exe"));

            var classicPath = Path.Combine(
                root,
                "tests",
                "CodexProfileLauncher.JobBroker.TestHost",
                "bin",
                ToClassicConfigurationName(config),
                TargetFrameworkFolder,
                RuntimeIdentifier,
                "CodexProfileLauncher.JobBroker.TestHost.exe");

            if (preferArtifacts)
            {
                foreach (var path in artifactsPaths)
                {
                    yield return path;
                }

                yield return classicPath;
            }
            else
            {
                yield return classicPath;
                foreach (var path in artifactsPaths)
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// When tests run under UseArtifactsOutput, BaseDirectory looks like
    /// <c>{ArtifactsPath}/bin/CodexProfileLauncher.Windows.Tests/{config}/</c>.
    /// </summary>
    private static bool TryGetArtifactsBinRoot(out string? artifactsBinRoot)
    {
        artifactsBinRoot = null;
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        // .../bin/Project/config  -> climb to bin
        var projectDir = current.Parent;
        var binDir = projectDir?.Parent;
        if (binDir is null || !binDir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Confirm sibling project folders exist under this bin root.
        var sibling = Path.Combine(binDir.FullName, "CodexProfileLauncher");
        if (!Directory.Exists(sibling))
        {
            return false;
        }

        artifactsBinRoot = binDir.FullName;
        return true;
    }

    private static IEnumerable<string> ConfigurationFolderNames()
    {
        // Prefer the configuration that produced the currently loaded test assembly.
        var fromBase = InferConfigurationToken(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(fromBase))
        {
            yield return fromBase;
        }

        yield return "release";
        yield return "debug";
        yield return "Release";
        yield return "Debug";
    }

    private static string? InferConfigurationToken(string baseDirectory)
    {
        // Classic: .../bin/Release/netX/
        // Artifacts: .../bin/Project/release or .../bin/Project/release_win-x64
        var leaf = new DirectoryInfo(baseDirectory).Name;
        if (leaf.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return new DirectoryInfo(baseDirectory).Parent?.Name;
        }

        var underscore = leaf.IndexOf('_');
        return underscore > 0 ? leaf[..underscore] : leaf;
    }

    private static string ToClassicConfigurationName(string configurationToken)
    {
        if (configurationToken.Equals("release", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        if (configurationToken.Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        return configurationToken;
    }
}
