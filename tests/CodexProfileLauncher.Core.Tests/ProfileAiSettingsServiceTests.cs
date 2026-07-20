using System.Net;
using System.Text;
using System.Text.Json;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;

namespace CodexProfileLauncher.Core.Tests;

[TestClass]
public sealed class ProfileAiSettingsServiceTests
{
    [TestMethod]
    public async Task Load_InitializesExactPlaintextDefaults()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var service = new ProfileAiSettingsService();

        var settings = await service.LoadAsync(paths);
        var file = await File.ReadAllTextAsync(paths.AiSettingsFile);

        Assert.AreEqual(ProfileAiSettings.DefaultBaseUrl, settings.BaseUrl);
        Assert.AreEqual("https://ai98pro.xyz/", settings.BaseUrl);
        Assert.IsFalse(settings.ApiEnabled);
        Assert.IsTrue(settings.SystemPromptEnabled);
        Assert.IsTrue(settings.KeysmithModeEnabled);
        Assert.AreEqual(string.Empty, settings.ApiKey);
        Assert.AreEqual(string.Empty, settings.SelectedModel);
        StringAssert.Contains(file, "base_url = \"https://ai98pro.xyz/\"");
        StringAssert.Contains(file, "keysmith_mode_enabled = true");
        Assert.IsFalse(file.Contains("/v1", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(paths.SystemPromptFile));
        // Default seed is keysmith unrestricted text (not operator-core).
        StringAssert.Contains(settings.SystemPrompt, "Local fixture");
    }

    [TestMethod]
    public async Task Save_AlwaysReplacesSystemPrompt_UnderCodexHome()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var service = new ProfileAiSettingsService();
        var loaded = await service.LoadAsync(paths);

        var saved = await service.SaveAsync(paths, loaded with
        {
            SystemPromptEnabled = false, // forced back on
            SystemPrompt = string.Empty, // filled with operator-core
            KeysmithModeEnabled = false,
        });

        Assert.IsTrue(saved.SystemPromptEnabled);
        StringAssert.Contains(saved.SystemPrompt, "Operator Core");
        Assert.IsTrue(File.Exists(paths.SystemPromptFile));
        Assert.IsTrue(File.Exists(paths.CodexSystemPromptFile));
        Assert.AreEqual(
            await File.ReadAllTextAsync(paths.SystemPromptFile),
            await File.ReadAllTextAsync(paths.CodexSystemPromptFile));

        var managed = await File.ReadAllTextAsync(paths.ManagedConfigFile);
        StringAssert.Contains(managed, "model_instructions_file = ");
        StringAssert.Contains(managed, "system-prompt.md");
        // Must be its own TOML line (no glue onto log_dir).
        Assert.IsTrue(
            managed.Contains("log_dir = ", StringComparison.Ordinal) &&
            managed.Contains(Environment.NewLine + "model_instructions_file = ", StringComparison.Ordinal),
            "model_instructions_file must start on a new line after isolation keys.");

        var config = await File.ReadAllTextAsync(paths.ConfigFile);
        StringAssert.Contains(config, "model_instructions_file = \"./system-prompt.md\"");
    }

    [TestMethod]
    public async Task Save_DefaultKeysmithMode_WritesGptUnrestrictedUnderCodexHome()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var service = new ProfileAiSettingsService();
        var loaded = await service.LoadAsync(paths);
        Assert.IsTrue(loaded.KeysmithModeEnabled);

        var saved = await service.SaveAsync(paths, loaded with { RevisionToken = loaded.RevisionToken });
        Assert.IsTrue(saved.KeysmithModeEnabled);
        Assert.IsTrue(File.Exists(paths.KeysmithInstructionFile));
        StringAssert.Contains(await File.ReadAllTextAsync(paths.KeysmithInstructionFile), "Local fixture");

        var managed = (await File.ReadAllTextAsync(paths.ManagedConfigFile)).Replace('\\', '/');
        StringAssert.Contains(managed, "model_instructions_file = ");
        StringAssert.Contains(managed, "/gpt-unrestricted.md");
        Assert.IsFalse(managed.Contains(".launcher/system-prompt.md", StringComparison.OrdinalIgnoreCase));

        var config = await File.ReadAllTextAsync(paths.ConfigFile);
        StringAssert.Contains(config, "model_instructions_file = \"./gpt-unrestricted.md\"");
    }

    [TestMethod]
    public async Task Save_RoundTripsPlaintextAndGeneratesManagedOverridesWithLiveCatalog()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var handler = new ScriptedHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://relay.example/custom/models", request.RequestUri!.AbsoluteUri);
            return OkModelsJson(["claude-sonnet-4", "glm-5"]);
        });
        using var client = new HttpClient(handler);
        var service = new ProfileAiSettingsService(client);
        var loaded = await service.LoadAsync(paths);
        var key = "visible-key-without-sk-prefix";
        var prompt = "完全替换\r\n工具规则";

        var saved = await service.SaveAsync(paths, loaded with
        {
            ApiEnabled = true,
            BaseUrl = "https://relay.example/custom",
            ApiKey = key,
            SelectedModel = "glm-5",
            ModelReasoningEffort = "high",
            SystemPromptEnabled = true,
            // Custom prompt path requires keysmith off; keysmith would overwrite with unrestricted text.
            KeysmithModeEnabled = false,
            SystemPrompt = prompt,
        });
        Assert.AreEqual("high", saved.ModelReasoningEffort);

        Assert.AreEqual(key, saved.ApiKey);
        Assert.AreEqual("glm-5", saved.SelectedModel);
        Assert.AreEqual(prompt, saved.SystemPrompt);
        StringAssert.Contains(await File.ReadAllTextAsync(paths.AiSettingsFile), $"api_key = \"{key}\"");
        StringAssert.Contains(await File.ReadAllTextAsync(paths.AiSettingsFile), "selected_model = \"glm-5\"");
        Assert.AreEqual(prompt, await File.ReadAllTextAsync(paths.SystemPromptFile));
        var managed = await File.ReadAllTextAsync(paths.ManagedConfigFile);
        StringAssert.Contains(managed, "cli_auth_credentials_store = \"file\"");
        StringAssert.Contains(managed, "model = \"glm-5\"");
        StringAssert.Contains(managed, "model_reasoning_effort = \"high\"");
        StringAssert.Contains(managed, "model_provider = \"profile_launcher\"");
        StringAssert.Contains(managed, "model_catalog_json = ");
        StringAssert.Contains(managed, "base_url = \"https://relay.example/custom\"");
        StringAssert.Contains(managed, "env_key = \"CODEX_PROFILE_LAUNCHER_API_KEY\"");
        StringAssert.Contains(managed, "wire_api = \"responses\"");
        StringAssert.Contains(managed, "requires_openai_auth = false");
        StringAssert.Contains(managed, "model_instructions_file = ");
        StringAssert.Contains(managed, paths.CodexSystemPromptFile.Replace('\\', '/'));
        Assert.IsTrue(File.Exists(paths.CodexSystemPromptFile));
        Assert.IsFalse(managed.Contains("openai_base_url", StringComparison.Ordinal));
        Assert.IsFalse(managed.Contains("preferred_auth_method", StringComparison.Ordinal));
        Assert.IsFalse(managed.Contains(key, StringComparison.Ordinal));

        var catalog = await File.ReadAllTextAsync(paths.ModelCatalogFile);
        using var document = JsonDocument.Parse(catalog);
        var models = document.RootElement.GetProperty("models");
        Assert.AreEqual(2, models.GetArrayLength());
        Assert.AreEqual("claude-sonnet-4", models[0].GetProperty("slug").GetString());
        Assert.AreEqual("list", models[0].GetProperty("visibility").GetString());
        Assert.AreEqual("glm-5", models[1].GetProperty("slug").GetString());
        Assert.IsTrue(models[0].TryGetProperty("base_instructions", out _));
        Assert.IsTrue(models[0].TryGetProperty("default_reasoning_level", out _));
        var levels = models[0].GetProperty("supported_reasoning_levels");
        Assert.IsGreaterThanOrEqualTo(7, levels.GetArrayLength());
        var efforts = levels.EnumerateArray()
            .Select(level => level.GetProperty("effort").GetString())
            .ToArray();
        CollectionAssert.Contains(efforts, "max");
        CollectionAssert.Contains(efforts, "ultra");
        CollectionAssert.Contains(efforts, "xhigh");
        Assert.IsTrue(File.Exists(Path.Combine(paths.CodexHome, "pl-glm-5.config.toml")));
    }

    [TestMethod]
    public async Task Save_AutoSelectsFirstLiveModelWhenSelectedModelEmpty()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var handler = new ScriptedHandler(_ => OkModelsJson(["deepseek-v3", "qwen3"]));
        using var client = new HttpClient(handler);
        var service = new ProfileAiSettingsService(client);
        var loaded = await service.LoadAsync(paths);

        var saved = await service.SaveAsync(paths, loaded with
        {
            ApiEnabled = true,
            BaseUrl = "https://relay.example/",
            ApiKey = "k",
            SelectedModel = string.Empty,
        });

        Assert.AreEqual("deepseek-v3", saved.SelectedModel);
        StringAssert.Contains(await File.ReadAllTextAsync(paths.ManagedConfigFile), "model = \"deepseek-v3\"");
    }

    [TestMethod]
    public async Task Save_FailsWhenSelectedModelMissingFromLiveList()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var handler = new ScriptedHandler(_ => OkModelsJson(["only-a"]));
        using var client = new HttpClient(handler);
        var service = new ProfileAiSettingsService(client);
        var loaded = await service.LoadAsync(paths);

        var exception = await Assert.ThrowsAsync<ProfileAiSettingsException>(() => service.SaveAsync(paths, loaded with
        {
            ApiEnabled = true,
            BaseUrl = "https://relay.example/",
            ApiKey = "k",
            SelectedModel = "missing-model",
        }));

        Assert.AreEqual("AI_MODEL_NOT_IN_LIVE_LIST", exception.Code);
        Assert.IsFalse(File.Exists(paths.ModelCatalogFile));
    }

    [TestMethod]
    public async Task Save_FailsWhenLiveFetchFails()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"nope\"}", Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var service = new ProfileAiSettingsService(client);
        var loaded = await service.LoadAsync(paths);

        var exception = await Assert.ThrowsAsync<ProfileAiSettingsException>(() => service.SaveAsync(paths, loaded with
        {
            ApiEnabled = true,
            BaseUrl = "https://relay.example/",
            ApiKey = "k",
            SelectedModel = "x",
        }));

        Assert.AreEqual("AI_MODELS_FETCH_FAILED", exception.Code);
    }

    [TestMethod]
    public async Task Save_DisablesOverridesButPreservesContent_AndClearRemovesBackupKey()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var handler = new ScriptedHandler(_ => OkModelsJson(["probe_model"]));
        using var client = new HttpClient(handler);
        var service = new ProfileAiSettingsService(client);
        var loaded = await service.LoadAsync(paths);
        var enabled = await service.SaveAsync(paths, loaded with
        {
            ApiEnabled = true,
            ApiKey = "secret-to-clear",
            SelectedModel = "probe_model",
            SystemPromptEnabled = true,
            KeysmithModeEnabled = false,
            SystemPrompt = "keep prompt",
        });

        var disabled = await service.SaveAsync(paths, enabled with
        {
            ApiEnabled = false,
            SystemPromptEnabled = false, // product still forces system prompt replacement on
        });
        var managed = await File.ReadAllTextAsync(paths.ManagedConfigFile);
        Assert.AreEqual("secret-to-clear", disabled.ApiKey);
        Assert.AreEqual("keep prompt", disabled.SystemPrompt);
        Assert.IsTrue(disabled.SystemPromptEnabled);
        Assert.IsFalse(managed.Contains("model_provider", StringComparison.Ordinal));
        Assert.IsFalse(managed.Contains("model_catalog_json", StringComparison.Ordinal));
        // System prompt replacement remains on by default.
        Assert.IsTrue(managed.Contains("model_instructions_file", StringComparison.Ordinal));

        _ = await service.SaveAsync(paths, disabled with { ApiKey = string.Empty });
        Assert.IsFalse(File.Exists(paths.AiSettingsFile + ".bak"));
        Assert.IsFalse((await File.ReadAllTextAsync(paths.AiSettingsFile)).Contains("secret-to-clear", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Save_DetectsExternalEditConflict()
    {
        using var temp = new TemporaryDirectory();
        var paths = ProfilePaths.FromRoot(temp.Combine("profile"));
        var service = new ProfileAiSettingsService();
        var loaded = await service.LoadAsync(paths);
        var beforeExternal = await File.ReadAllTextAsync(paths.SystemPromptFile);
        await File.WriteAllTextAsync(paths.SystemPromptFile, beforeExternal + "external");

        ProfileAiSettingsException? exception = null;
        try
        {
            _ = await service.SaveAsync(paths, loaded with { SystemPrompt = "local" });
        }
        catch (ProfileAiSettingsException ex)
        {
            exception = ex;
        }

        Assert.IsNotNull(exception);
        Assert.AreEqual("AI_SETTINGS_CONFLICT", exception!.Code);
        // Conflict path must not overwrite the externally edited disk prompt.
        var diskPrompt = await File.ReadAllTextAsync(paths.SystemPromptFile);
        StringAssert.Contains(diskPrompt, "external");
        Assert.AreNotEqual("local", diskPrompt);
    }

    [TestMethod]
    public async Task Resolve_ReloadsDiskAndKeepsProfilesIsolated()
    {
        using var temp = new TemporaryDirectory();
        var firstPaths = ProfilePaths.FromRoot(temp.Combine("first"));
        var secondPaths = ProfilePaths.FromRoot(temp.Combine("second"));
        var handler = new ScriptedHandler(_ => OkModelsJson(["shared-model"]));
        using var client = new HttpClient(handler);
        var service = new ProfileAiSettingsService(client);
        var first = await service.LoadAsync(firstPaths);
        var second = await service.LoadAsync(secondPaths);
        _ = await service.SaveAsync(firstPaths, first with
        {
            ApiEnabled = true,
            ApiKey = "first-key",
            SelectedModel = "shared-model",
        });
        _ = await service.SaveAsync(secondPaths, second with
        {
            ApiEnabled = true,
            ApiKey = "second-key",
            SelectedModel = "shared-model",
        });

        var firstLaunch = await service.ResolveLaunchConfigurationAsync(firstPaths);
        var secondLaunch = await service.ResolveLaunchConfigurationAsync(secondPaths);

        Assert.AreEqual("first-key", firstLaunch.ApiKey);
        Assert.AreEqual("second-key", secondLaunch.ApiKey);
        StringAssert.Contains(await File.ReadAllTextAsync(firstPaths.ManagedConfigFile), "model = \"shared-model\"");
        Assert.IsTrue(File.Exists(firstPaths.ModelCatalogFile));
    }

    [TestMethod]
    public async Task TestConnection_UsesCurrentInputAndExactModelsPath_AndParsesIds()
    {
        var handler = new RecordingHandler(OkModelsJson(["id-a", "id-b"]));
        using var client = new HttpClient(handler);
        var service = new ProfileAiSettingsService(client);

        var result = await service.TestConnectionAsync("https://relay.example/v1/", "unsaved-key");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.ModelCount);
        CollectionAssert.AreEqual(new[] { "id-a", "id-b" }, result.ModelIds.ToArray());
        Assert.AreEqual("https://relay.example/v1/models", result.RequestUri.AbsoluteUri);
        Assert.AreEqual("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.AreEqual("unsaved-key", handler.Request.Headers.Authorization.Parameter);
    }

    [TestMethod]
    public void TryReadModelIds_SupportsModelAndModelIdAliases()
    {
        var ids = ProfileAiSettingsService.TryReadModelIds(
            """{"data":[{"model":"m1"},{"model_id":"m2"},{"id":"m3"},{"id":"m1"}]}""");

        CollectionAssert.AreEqual(new[] { "m1", "m2", "m3" }, ids.ToArray());
    }

    [TestMethod]
    public void CatalogBuilder_IncludesRequiredFieldsAndPrefersSelectedWhenTruncating()
    {
        var many = Enumerable.Range(1, 100).Select(i => $"model-{i}").ToArray();
        var json = CodexModelCatalogBuilder.BuildJson(many, preferredModel: "model-99");
        using var document = JsonDocument.Parse(json);
        var models = document.RootElement.GetProperty("models");
        Assert.AreEqual(CodexModelCatalogBuilder.MaxModels, models.GetArrayLength());
        Assert.AreEqual("model-99", models[0].GetProperty("slug").GetString());
        Assert.AreEqual("list", models[0].GetProperty("visibility").GetString());
        Assert.IsTrue(models[0].TryGetProperty("base_instructions", out _));
        Assert.IsTrue(models[0].TryGetProperty("truncation_policy", out _));
        Assert.IsTrue(models[0].TryGetProperty("supported_reasoning_levels", out _));
        Assert.IsTrue(models[0].TryGetProperty("default_reasoning_level", out _));
        Assert.IsGreaterThanOrEqualTo(
            2,
            models[0].GetProperty("supported_reasoning_levels").GetArrayLength());
    }

    private static HttpResponseMessage OkModelsJson(IEnumerable<string> ids)
    {
        var items = string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"object\":\"model\"}}"));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"object\":\"list\",\"data\":[{items}]}}", Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
