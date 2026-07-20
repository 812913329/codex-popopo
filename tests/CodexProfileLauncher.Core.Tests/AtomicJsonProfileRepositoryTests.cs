using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Persistence;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class AtomicJsonProfileRepositoryTests
{
    [TestMethod]
    public async Task SaveAndLoad_RoundTripsStateAndIncrementsRevision()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var state = new LauncherState
        {
            Profiles =
            [
                new CodexProfile
                {
                    Name = "工作",
                    DataRoot = temp.Combine("profile"),
                    WorkingDirectory = temp.Path,
                }
            ],
        };

        var saved = await repository.SaveAsync(state, expectedRevision: 0);
        var loaded = await repository.LoadAsync();

        Assert.AreEqual(0L, state.Revision, "Repository must not mutate caller state before commit is accepted.");
        Assert.AreEqual(1L, saved.Revision);
        Assert.AreEqual(1L, loaded.Revision);
        Assert.AreEqual("工作", loaded.Profiles.Single().Name);
    }

    [TestMethod]
    public async Task Save_WithStaleRevision_IsRejected()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var state = await repository.SaveAsync(new LauncherState(), expectedRevision: 0);

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(state, expectedRevision: 0));

        Assert.AreEqual("STORE_REVISION_CONFLICT", exception.Code);
    }

    [TestMethod]
    public async Task CorruptMainFile_IsNotSilentlyReplacedWithEmptyState()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var state = await repository.SaveAsync(new LauncherState(), expectedRevision: 0);
        state.Profiles.Add(new CodexProfile
        {
            Name = "需要保留",
            DataRoot = temp.Combine("profile"),
            WorkingDirectory = temp.Path,
        });
        _ = await repository.SaveAsync(state, expectedRevision: 1);
        await File.WriteAllTextAsync(temp.Combine("profiles.json"), "{ broken json");

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.LoadAsync());

        Assert.AreEqual("STORE_CORRUPT", exception.Code);
        StringAssert.Contains(exception.Details, "备份");
    }

    [TestMethod]
    public async Task Save_RelativeProfileRoot_IsRejectedSemantically()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var state = new LauncherState
        {
            Profiles = [NewProfile(Guid.NewGuid(), "relative-profile")],
        };

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(state, expectedRevision: 0));

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
        StringAssert.Contains(exception.Details, "绝对路径");
    }

    [TestMethod]
    public async Task Save_DuplicateProfileIds_AreRejectedSemantically()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var duplicateId = Guid.NewGuid();
        var state = new LauncherState
        {
            Profiles =
            [
                NewProfile(duplicateId, temp.Combine("one")),
                NewProfile(duplicateId, temp.Combine("two")),
            ],
        };

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(state, expectedRevision: 0));

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
        StringAssert.Contains(exception.Details, "UUID 重复");
    }

    [TestMethod]
    public async Task Save_OverlappingProfileRoots_AreRejectedSemantically()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var outer = temp.Combine("profile");
        var state = new LauncherState
        {
            Profiles =
            [
                NewProfile(Guid.NewGuid(), outer),
                NewProfile(Guid.NewGuid(), System.IO.Path.Combine(outer, "nested")),
            ],
        };

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(state, expectedRevision: 0));

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
        StringAssert.Contains(exception.Details, "重合或嵌套");
    }

    [TestMethod]
    public async Task Load_EmptyProfileId_IsReportedAsSemanticCorruption()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var invalidState = new LauncherState
        {
            Profiles = [NewProfile(Guid.Empty, temp.Combine("profile"))],
        };
        var json = JsonSerializer.Serialize(invalidState, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        await File.WriteAllTextAsync(temp.Combine("profiles.json"), json);

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.LoadAsync());

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
        StringAssert.Contains(exception.Details, "UUID 不能为空");
    }

    [TestMethod]
    public async Task Save_ReceiptPathsMustMatchOwningProfile()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("profile"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = profile.Id,
            RootProcessId = 42,
            ProcessStartUtcTicks = DateTime.UtcNow.Ticks,
            ExecutablePath = temp.Combine("ChatGPT.exe"),
            CodexHomePath = temp.Combine("other-home"),
            AppDataPath = paths.AppData,
        };

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(new LauncherState { Profiles = [profile] }, expectedRevision: 0));

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
        StringAssert.Contains(exception.Details, "运行记录路径");
    }

    [TestMethod]
    public async Task Save_ReceiptProfileIdMustMatchOwningProfile()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("profile"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = Guid.NewGuid(),
            RootProcessId = 42,
            ProcessStartUtcTicks = DateTime.UtcNow.Ticks,
            ExecutablePath = temp.Combine("ChatGPT.exe"),
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
        };

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(new LauncherState { Profiles = [profile] }, expectedRevision: 0));

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
        StringAssert.Contains(exception.Details, "另一个 profile");
    }

    [TestMethod]
    public async Task Save_PendingLaunchReceipt_AllowsZeroProcessIdentity()
    {
        using var temp = new TemporaryDirectory();
        var repository = new AtomicJsonProfileRepository(temp.Path);
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("profile"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = profile.Id,
            IsLaunchPending = true,
            RootProcessId = 0,
            ProcessStartUtcTicks = 0,
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
        };

        var saved = await repository.SaveAsync(
            new LauncherState { Profiles = [profile] },
            expectedRevision: 0);

        Assert.AreEqual(1L, saved.Revision);
        Assert.IsTrue(saved.Profiles.Single().ActiveInstance!.IsLaunchPending);
    }

    [TestMethod]
    public async Task Save_PendingLaunchReceipt_CannotClaimIsolationVerified()
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("pending-invalid"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = profile.Id,
            IsLaunchPending = true,
            IsIsolationVerified = true,
            RootProcessId = 0,
            ProcessStartUtcTicks = 0,
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
        };
        var repository = new AtomicJsonProfileRepository(temp.Combine("state"));

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(new LauncherState { Profiles = [profile] }, 0));

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
    }

    [TestMethod]
    public async Task Save_JobPendingIntent_RoundTripsOwnershipContract()
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("job-pending"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        var launchId = Guid.NewGuid();
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = profile.Id,
            LaunchId = launchId,
            OwnershipMode = ProcessOwnershipModes.WindowsJob,
            OwnershipVersion = ProcessOwnershipModes.WindowsJobVersion,
            LaunchPhase = JobLaunchPhases.PendingIntent,
            JobObjectName = JobName(profile.Id, launchId),
            ReadyEventName = ReadyName(launchId),
            WindowsSessionId = 1,
            IsLaunchPending = true,
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
        };
        var repository = new AtomicJsonProfileRepository(temp.Combine("state"));

        var saved = await repository.SaveAsync(new LauncherState { Profiles = [profile] }, 0);
        var receipt = saved.Profiles.Single().ActiveInstance!;

        Assert.AreEqual(ProcessOwnershipModes.WindowsJob, receipt.OwnershipMode);
        Assert.AreEqual(JobLaunchPhases.PendingIntent, receipt.LaunchPhase);
        Assert.AreEqual(JobName(profile.Id, launchId), receipt.JobObjectName);
    }

    [TestMethod]
    public async Task Save_JobResumed_RequiresExactBrokerAndRootIdentity()
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("job-assigned"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        var launchId = Guid.NewGuid();
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = profile.Id,
            LaunchId = launchId,
            OwnershipMode = ProcessOwnershipModes.WindowsJob,
            OwnershipVersion = ProcessOwnershipModes.WindowsJobVersion,
            LaunchPhase = JobLaunchPhases.Resumed,
            JobObjectName = JobName(profile.Id, launchId),
            ReadyEventName = ReadyName(launchId),
            WindowsSessionId = 1,
            IsLaunchPending = false,
            RootProcessId = 42,
            ProcessStartUtcTicks = 43,
            BrokerProcessId = 45,
            BrokerProcessStartUtcTicks = 46,
            ExecutablePath = temp.Combine("ChatGPT.exe"),
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
        };
        var repository = new AtomicJsonProfileRepository(temp.Combine("state"));

        var saved = await repository.SaveAsync(new LauncherState { Profiles = [profile] }, 0);
        Assert.AreEqual(JobLaunchPhases.Resumed, saved.Profiles.Single().ActiveInstance!.LaunchPhase);

        saved.Profiles.Single().ActiveInstance!.BrokerProcessStartUtcTicks = 0;
        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(saved, saved.Revision));
        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
    }

    [TestMethod]
    public async Task Save_JobReceiptWithAnotherProfileOrLaunchName_IsRejected()
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("job-wrong-name"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        var launchId = Guid.NewGuid();
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = profile.Id,
            LaunchId = launchId,
            OwnershipMode = ProcessOwnershipModes.WindowsJob,
            OwnershipVersion = ProcessOwnershipModes.WindowsJobVersion,
            LaunchPhase = JobLaunchPhases.PendingIntent,
            JobObjectName = JobName(Guid.NewGuid(), launchId),
            ReadyEventName = ReadyName(launchId),
            WindowsSessionId = 1,
            IsLaunchPending = true,
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
        };
        var repository = new AtomicJsonProfileRepository(temp.Combine("state"));

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(new LauncherState { Profiles = [profile] }, 0));

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
        StringAssert.Contains(exception.Details, "Job Object 名称");
    }

    [TestMethod]
    public async Task Save_LegacyReceiptCannotClaimJobFields()
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("legacy-job-fields"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = profile.Id,
            OwnershipMode = ProcessOwnershipModes.LegacyProcessTree,
            OwnershipVersion = 1,
            ReadyEventName = "Local\\unexpected",
            RootProcessId = 42,
            ProcessStartUtcTicks = 43,
            ExecutablePath = temp.Combine("ChatGPT.exe"),
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
        };
        var repository = new AtomicJsonProfileRepository(temp.Combine("state"));

        var exception = await Assert.ThrowsAsync<ProfileStoreException>(
            () => repository.SaveAsync(new LauncherState { Profiles = [profile] }, 0));

        Assert.AreEqual("STORE_SEMANTIC_INVALID", exception.Code);
        StringAssert.Contains(exception.Details, "legacy");
    }

    [TestMethod]
    public async Task Load_PreJobLegacyReceiptWithoutNewFields_RemainsSupported()
    {
        using var temp = new TemporaryDirectory();
        var profile = NewProfile(Guid.NewGuid(), temp.Combine("pre-job-legacy"));
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        profile.ActiveInstance = new RunningInstanceReceipt
        {
            ProfileId = profile.Id,
            RootProcessId = 42,
            ProcessStartUtcTicks = 43,
            ExecutablePath = temp.Combine("ChatGPT.exe"),
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
        };
        var state = new LauncherState { Profiles = [profile] };
        var json = JsonNode.Parse(JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }))!;
        var receiptNode = json["profiles"]![0]!["activeInstance"]!.AsObject();
        foreach (var property in new[]
                 {
                     "ownershipMode",
                     "ownershipVersion",
                     "launchPhase",
                     "jobObjectName",
                     "readyEventName",
                     "windowsSessionId",
                     "brokerProcessId",
                     "brokerProcessStartUtcTicks",
                 })
        {
            _ = receiptNode.Remove(property);
        }

        await File.WriteAllTextAsync(temp.Combine("profiles.json"), json.ToJsonString());
        var loaded = await new AtomicJsonProfileRepository(temp.Path).LoadAsync();

        var receipt = loaded.Profiles.Single().ActiveInstance!;
        Assert.IsTrue(ProcessOwnershipModes.IsLegacy(receipt));
        Assert.AreEqual(-1, receipt.WindowsSessionId);
        Assert.AreEqual(42, receipt.RootProcessId);
    }

    private static string JobName(Guid profileId, Guid launchId) =>
        $@"Global\CodexProfileLauncher.Job.v1.S-1-5-21-test.{profileId:N}.{launchId:N}";

    private static string ReadyName(Guid launchId) =>
        $@"Global\CodexProfileLauncher.JobReady.v1.S-1-5-21-test.{launchId:N}";

    private static CodexProfile NewProfile(Guid id, string dataRoot) => new()
    {
        Id = id,
        Name = $"环境-{id}",
        DataRoot = dataRoot,
        WorkingDirectory = dataRoot,
    };
}
