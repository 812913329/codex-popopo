using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
public sealed class AiSettingsDialogViewModelTests
{
    [TestMethod]
    public void ApiKey_IsKeptAsPlainTextWithoutNormalization()
    {
        var viewModel = CreateViewModel();
        const string key = "sk-visible-plain-text-value";

        viewModel.ApiKey = key;

        Assert.AreEqual(key, viewModel.ApiKey);
        Assert.IsTrue(viewModel.IsDirty);
    }

    [TestMethod]
    public void ClearApi_ClearsKeyAndDisablesApi()
    {
        var viewModel = CreateViewModel(new AiSettingsEditorState(
            true,
            AiSettingsDialogViewModel.DefaultBaseUrl,
            "sk-value",
            "gpt-test",
            "medium",
            false,
            string.Empty,
            true,
            "r1"));

        viewModel.ClearApi();

        Assert.IsFalse(viewModel.ApiEnabled);
        Assert.AreEqual(string.Empty, viewModel.ApiKey);
        Assert.AreEqual(string.Empty, viewModel.SelectedModel);
    }

    [TestMethod]
    public void Validate_AllowsHttpAndDoesNotRequireSkPrefix()
    {
        var viewModel = CreateViewModel();
        viewModel.ApiEnabled = true;
        viewModel.BaseUrl = "http://example.test/custom-root";
        viewModel.ApiKey = "plain-value-without-prefix";

        Assert.IsNull(viewModel.Validate());
    }

    [TestMethod]
    public void Validate_RequiresKeyOnlyWhenApiIsEnabled()
    {
        var viewModel = CreateViewModel();
        viewModel.ApiEnabled = true;
        viewModel.ApiKey = string.Empty;

        Assert.AreEqual("启用自定义 API 时必须填写 API Key。", viewModel.Validate());

        viewModel.ApiEnabled = false;
        Assert.IsNull(viewModel.Validate());
    }

    [TestMethod]
    public async Task TestConnection_UsesUnsavedBaseUrlAndPlainKeyAndFillsModels()
    {
        string? observedUrl = null;
        string? observedKey = null;
        var viewModel = CreateViewModel(
            test: (url, key, _) =>
            {
                observedUrl = url;
                observedKey = key;
                return Task.FromResult(new AiConnectionTestDisplay(
                    true,
                    "https://service.test/models",
                    200,
                    18,
                    2,
                    ["claude-sonnet-4", "glm-5"],
                    "ok"));
            });
        viewModel.BaseUrl = "https://service.test/root/";
        viewModel.ApiKey = "visible-key";

        await viewModel.TestConnectionAsync();

        Assert.AreEqual("https://service.test/root/", observedUrl);
        Assert.AreEqual("visible-key", observedKey);
        StringAssert.Contains(viewModel.TestResultText, "2 个模型");
        CollectionAssert.AreEqual(new[] { "claude-sonnet-4", "glm-5" }, viewModel.AvailableModels.ToArray());
        Assert.AreEqual("claude-sonnet-4", viewModel.SelectedModel);
    }

    [TestMethod]
    public async Task RefreshModels_PreservesExistingSelectionWhenStillPresent()
    {
        var viewModel = CreateViewModel(
            new AiSettingsEditorState(
                true,
                AiSettingsDialogViewModel.DefaultBaseUrl,
                "key",
                "glm-5",
                "high",
                false,
                string.Empty,
                true,
                "r1"),
            test: (_, _, _) => Task.FromResult(new AiConnectionTestDisplay(
                true,
                "https://service.test/models",
                200,
                10,
                3,
                ["claude-sonnet-4", "glm-5", "deepseek-v3"],
                "ok")));

        await viewModel.RefreshModelsAsync();

        Assert.AreEqual("glm-5", viewModel.SelectedModel);
        Assert.AreEqual("high", viewModel.ModelReasoningEffort);
        Assert.HasCount(3, viewModel.AvailableModels);
    }

    [TestMethod]
    public async Task SaveConflict_KeepsCurrentInputAndAllowsExplicitRetry()
    {
        var attempts = 0;
        var disk = new AiSettingsEditorState(
            false,
            AiSettingsDialogViewModel.DefaultBaseUrl,
            "disk-key",
            string.Empty,
            "medium",
            false,
            string.Empty,
            true,
            "r2");
        var viewModel = CreateViewModel(
            save: (state, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new AiSettingsConflictException("changed", disk);
                }

                return Task.FromResult(state with { RevisionToken = "r3" });
            });
        viewModel.ApiKey = "current-key";

        Assert.IsFalse(await viewModel.SaveAsync());
        Assert.IsTrue(viewModel.HasConflict);
        Assert.AreEqual("current-key", viewModel.ApiKey);

        Assert.IsTrue(await viewModel.SaveAsync());
        Assert.AreEqual("current-key", viewModel.ApiKey);
        Assert.IsFalse(viewModel.HasConflict);
    }

    [TestMethod]
    public void PromptUndo_RestoresContentBeforeImportOrClear()
    {
        var viewModel = CreateViewModel();
        viewModel.SystemPrompt = "before";
        viewModel.RememberPromptBeforeImport();
        viewModel.SystemPrompt = "after";

        viewModel.UndoPrompt();

        Assert.AreEqual("before", viewModel.SystemPrompt);
    }

    private static AiSettingsDialogViewModel CreateViewModel(
        AiSettingsEditorState? initial = null,
        Func<AiSettingsEditorState, CancellationToken, Task<AiSettingsEditorState>>? save = null,
        Func<string, string, CancellationToken, Task<AiConnectionTestDisplay>>? test = null)
    {
        var state = initial ?? new AiSettingsEditorState(
            false,
            AiSettingsDialogViewModel.DefaultBaseUrl,
            string.Empty,
            string.Empty,
            "medium",
            false,
            string.Empty,
            true,
            "r1");

        return new AiSettingsDialogViewModel(
            state,
            isProfileRunning: false,
            profileName: "测试环境",
            settingsFilePath: @"C:\profile\.launcher\ai-settings.toml",
            promptFilePath: @"C:\profile\.launcher\system-prompt.md",
            reload: _ => Task.FromResult(state),
            save: save ?? ((updated, _) => Task.FromResult(updated with { RevisionToken = "saved" })),
            test: test ?? ((_, _, _) => Task.FromResult(new AiConnectionTestDisplay(
                true,
                "https://example.test/models",
                200,
                1,
                1,
                ["probe-model"],
                "ok"))),
            copyText: _ => { },
            openPath: _ => { });
    }
}
