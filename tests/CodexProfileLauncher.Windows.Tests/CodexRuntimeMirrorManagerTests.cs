using System.Text.Json.Nodes;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Infrastructure;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
public sealed class CodexRuntimeMirrorManagerTests
{
    [TestMethod]
    public async Task EnsureMirrorAsync_CopiesCompleteTreeAndPreservesPackageMetadata()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var manager = fixture.CreateManager();

        var mirror = await manager.EnsureMirrorAsync(installation);

        var expectedApp = fixture.ExpectedAppDirectory(installation.PackageFullName);
        Assert.AreEqual(Path.Combine(expectedApp, "ChatGPT.exe"), mirror.ExecutablePath, ignoreCase: true);
        Assert.AreEqual(installation.PackageFullName, mirror.PackageFullName);
        Assert.AreEqual(installation.PackageFamilyName, mirror.PackageFamilyName);
        Assert.AreEqual(installation.Version, mirror.Version);
        Assert.AreEqual(installation.InstallRoot, mirror.InstallRoot);
        Assert.AreEqual("main", await File.ReadAllTextAsync(mirror.ExecutablePath));
        Assert.AreEqual(
            "settings",
            await File.ReadAllTextAsync(Path.Combine(expectedApp, "nested", "settings.json")));
        Assert.IsTrue(Directory.Exists(Path.Combine(expectedApp, "empty")));

        var marker = Path.Combine(expectedApp, CodexRuntimeMirrorManager.CompletionMarkerFileName);
        Assert.IsTrue(File.Exists(marker));
        var markerText = await File.ReadAllTextAsync(marker);
        StringAssert.Contains(markerText, "resources/app.asar");
        StringAssert.Contains(markerText, "sha256");
        var payloadNewest = Directory.EnumerateFiles(expectedApp, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(marker, StringComparison.OrdinalIgnoreCase))
            .Max(File.GetLastWriteTimeUtc);
        Assert.IsGreaterThanOrEqualTo(payloadNewest, File.GetLastWriteTimeUtc(marker));
        AssertNoTemporaryDirectories(expectedApp);
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_ValidCacheIsReusedWithoutCopying()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var copied = 0;
        var manager = fixture.CreateManager(_ => Interlocked.Increment(ref copied));
        var first = await manager.EnsureMirrorAsync(installation);
        Assert.IsGreaterThan(0, copied);
        copied = 0;
        var originalWrite = File.GetLastWriteTimeUtc(first.ExecutablePath);

        var second = await manager.EnsureMirrorAsync(installation);

        Assert.AreEqual(0, copied);
        Assert.AreEqual(first.ExecutablePath, second.ExecutablePath, ignoreCase: true);
        Assert.AreEqual(originalWrite, File.GetLastWriteTimeUtc(second.ExecutablePath));
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_DamagedCriticalFileAndExtraFileAreRebuilt()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var manager = fixture.CreateManager();
        var mirror = await manager.EnsureMirrorAsync(installation);
        var app = Path.GetDirectoryName(mirror.ExecutablePath)!;
        await File.WriteAllTextAsync(Path.Combine(app, "chrome.dll"), "tampered");
        await File.WriteAllTextAsync(Path.Combine(app, "unexpected.bin"), "extra");

        var rebuilt = await manager.EnsureMirrorAsync(installation);

        Assert.AreEqual("chrome", await File.ReadAllTextAsync(Path.Combine(app, "chrome.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(app, "unexpected.bin")));
        Assert.AreEqual(mirror.ExecutablePath, rebuilt.ExecutablePath, ignoreCase: true);
        AssertNoTemporaryDirectories(app);
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_EqualLengthNonCriticalChangeIsRebuiltFromStoreSource()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var manager = fixture.CreateManager();
        var mirror = await manager.EnsureMirrorAsync(installation);
        var settings = Path.Combine(Path.GetDirectoryName(mirror.ExecutablePath)!, "nested", "settings.json");
        var sourceLength = new FileInfo(settings).Length;
        await File.WriteAllTextAsync(settings, "tampered");
        Assert.AreEqual(sourceLength, new FileInfo(settings).Length);

        _ = await manager.EnsureMirrorAsync(installation);

        Assert.AreEqual("settings", await File.ReadAllTextAsync(settings));
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_RuntimeGeneratedBdicDictionaryIsPreservedAndCacheIsReused()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var copied = 0;
        var manager = fixture.CreateManager(_ => Interlocked.Increment(ref copied));
        var mirror = await manager.EnsureMirrorAsync(installation);
        var dictionaryDirectory = Path.Combine(
            Path.GetDirectoryName(mirror.ExecutablePath)!,
            "Dictionaries");
        Directory.CreateDirectory(dictionaryDirectory);
        var dictionary = Path.Combine(dictionaryDirectory, "en-US-10-1.bdic");
        await File.WriteAllTextAsync(dictionary, "runtime dictionary");
        copied = 0;

        _ = await manager.EnsureMirrorAsync(installation);

        Assert.AreEqual(0, copied);
        Assert.AreEqual("runtime dictionary", await File.ReadAllTextAsync(dictionary));
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_RuntimeDictionaryDirectoryRejectsNonBdicContentAndRebuilds()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var manager = fixture.CreateManager();
        var mirror = await manager.EnsureMirrorAsync(installation);
        var dictionaryDirectory = Path.Combine(
            Path.GetDirectoryName(mirror.ExecutablePath)!,
            "Dictionaries");
        Directory.CreateDirectory(dictionaryDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dictionaryDirectory, "injected.dll"),
            "not a dictionary");

        _ = await manager.EnsureMirrorAsync(installation);

        Assert.IsFalse(Directory.Exists(dictionaryDirectory));
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_MalformedMarkerNullFieldsAreRebuiltInsteadOfEscapingValidation()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var copied = 0;
        var manager = fixture.CreateManager(_ => Interlocked.Increment(ref copied));
        var mirror = await manager.EnsureMirrorAsync(installation);
        var markerPath = Path.Combine(
            Path.GetDirectoryName(mirror.ExecutablePath)!,
            CodexRuntimeMirrorManager.CompletionMarkerFileName);

        var marker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!.AsObject();
        marker["sourceDirectory"] = null;
        await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
        copied = 0;
        _ = await manager.EnsureMirrorAsync(installation);
        Assert.IsGreaterThan(0, copied);

        marker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!.AsObject();
        marker["files"]!.AsArray()[0] = null;
        await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
        copied = 0;
        _ = await manager.EnsureMirrorAsync(installation);
        Assert.IsGreaterThan(0, copied);
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_CleansOnlyStrictlyNamedAbandonedBuildDirectories()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var manager = fixture.CreateManager();
        var mirror = await manager.EnsureMirrorAsync(installation);
        var packageDirectory = Directory.GetParent(Path.GetDirectoryName(mirror.ExecutablePath)!)!.FullName;
        var orphanStaging = Path.Combine(
            packageDirectory,
            $".app.staging-123-{Guid.NewGuid():N}");
        var orphanReplaced = Path.Combine(
            packageDirectory,
            $".app.replaced-456-{Guid.NewGuid():N}");
        var userDirectory = Path.Combine(packageDirectory, ".app.staging-user-content");
        Directory.CreateDirectory(orphanStaging);
        Directory.CreateDirectory(orphanReplaced);
        Directory.CreateDirectory(userDirectory);
        await File.WriteAllTextAsync(Path.Combine(orphanStaging, "partial.bin"), "partial");
        await File.WriteAllTextAsync(Path.Combine(orphanReplaced, "old.bin"), "old");

        _ = await manager.EnsureMirrorAsync(installation);

        Assert.IsFalse(Directory.Exists(orphanStaging));
        Assert.IsFalse(Directory.Exists(orphanReplaced));
        Assert.IsTrue(Directory.Exists(userDirectory));
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_AbandonedJunctionIsNeverTraversedOrDeleted()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var manager = fixture.CreateManager();
        var mirror = await manager.EnsureMirrorAsync(installation);
        var packageDirectory = Directory.GetParent(Path.GetDirectoryName(mirror.ExecutablePath)!)!.FullName;
        var outside = Path.Combine(fixture.Root, "outside-sentinel");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var junction = Path.Combine(
            packageDirectory,
            $".app.staging-789-{Guid.NewGuid():N}");
        CreateDirectoryLink(junction, outside);

        try
        {
            _ = await manager.EnsureMirrorAsync(installation);

            Assert.IsTrue(File.Exists(sentinel));
            Assert.IsTrue(Directory.Exists(junction));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }
        }
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_ConcurrentCallersPublishOnlyOneCopy()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        using var copyEntered = new ManualResetEventSlim();
        using var releaseCopy = new ManualResetEventSlim();
        var copied = 0;
        var firstBlocked = 0;
        Action<string> observer = _ =>
        {
            Interlocked.Increment(ref copied);
            if (Interlocked.CompareExchange(ref firstBlocked, 1, 0) == 0)
            {
                copyEntered.Set();
                Assert.IsTrue(releaseCopy.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        var firstManager = fixture.CreateManager(observer);
        var secondManager = fixture.CreateManager(observer);

        var firstTask = firstManager.EnsureMirrorAsync(installation);
        Assert.IsTrue(copyEntered.Wait(TimeSpan.FromSeconds(10)));
        var secondTask = secondManager.EnsureMirrorAsync(installation);
        await Task.Delay(150);
        releaseCopy.Set();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.AreEqual(fixture.SourceFileCount, copied);
        Assert.AreEqual(results[0].ExecutablePath, results[1].ExecutablePath, ignoreCase: true);
        AssertNoTemporaryDirectories(Path.GetDirectoryName(results[0].ExecutablePath)!);
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_InsufficientSpaceFailsBeforeCopyAndLeavesSourceUntouched()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var manager = new CodexRuntimeMirrorManager(
            fixture.CacheRoot,
            _ => 0,
            TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsExactlyAsync<CodexRuntimeMirrorException>(
            () => manager.EnsureMirrorAsync(installation));

        Assert.AreEqual("RUNTIME_MIRROR_DISK_SPACE_INSUFFICIENT", error.Code);
        StringAssert.Contains(error.Details, "RequiredBytes=");
        Assert.AreEqual("main", await File.ReadAllTextAsync(installation.ExecutablePath));
        Assert.IsFalse(Directory.Exists(fixture.ExpectedAppDirectory(installation.PackageFullName)));
        AssertNoStagingUnderCache(fixture.CacheRoot);
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_OverlappingCacheIsRejectedBeforeCreatingIt()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var overlappingCache = Path.Combine(fixture.SourceAppRoot, "runtime-cache");
        var manager = new CodexRuntimeMirrorManager(
            overlappingCache,
            _ => long.MaxValue,
            TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsExactlyAsync<CodexRuntimeMirrorException>(
            () => manager.EnsureMirrorAsync(installation));

        Assert.AreEqual("RUNTIME_MIRROR_PATH_OVERLAP", error.Code);
        Assert.IsFalse(Directory.Exists(overlappingCache));
        Assert.AreEqual("main", await File.ReadAllTextAsync(installation.ExecutablePath));
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_UnsafePackageNameCannotEscapeCacheRoot()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation("..\\..//outside:*?package");
        var manager = fixture.CreateManager();

        var mirror = await manager.EnsureMirrorAsync(installation);

        Assert.IsTrue(PathUtilities.IsSameOrNested(mirror.ExecutablePath, fixture.CacheRoot));
        Assert.IsFalse(mirror.ExecutablePath.Contains("..", StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "outside")));
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_MissingCriticalFileFailsExplicitly()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        File.Delete(Path.Combine(fixture.SourceAppRoot, "resources", "codex.exe"));
        var manager = fixture.CreateManager();

        var error = await Assert.ThrowsExactlyAsync<CodexRuntimeMirrorException>(
            () => manager.EnsureMirrorAsync(installation));

        Assert.AreEqual("RUNTIME_MIRROR_CRITICAL_FILE_MISSING", error.Code);
        StringAssert.Contains(error.Details, "resources/codex.exe");
        AssertNoStagingUnderCache(fixture.CacheRoot);
    }
    [TestMethod]
    public async Task EnsureMirrorAsync_CopyFailureCleansOnlyStagingAndKeepsSourceAndExistingCache()
    {
        using var fixture = new RuntimeMirrorFixture();
        var installation = fixture.CreateInstallation();
        var initial = await fixture.CreateManager().EnsureMirrorAsync(installation);
        var cachedChrome = Path.Combine(Path.GetDirectoryName(initial.ExecutablePath)!, "chrome.dll");
        await File.WriteAllTextAsync(cachedChrome, "damaged-cache");
        var failingManager = fixture.CreateManager(_ => throw new IOException("injected copy failure"));

        var error = await Assert.ThrowsExactlyAsync<CodexRuntimeMirrorException>(
            () => failingManager.EnsureMirrorAsync(installation));

        Assert.AreEqual("RUNTIME_MIRROR_COPY_FAILED", error.Code);
        Assert.AreEqual("main", await File.ReadAllTextAsync(installation.ExecutablePath));
        Assert.AreEqual("damaged-cache", await File.ReadAllTextAsync(cachedChrome));
        AssertNoStagingUnderCache(fixture.CacheRoot);
    }

    [TestMethod]
    public async Task EnsureMirrorAsync_NewPackageVersionDoesNotDeleteOlderVersion()
    {
        using var fixture = new RuntimeMirrorFixture();
        var firstInstallation = fixture.CreateInstallation("OpenAI.Codex_1.0.0.0_x64__test");
        var secondInstallation = fixture.CreateInstallation("OpenAI.Codex_2.0.0.0_x64__test") with
        {
            Version = new Version(2, 0, 0, 0),
        };
        var manager = fixture.CreateManager();

        var first = await manager.EnsureMirrorAsync(firstInstallation);
        var second = await manager.EnsureMirrorAsync(secondInstallation);

        Assert.AreNotEqual(first.ExecutablePath, second.ExecutablePath, ignoreCase: true);
        Assert.IsTrue(File.Exists(first.ExecutablePath));
        Assert.IsTrue(File.Exists(second.ExecutablePath));
        Assert.HasCount(2, Directory.GetDirectories(fixture.CacheRoot));
    }

    [TestMethod]
    public void BuildSafePackageDirectoryName_IsDeterministicAndCollisionResistant()
    {
        var first = CodexRuntimeMirrorManager.BuildSafePackageDirectoryName("a/b");
        var again = CodexRuntimeMirrorManager.BuildSafePackageDirectoryName("a/b");
        var second = CodexRuntimeMirrorManager.BuildSafePackageDirectoryName("a\\b");

        Assert.AreEqual(first, again);
        Assert.AreNotEqual(first, second);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('\\', first);
        Assert.IsLessThanOrEqualTo(113, first.Length);
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // A directory junction exercises the same reparse-point boundary without Developer Mode.
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 mklink 测试辅助进程。");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"创建测试 junction 失败，exit={process.ExitCode}: " +
                $"{standardOutput} {standardError}");
        }
    }

    private static void AssertNoTemporaryDirectories(string appDirectory)
    {
        var packageDirectory = Directory.GetParent(appDirectory)!.FullName;
        Assert.IsEmpty(Directory.GetDirectories(packageDirectory, ".app.staging-*"));
        Assert.IsEmpty(Directory.GetDirectories(packageDirectory, ".app.replaced-*"));
    }

    private static void AssertNoStagingUnderCache(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        Assert.IsEmpty(Directory.GetDirectories(
            cacheRoot,
            ".app.staging-*",
            SearchOption.AllDirectories));
    }

    private sealed class RuntimeMirrorFixture : IDisposable
    {
        public RuntimeMirrorFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CodexProfileLauncher.RuntimeMirror.Tests",
                Guid.NewGuid().ToString("N"));
            SourceInstallRoot = Path.Combine(Root, "source-package");
            SourceAppRoot = Path.Combine(SourceInstallRoot, "app");
            CacheRoot = Path.Combine(Root, "runtime-cache");
            Directory.CreateDirectory(Path.Combine(SourceAppRoot, "resources"));
            Directory.CreateDirectory(Path.Combine(SourceAppRoot, "nested"));
            Directory.CreateDirectory(Path.Combine(SourceAppRoot, "empty"));
            WriteSourceFile("ChatGPT.exe", "main");
            WriteSourceFile("chrome.dll", "chrome");
            WriteSourceFile("resources/app.asar", "asar-payload");
            WriteSourceFile("resources/codex.exe", "codex-cli");
            WriteSourceFile("nested/settings.json", "settings");
        }

        public string Root { get; }
        public string SourceInstallRoot { get; }
        public string SourceAppRoot { get; }
        public string CacheRoot { get; }
        public int SourceFileCount => Directory.GetFiles(SourceAppRoot, "*", SearchOption.AllDirectories).Length;

        public CodexInstallation CreateInstallation(
            string packageFullName = "OpenAI.Codex_1.0.0.0_x64__test") => new(
            packageFullName,
            "OpenAI.Codex_test",
            new Version(1, 0, 0, 0),
            SourceInstallRoot,
            Path.Combine(SourceAppRoot, "ChatGPT.exe"));

        public CodexRuntimeMirrorManager CreateManager(Action<string>? beforeCopyFile = null) => new(
            CacheRoot,
            _ => long.MaxValue,
            TimeSpan.FromSeconds(10),
            beforeCopyFile);

        public string ExpectedAppDirectory(string packageFullName) => Path.Combine(
            CacheRoot,
            CodexRuntimeMirrorManager.BuildSafePackageDirectoryName(packageFullName),
            "app");

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }

        private void WriteSourceFile(string relativePath, string content)
        {
            var path = Path.Combine(
                SourceAppRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        }
    }
}