using System.Windows;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Services;
using CodexProfileLauncher.ViewModels;
using CodexProfileLauncher.Views;

namespace CodexProfileLauncher.Infrastructure;

public sealed record AiSettingsDialogResult(
    bool Saved,
    bool ApiEnabled,
    string BaseUrl,
    bool HasApiKey,
    bool SystemPromptEnabled);

public sealed class AiSettingsDialogCoordinator(
    IProfileAiSettingsService settingsService,
    ShellService shell,
    WpfDialogService dialogs)
{
    public async Task<AiSettingsDialogViewModel> CreateViewModelAsync(
        ProfileItemViewModel profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        var loaded = await settingsService.LoadAsync(paths, cancellationToken).ConfigureAwait(true);
        return BuildViewModel(profile, paths, loaded);
    }

    public async Task<AiSettingsDialogResult> ShowAsync(
        ProfileItemViewModel profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        var loaded = await settingsService.LoadAsync(paths, cancellationToken).ConfigureAwait(true);
        var viewModel = BuildViewModel(profile, paths, loaded);

        var dialog = new AiSettingsDialog(viewModel)
        {
            Owner = Application.Current.MainWindow,
        };

        try
        {
            var saved = dialog.ShowDialog() == true;
            return new(
                saved,
                viewModel.ApiEnabled,
                viewModel.BaseUrl,
                !string.IsNullOrEmpty(viewModel.ApiKey),
                viewModel.SystemPromptEnabled);
        }
        catch (Exception ex) when (ex is ProfileAiSettingsException or IOException or UnauthorizedAccessException)
        {
            dialogs.ShowError("AI 配置失败", ex.Message, ex.ToString());
            return new(false, loaded.ApiEnabled, loaded.BaseUrl, !string.IsNullOrEmpty(loaded.ApiKey), loaded.SystemPromptEnabled);
        }
    }

    private AiSettingsDialogViewModel BuildViewModel(
        ProfileItemViewModel profile,
        ProfilePaths paths,
        ProfileAiSettings loaded) =>
        new(
            ToEditorState(loaded),
            profile.IsRunning,
            profile.Name,
            paths.AiSettingsFile,
            paths.SystemPromptFile,
            reload: async token => ToEditorState(
                await settingsService.ReloadAsync(paths, token).ConfigureAwait(true)),
            save: async (edited, token) =>
            {
                try
                {
                    var saved = await settingsService.SaveAsync(
                        paths,
                        new ProfileAiSettings
                        {
                            SchemaVersion = ProfileAiSettings.CurrentSchemaVersion,
                            ApiEnabled = edited.ApiEnabled,
                            BaseUrl = edited.BaseUrl,
                            ApiKey = edited.ApiKey,
                            SelectedModel = edited.SelectedModel,
                            ModelReasoningEffort = edited.ModelReasoningEffort,
                            SystemPromptEnabled = edited.SystemPromptEnabled,
                            SystemPrompt = edited.SystemPrompt,
                            KeysmithModeEnabled = edited.KeysmithModeEnabled,
                            RevisionToken = edited.RevisionToken,
                        },
                        token).ConfigureAwait(true);
                    return ToEditorState(saved);
                }
                catch (ProfileAiSettingsException ex) when (ex.Code == "AI_SETTINGS_CONFLICT")
                {
                    var disk = await settingsService.ReloadAsync(paths, token).ConfigureAwait(true);
                    throw new AiSettingsConflictException(ex.Message, ToEditorState(disk));
                }
            },
            test: async (baseUrl, apiKey, token) =>
            {
                var tested = await settingsService.FetchModelsAsync(baseUrl, apiKey, token)
                    .ConfigureAwait(true);
                return new AiConnectionTestDisplay(
                    tested.IsSuccess,
                    tested.RequestUri.AbsoluteUri,
                    tested.HttpStatusCode,
                    (long)tested.Elapsed.TotalMilliseconds,
                    tested.ModelCount,
                    tested.ModelIds,
                    tested.Details);
            },
            copyText: shell.CopyText,
            openPath: shell.OpenPath);

    private static AiSettingsEditorState ToEditorState(ProfileAiSettings settings) => new(
        settings.ApiEnabled,
        settings.BaseUrl,
        settings.ApiKey,
        settings.SelectedModel,
        settings.ModelReasoningEffort,
        settings.SystemPromptEnabled,
        settings.SystemPrompt,
        settings.KeysmithModeEnabled,
        settings.RevisionToken);
}
