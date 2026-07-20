using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CodexProfileLauncher.Core.Configuration;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Persistence;
using CodexProfileLauncher.Core.Services;
using CodexProfileLauncher.Core.Validation;
using CodexProfileLauncher.Infrastructure;
using CodexProfileLauncher.Views;

namespace CodexProfileLauncher.ViewModels;

internal enum LaunchIntentSaveResolution
{
    CommitConfirmed,
    NotCommitted,
    StateReloaded,
    Indeterminate,
}

public enum EnvironmentConfigTab
{
    Overview,
    Ai,
    Skills,
    Paths,
    Advanced,
    Manage,
}

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly LauncherPaths _paths;
    private readonly IProfileRepository _repository;
    private readonly IProfileAiSettingsService _aiSettingsService;
    private readonly IProfileSkillsService _skillsService;
    private readonly AiSettingsDialogCoordinator _aiSettingsCoordinator;
    private readonly WindowsCodexAppLocator _appLocator;
    private readonly CodexRuntimeMirrorManager _runtimeMirrorManager;
    private readonly WindowsWindowController _windowController;
    private readonly WindowsProcessInspector _processInspector;
    private readonly WindowsJobObjectManager _jobManager;
    private readonly StartupIsolationVerifier _startupVerifier;
    private readonly JsonlFileLogger _logger;
    private readonly WpfDialogService _dialogs;
    private readonly ShellService _shell;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly HashSet<Guid> _scheduledPendingRecoveries = [];
    private readonly Dictionary<Guid, TrackedProcess> _ownedProcesses = [];
    private readonly AsyncRelayCommand _primaryActionCommand;
    private readonly AsyncRelayCommand _closeCodexCommand;
    private readonly AsyncRelayCommand _createProfileCommand;
    private readonly AsyncRelayCommand _editProfileCommand;
    private readonly AsyncRelayCommand _duplicateProfileCommand;
    private readonly AsyncRelayCommand _deleteProfileCommand;
    private readonly AsyncRelayCommand _refreshCodexCommand;
    private readonly AsyncRelayCommand _saveConfigCommand;
    private readonly RelayCommand _openDataRootCommand;
    private readonly RelayCommand _openWorkingDirectoryCommand;
    private readonly RelayCommand _copyDataRootCommand;
    private readonly RelayCommand _openLogsCommand;
    private readonly AsyncRelayCommand _openAiSettingsCommand;
    private readonly AsyncRelayCommand _copyAiKeyCommand;
    private readonly AsyncRelayCommand _installAllSkillsCommand;
    private readonly AsyncRelayCommand _importSkillCommand;
    private readonly RelayCommand _openSkillsDirectoryCommand;
    private readonly AsyncRelayCommand _saveSelectedSkillCommand;
    private readonly AsyncRelayCommand _resetSelectedSkillCommand;
    private readonly RelayCommand _goToAiTabCommand;
    private readonly RelayCommand _goToSkillsTabCommand;

    private LauncherState _state = new();
    private ProfileItemViewModel? _selectedProfile;
    private CodexInstallation? _installation;
    private bool _isBusy;
    private bool _initialized;
    private bool _suppressSelectionSideEffects;
    private bool _disposed;
    private long _selectionGeneration;
    private string _statusTitle = "请选择环境";
    private string _statusMessage = "选择或创建环境后即可启动独立 Codex。";
    private string _statusGlyph = "\uE946";
    private string _primaryActionText = "启动 Codex";
    private string _codexDetectionText = "正在检测…";
    private string _codexVersionText = string.Empty;
    private string _codexExecutablePath = string.Empty;
    private string _runtimeDetails = "尚无运行记录。";
    private string _configText = string.Empty;
    private string _savedConfigText = string.Empty;
    private string _aiSettingsSummary = "API 未启用 · Key 未保存 · 系统提示词未启用";
    private string _skillsSummary = "尚未加载技能";
    private string _skillSearchText = string.Empty;
    private string _selectedSkillMarkdown = string.Empty;
    private string _savedSkillMarkdown = string.Empty;
    private string _skillEditorHint = "选择左侧技能以查看或编辑 SKILL.md。";
    private bool _hasAiKey;
    private EnvironmentConfigTab _selectedEnvironmentTab = EnvironmentConfigTab.Overview;
    private AiSettingsDialogViewModel? _aiSettingsEditor;
    private SkillItemViewModel? _selectedSkill;
    private PrimaryActionMode _primaryMode = PrimaryActionMode.Launch;

    public MainWindowViewModel(
        LauncherPaths paths,
        IProfileRepository repository,
        IProfileAiSettingsService aiSettingsService,
        WindowsCodexAppLocator appLocator,
        CodexRuntimeMirrorManager runtimeMirrorManager,
        WindowsWindowController windowController,
        WindowsProcessInspector processInspector,
        WindowsJobObjectManager jobManager,
        StartupIsolationVerifier startupVerifier,
        JsonlFileLogger logger,
        WpfDialogService dialogs,
        ShellService shell,
        IProfileSkillsService? skillsService = null)
    {
        _paths = paths;
        _repository = repository;
        _aiSettingsService = aiSettingsService;
        _skillsService = skillsService ?? new ProfileSkillsService();
        _appLocator = appLocator;
        _runtimeMirrorManager = runtimeMirrorManager;
        _windowController = windowController;
        _processInspector = processInspector;
        _jobManager = jobManager;
        _startupVerifier = startupVerifier;
        _logger = logger;
        _dialogs = dialogs;
        _shell = shell;
        _aiSettingsCoordinator = new(aiSettingsService, shell, dialogs);

        _primaryActionCommand = new(PrimaryActionAsync, () => CanPrimaryAction);
        _closeCodexCommand = new(CloseCodexAsync, () => HasSelection && SelectedProfile!.IsRunning && !IsBusy);
        _createProfileCommand = new(CreateProfileAsync, () => !IsBusy);
        _editProfileCommand = new(EditProfileAsync, () => HasSelection && !IsBusy);
        _duplicateProfileCommand = new(DuplicateProfileAsync, () => HasSelection && !IsBusy);
        _deleteProfileCommand = new(DeleteProfileAsync, () => HasSelection && !IsBusy);
        _refreshCodexCommand = new(() => RefreshCodexAsync(showDialogOnFailure: true), () => !IsBusy);
        _saveConfigCommand = new(SaveConfigAsync, () => HasSelection && IsConfigDirty && !IsBusy);
        _openDataRootCommand = new(() => OpenPath(SelectedProfile?.DataRoot), () => HasSelection);
        _openWorkingDirectoryCommand = new(
            () => OpenPath(SelectedProfile?.WorkingDirectory),
            () => HasSelection && !string.IsNullOrWhiteSpace(SelectedProfile?.WorkingDirectory));
        _copyDataRootCommand = new(() =>
        {
            if (SelectedProfile is not null)
            {
                try
                {
                    _shell.CopyText(SelectedProfile.DataRoot);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.Warning(
                        "CLIPBOARD_COPY_FAILED",
                        ex.Message,
                        SelectedProfile.Profile.Id);
                    _dialogs.ShowError(
                        "复制失败",
                        "剪贴板暂时不可用，请稍后重试。",
                        ex.ToString());
                }
            }
        }, () => HasSelection);
        _openLogsCommand = new(() => OpenPath(_paths.LogsDirectory));
        _openAiSettingsCommand = new(OpenAiSettingsAsync, () => HasSelection && !IsBusy);
        _copyAiKeyCommand = new(CopySelectedAiKey, () => HasSelection && HasAiKey && !IsBusy);
        _installAllSkillsCommand = new(InstallAllSkillsAsync, () => HasSelection && !IsBusy);
        _importSkillCommand = new(ImportSkillAsync, () => HasSelection && !IsBusy);
        _openSkillsDirectoryCommand = new(OpenSkillsDirectory, () => HasSelection);
        _saveSelectedSkillCommand = new(SaveSelectedSkillAsync, () => HasSelection && HasSelectedSkill && IsSkillMarkdownDirty && !IsBusy);
        _resetSelectedSkillCommand = new(ResetSelectedSkillAsync, () => HasSelection && HasSelectedSkill && !IsBusy);
        _goToAiTabCommand = new(() => SelectedEnvironmentTab = EnvironmentConfigTab.Ai, () => HasSelection);
        _goToSkillsTabCommand = new(() => SelectedEnvironmentTab = EnvironmentConfigTab.Skills, () => HasSelection);
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public ProfileItemViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value))
            {
                return;
            }

            if (_suppressSelectionSideEffects)
            {
                SetSelectedProfileCore(value);
                return;
            }

            // Selection changes are transactional. Keep the accepted selection visible until
            // the dirty-config decision and the target profile load both complete.
            OnPropertyChanged(nameof(SelectedProfile));
            if (IsBusy || _disposed)
            {
                return;
            }

            var generation = Interlocked.Increment(ref _selectionGeneration);
            _ = HandleSelectionChangedSafeAsync(value, generation);
        }
    }

    public bool HasSelection => SelectedProfile is not null;

    public bool IsEmpty => Profiles.Count == 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanPrimaryAction));
                UpdateCommandStates();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string StatusGlyph
    {
        get => _statusGlyph;
        private set => SetProperty(ref _statusGlyph, value);
    }

    public string PrimaryActionText
    {
        get => _primaryActionText;
        private set => SetProperty(ref _primaryActionText, value);
    }

    public string PrimaryActionAutomationId => _primaryMode switch
    {
        PrimaryActionMode.Activate => "OpenCodexButton",
        PrimaryActionMode.RefreshCodex => "RefreshCodexButton",
        PrimaryActionMode.EditProfile => "EditProfileButton",
        PrimaryActionMode.OpenConfig => "OpenConfigButton",
        _ => "LaunchCodexButton",
    };

    public bool CanPrimaryAction =>
        HasSelection &&
        !IsBusy &&
        _primaryMode != PrimaryActionMode.Blocked;

    public string CodexDetectionText
    {
        get => _codexDetectionText;
        private set => SetProperty(ref _codexDetectionText, value);
    }

    public string CodexVersionText
    {
        get => _codexVersionText;
        private set => SetProperty(ref _codexVersionText, value);
    }

    public string CodexExecutablePath
    {
        get => _codexExecutablePath;
        private set => SetProperty(ref _codexExecutablePath, value);
    }

    public string RuntimeDetails
    {
        get => _runtimeDetails;
        private set => SetProperty(ref _runtimeDetails, value);
    }

    public string ConfigText
    {
        get => _configText;
        set
        {
            if (SetProperty(ref _configText, value))
            {
                OnPropertyChanged(nameof(IsConfigDirty));
                _saveConfigCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConfigDirty => !_configText.Equals(_savedConfigText, StringComparison.Ordinal);

    public string AiSettingsSummary
    {
        get => _aiSettingsSummary;
        private set => SetProperty(ref _aiSettingsSummary, value);
    }

    public bool HasAiKey
    {
        get => _hasAiKey;
        private set
        {
            if (SetProperty(ref _hasAiKey, value))
            {
                _copyAiKeyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand CreateProfileCommand => _createProfileCommand;

    public ICommand EditProfileCommand => _editProfileCommand;

    public ICommand DuplicateProfileCommand => _duplicateProfileCommand;

    public ICommand DeleteProfileCommand => _deleteProfileCommand;

    public ICommand PrimaryActionCommand => _primaryActionCommand;

    public ICommand CloseCodexCommand => _closeCodexCommand;

    public ICommand OpenDataRootCommand => _openDataRootCommand;

    public ICommand OpenWorkingDirectoryCommand => _openWorkingDirectoryCommand;

    public ICommand CopyDataRootCommand => _copyDataRootCommand;

    public ICommand RefreshCodexCommand => _refreshCodexCommand;

    public ICommand OpenLogsCommand => _openLogsCommand;

    public ICommand SaveConfigCommand => _saveConfigCommand;

    public ICommand OpenAiSettingsCommand => _openAiSettingsCommand;

    public ICommand CopyAiKeyCommand => _copyAiKeyCommand;

    public ICommand InstallAllSkillsCommand => _installAllSkillsCommand;

    public ICommand ImportSkillCommand => _importSkillCommand;

    public ICommand OpenSkillsDirectoryCommand => _openSkillsDirectoryCommand;

    public ICommand SaveSelectedSkillCommand => _saveSelectedSkillCommand;

    public ICommand ResetSelectedSkillCommand => _resetSelectedSkillCommand;

    public ICommand GoToAiTabCommand => _goToAiTabCommand;

    public ICommand GoToSkillsTabCommand => _goToSkillsTabCommand;

    public ObservableCollection<SkillItemViewModel> Skills { get; } = [];

    public IEnumerable<SkillItemViewModel> FilteredSkills =>
        string.IsNullOrWhiteSpace(SkillSearchText)
            ? Skills
            : Skills.Where(skill =>
                skill.Name.Contains(SkillSearchText, StringComparison.OrdinalIgnoreCase) ||
                skill.Id.Contains(SkillSearchText, StringComparison.OrdinalIgnoreCase) ||
                skill.Description.Contains(SkillSearchText, StringComparison.OrdinalIgnoreCase));

    public EnvironmentConfigTab SelectedEnvironmentTab
    {
        get => _selectedEnvironmentTab;
        set
        {
            if (SetProperty(ref _selectedEnvironmentTab, value))
            {
                OnPropertyChanged(nameof(SelectedTabIndex));
                OnPropertyChanged(nameof(IsOverviewTab));
                OnPropertyChanged(nameof(IsAiTab));
                OnPropertyChanged(nameof(IsSkillsTab));
                OnPropertyChanged(nameof(IsPathsTab));
                OnPropertyChanged(nameof(IsAdvancedTab));
                OnPropertyChanged(nameof(IsManageTab));
            }
        }
    }

    public int SelectedTabIndex
    {
        get => (int)SelectedEnvironmentTab;
        set
        {
            if (value < 0 || value > (int)EnvironmentConfigTab.Manage)
            {
                return;
            }

            SelectedEnvironmentTab = (EnvironmentConfigTab)value;
        }
    }

    public bool IsOverviewTab => SelectedEnvironmentTab == EnvironmentConfigTab.Overview;

    public bool IsAiTab => SelectedEnvironmentTab == EnvironmentConfigTab.Ai;

    public bool IsSkillsTab => SelectedEnvironmentTab == EnvironmentConfigTab.Skills;

    public bool IsPathsTab => SelectedEnvironmentTab == EnvironmentConfigTab.Paths;

    public bool IsAdvancedTab => SelectedEnvironmentTab == EnvironmentConfigTab.Advanced;

    public bool IsManageTab => SelectedEnvironmentTab == EnvironmentConfigTab.Manage;

    public AiSettingsDialogViewModel? AiSettingsEditor
    {
        get => _aiSettingsEditor;
        private set => SetProperty(ref _aiSettingsEditor, value);
    }

    public string SkillsSummary
    {
        get => _skillsSummary;
        private set => SetProperty(ref _skillsSummary, value);
    }

    public string SkillSearchText
    {
        get => _skillSearchText;
        set
        {
            if (SetProperty(ref _skillSearchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(FilteredSkills));
            }
        }
    }

    public SkillItemViewModel? SelectedSkill
    {
        get => _selectedSkill;
        set
        {
            if (SetProperty(ref _selectedSkill, value))
            {
                OnPropertyChanged(nameof(HasSelectedSkill));
                _ = LoadSelectedSkillMarkdownAsync();
                _saveSelectedSkillCommand.RaiseCanExecuteChanged();
                _resetSelectedSkillCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedSkill => SelectedSkill is not null;

    public string SelectedSkillMarkdown
    {
        get => _selectedSkillMarkdown;
        set
        {
            if (SetProperty(ref _selectedSkillMarkdown, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsSkillMarkdownDirty));
                _saveSelectedSkillCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSkillMarkdownDirty =>
        !_selectedSkillMarkdown.Equals(_savedSkillMarkdown, StringComparison.Ordinal);

    public string SkillEditorHint
    {
        get => _skillEditorHint;
        private set => SetProperty(ref _skillEditorHint, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _state = await _repository.LoadAsync().ConfigureAwait(true);
            Profiles.Clear();
            foreach (var profile in _state.Profiles.OrderBy(profile => profile.CreatedUtc))
            {
                Profiles.Add(new ProfileItemViewModel(profile));
            }

            OnPropertyChanged(nameof(IsEmpty));
            await RefreshCodexAsync(showDialogOnFailure: false).ConfigureAwait(true);

            foreach (var profile in Profiles)
            {
                await ReconcileRuntimeAsync(profile, persistStoppedReceipt: true).ConfigureAwait(true);
            }

            var initialSelection = _state.SelectedProfileId is { } selectedId
                ? Profiles.FirstOrDefault(profile => profile.Profile.Id == selectedId)
                : Profiles.FirstOrDefault();
            var generation = Interlocked.Increment(ref _selectionGeneration);
            await LoadSelectedProfileAsync(initialSelection, persistSelection: false, generation)
                .ConfigureAwait(true);
            _initialized = true;
            _logger.Info("APP_INITIALIZED", "启动器初始化完成。", details: new
            {
                ProfileCount = Profiles.Count,
                CodexVersion = _installation?.DisplayVersion,
            });
        }
        catch (ProfileStoreException ex)
        {
            SetStatus("环境列表无法读取", ex.Message, "\uEA39", PrimaryActionMode.Blocked, ex.Details);
            _logger.Error(ex.Code, ex.Message, ex, details: ex.Details);
            _dialogs.ShowError("环境列表无法读取", ex.Message, ex.Details);
        }
        catch (Exception ex)
        {
            SetStatus("启动器初始化失败", ex.Message, "\uEA39", PrimaryActionMode.Blocked, ex.ToString());
            _logger.Error("APP_INITIALIZATION_FAILED", ex.Message, ex);
            _dialogs.ShowError("启动器初始化失败", ex.Message, ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task HandleSelectionChangedSafeAsync(
        ProfileItemViewModel? requestedProfile,
        long generation)
    {
        if (!IsSelectionRequestCurrent(generation) || IsBusy)
        {
            OnPropertyChanged(nameof(SelectedProfile));
            return;
        }

        IsBusy = true;
        try
        {
            var currentProfile = _selectedProfile;
            if (!await ResolveAllDirtyStateAsync(currentProfile).ConfigureAwait(true))
            {
                OnPropertyChanged(nameof(SelectedProfile));
                return;
            }

            if (!IsSelectionRequestCurrent(generation))
            {
                return;
            }

            await LoadSelectedProfileAsync(requestedProfile, _initialized, generation)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("PROFILE_SELECTION_FAILED", ex.Message, ex, requestedProfile?.Profile.Id);
            _dialogs.ShowError("无法切换环境", ex.Message, ex.ToString());
            OnPropertyChanged(nameof(SelectedProfile));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSelectedProfileAsync(
        ProfileItemViewModel? requestedProfile,
        bool persistSelection,
        long generation)
    {
        var requestedProfileId = requestedProfile?.Profile.Id;
        if (requestedProfile is not null &&
            !Profiles.Any(profile =>
                profile.Profile.Id == requestedProfileId &&
                ReferenceEquals(profile, requestedProfile)))
        {
            throw new InvalidOperationException("目标环境已不在当前环境列表中。");
        }

        string configText;
        if (requestedProfile is null)
        {
            configText = string.Empty;
        }
        else
        {
            await ReconcileRuntimeAsync(requestedProfile, persistStoppedReceipt: true)
                .ConfigureAwait(true);
            if (!IsSelectionRequestCurrent(generation))
            {
                return;
            }

            configText = await ReadProfileConfigAsync(requestedProfile).ConfigureAwait(true);
        }

        if (!IsSelectionRequestCurrent(generation))
        {
            return;
        }

        var previousSelectedProfileId = _state.SelectedProfileId;
        _state.SelectedProfileId = requestedProfileId;
        try
        {
            if (persistSelection)
            {
                await SaveStateAsync().ConfigureAwait(true);
            }
        }
        catch
        {
            _state.SelectedProfileId = previousSelectedProfileId;
            throw;
        }

        if (!IsSelectionRequestCurrent(generation))
        {
            return;
        }

        ApplySelectedProfile(requestedProfile, configText);
        await LoadAiSummaryAsync(requestedProfile).ConfigureAwait(true);
        await LoadAiEditorAsync(requestedProfile).ConfigureAwait(true);
        await LoadSkillsAsync(requestedProfile).ConfigureAwait(true);
    }

    private async Task<string> ReadProfileConfigAsync(ProfileItemViewModel profile)
    {
        var paths = ProfilePaths.FromRoot(profile.DataRoot);
        return File.Exists(paths.ConfigFile)
            ? await File.ReadAllTextAsync(paths.ConfigFile).ConfigureAwait(true)
            : ConfigIsolationAuditor.CreateDefaultConfig();
    }

    private void ApplySelectedProfile(ProfileItemViewModel? profile, string configText)
    {
        _suppressSelectionSideEffects = true;
        try
        {
            SelectedProfile = profile;
        }
        finally
        {
            _suppressSelectionSideEffects = false;
        }

        _savedConfigText = configText;
        _configText = configText;
        OnPropertyChanged(nameof(ConfigText));
        OnPropertyChanged(nameof(IsConfigDirty));
        _saveConfigCommand.RaiseCanExecuteChanged();

        if (profile is null)
        {
            SetStatus("请选择环境", "选择或创建环境后即可启动独立 Codex。", "\uE946", PrimaryActionMode.Blocked);
        }
        else
        {
            UpdateStatusForSelected();
        }
    }

    private void SetSelectedProfileCore(ProfileItemViewModel? profile)
    {
        if (!SetProperty(ref _selectedProfile, profile, nameof(SelectedProfile)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelection));
        UpdateCommandStates();
    }

    private bool IsSelectionRequestCurrent(long generation) =>
        !_disposed && generation == Interlocked.Read(ref _selectionGeneration);

    private async Task CreateProfileAsync()
    {
        if (!await GuardDirtyConfigBeforeNavigationAsync().ConfigureAwait(true))
        {
            return;
        }

        var id = Guid.NewGuid();
        var editor = new ProfileEditorViewModel(
            null,
            _state.Profiles,
            id,
            "我的 Codex",
            _paths.GetSuggestedProfileRoot(id),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var profile = _dialogs.EditProfile(editor);
        if (profile is null)
        {
            return;
        }

        await AddNewProfileAsync(profile, configToCopy: null, aiSettingsToCopy: null).ConfigureAwait(true);
    }

    private async Task EditProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var item = SelectedProfile;
        var source = item.Profile;
        var originalDataRoot = source.DataRoot;
        var editor = new ProfileEditorViewModel(
            source,
            _state.Profiles,
            source.Id,
            source.Name,
            source.DataRoot,
            source.WorkingDirectory);
        var edited = _dialogs.EditProfile(editor);
        if (edited is null)
        {
            return;
        }

        var dataRootChanged = !string.Equals(
            PathUtilities.Normalize(originalDataRoot),
            PathUtilities.Normalize(edited.DataRoot),
            StringComparison.OrdinalIgnoreCase);

        if (dataRootChanged)
        {
            if (item.IsRunning)
            {
                _dialogs.ShowError(
                    "无法迁移数据目录",
                    "请先关闭此环境的 Codex，再修改数据目录。",
                    "运行中迁移可能导致文件锁定与状态损坏。");
                return;
            }

            if (!_dialogs.Confirm(
                    "迁移环境数据",
                    $"将把“{source.Name}”的全部环境数据从：{Environment.NewLine}{originalDataRoot}{Environment.NewLine}迁移到：{Environment.NewLine}{edited.DataRoot}{Environment.NewLine}{Environment.NewLine}迁移过程可能需要一些时间。",
                    confirmText: "开始迁移"))
            {
                return;
            }
        }

        IsBusy = true;
        try
        {
            if (dataRootChanged)
            {
                var pathValidation = ProfilePathPolicy.Validate(edited, _state.Profiles);
                if (!pathValidation.IsValid)
                {
                    ShowValidationFailure(pathValidation);
                    return;
                }

                ProfileAiSettings? aiSettings = null;
                try
                {
                    aiSettings = await _aiSettingsService
                        .LoadAsync(ProfilePaths.FromRoot(originalDataRoot))
                        .ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is ProfileAiSettingsException or IOException or UnauthorizedAccessException)
                {
                    _logger.Warning(
                        "PROFILE_MIGRATE_AI_LOAD_SKIPPED",
                        ex.Message,
                        source.Id);
                }

                await ProfileDataMigrator
                    .MigrateDataRootAsync(source.Id, originalDataRoot, edited.DataRoot, aiSettings)
                    .ConfigureAwait(true);

                source.DataRoot = PathUtilities.Normalize(edited.DataRoot);
                source.Name = edited.Name;
                source.WorkingDirectory = edited.WorkingDirectory;
                source.UpdatedUtc = DateTimeOffset.UtcNow;
                await SaveStateAsync().ConfigureAwait(true);

                item.RefreshProfileProperties();
                Interlocked.Increment(ref _selectionGeneration);
                var configText = await ReadProfileConfigAsync(item).ConfigureAwait(true);
                ApplySelectedProfile(item, configText);
                await LoadAiSummaryAsync(item).ConfigureAwait(true);
                await LoadAiEditorAsync(item).ConfigureAwait(true);
                await LoadSkillsAsync(item).ConfigureAwait(true);
                UpdateStatusForSelected();

                _logger.Info(
                    "PROFILE_DATA_MIGRATED",
                    "环境数据目录已迁移。",
                    source.Id,
                    new { From = originalDataRoot, To = source.DataRoot });
                _dialogs.ShowInformation(
                    "迁移完成",
                    $"环境数据已迁移到：{source.DataRoot}");
                return;
            }

            var validation = await ProfilePathPolicy
                .PrepareAsync(edited, _state.Profiles)
                .ConfigureAwait(true);
            if (!validation.IsValid)
            {
                ShowValidationFailure(validation);
                return;
            }

            source.Name = edited.Name;
            source.WorkingDirectory = edited.WorkingDirectory;
            source.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveStateAsync().ConfigureAwait(true);
            SelectedProfile.RefreshProfileProperties();
            UpdateStatusForSelected();
            _logger.Info("PROFILE_UPDATED", "环境设置已更新。", source.Id);
        }
        catch (Exception ex) when (ex is ProfileDataMigrationException)
        {
            HandleOperationError("PROFILE_MIGRATE_FAILED", "环境数据迁移失败", ex, source.Id);
        }
        catch (Exception ex)
        {
            HandleOperationError("PROFILE_UPDATE_FAILED", "环境设置保存失败", ex, source.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DuplicateProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (!await GuardDirtyConfigBeforeNavigationAsync().ConfigureAwait(true))
        {
            return;
        }

        if (!_dialogs.Confirm(
                "复制环境",
                "将复制 config.toml、API 地址、明文 Key 和系统提示词；登录、会话和应用缓存不会复制。",
                confirmText: "继续复制"))
        {
            return;
        }

        var source = SelectedProfile.Profile;
        var id = Guid.NewGuid();
        var editor = new ProfileEditorViewModel(
            null,
            _state.Profiles,
            id,
            $"{source.Name} 副本",
            _paths.GetSuggestedProfileRoot(id),
            source.WorkingDirectory);
        var duplicate = _dialogs.EditProfile(editor);
        if (duplicate is null)
        {
            return;
        }

        var sourcePaths = ProfilePaths.FromRoot(source.DataRoot);
        var configToCopy = File.Exists(sourcePaths.ConfigFile)
            ? await File.ReadAllTextAsync(sourcePaths.ConfigFile).ConfigureAwait(true)
            : ConfigIsolationAuditor.CreateDefaultConfig();
        var aiSettingsToCopy = await _aiSettingsService.LoadAsync(sourcePaths).ConfigureAwait(true);
        await AddNewProfileAsync(duplicate, configToCopy, aiSettingsToCopy).ConfigureAwait(true);
    }

    private async Task AddNewProfileAsync(
        CodexProfile profile,
        string? configToCopy,
        ProfileAiSettings? aiSettingsToCopy)
    {
        IsBusy = true;
        ProfilePaths? preparedPaths = null;
        var rootExistedBefore = true;
        var profileAdded = false;
        var previousSelectedProfileId = _state.SelectedProfileId;
        try
        {
            var paths = ProfilePaths.FromRoot(profile.DataRoot);
            preparedPaths = paths;
            rootExistedBefore = Directory.Exists(paths.DataRoot);
            if (configToCopy is not null)
            {
                var report = ConfigIsolationAuditor.AuditText(
                    configToCopy,
                    paths,
                    string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                        ? paths.DataRoot
                        : profile.WorkingDirectory);
                if (!report.IsIsolated)
                {
                    ShowIsolationFailure(report, profile.Id);
                    return;
                }
            }

            var validation = await ProfilePathPolicy
                .PrepareAsync(profile, _state.Profiles)
                .ConfigureAwait(true);
            if (!validation.IsValid)
            {
                ShowValidationFailure(validation);
                return;
            }

            await ConfigIsolationAuditor.EnsureInitializedAsync(paths).ConfigureAwait(true);
            // New environments default to keysmith mode + unrestricted system instructions.
            var seedAi = aiSettingsToCopy is null
                ? new ProfileAiSettings
                {
                    KeysmithModeEnabled = true,
                    SystemPromptEnabled = true,
                    SystemPrompt = KeysmithBootstrap.GetBundledPrompt(),
                }
                : aiSettingsToCopy with
                {
                    RevisionToken = string.Empty,
                    SystemPromptEnabled = true,
                    // Keep explicit copy state; missing/default still prefers keysmith text when enabled.
                    SystemPrompt = aiSettingsToCopy.KeysmithModeEnabled
                        ? KeysmithBootstrap.GetBundledPrompt()
                        : (string.IsNullOrWhiteSpace(aiSettingsToCopy.SystemPrompt)
                            ? DefaultSystemPrompt.GetBundled()
                            : aiSettingsToCopy.SystemPrompt),
                };

            await _aiSettingsService.SaveAsync(paths, seedAi with { RevisionToken = string.Empty })
                .ConfigureAwait(true);
            if (seedAi.KeysmithModeEnabled)
            {
                await KeysmithBootstrap.ApplyAsync(paths, seedAi).ConfigureAwait(true);
            }
            if (configToCopy is not null)
            {
                await ConfigIsolationAuditor.SaveValidatedAsync(
                    configToCopy,
                    paths,
                    string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                        ? paths.DataRoot
                        : profile.WorkingDirectory).ConfigureAwait(true);
            }

            // Seed built-in skills for new environments (skip if already present after copy).
            await _skillsService.InstallAllBuiltinAsync(paths).ConfigureAwait(true);

            var newConfigText = File.Exists(paths.ConfigFile)
                ? await File.ReadAllTextAsync(paths.ConfigFile).ConfigureAwait(true)
                : ConfigIsolationAuditor.CreateDefaultConfig();

            _state.Profiles.Add(profile);
            profileAdded = true;
            _state.SelectedProfileId = profile.Id;
            await SaveStateAsync().ConfigureAwait(true);

            var item = new ProfileItemViewModel(profile);
            Profiles.Add(item);
            OnPropertyChanged(nameof(IsEmpty));
            Interlocked.Increment(ref _selectionGeneration);
            ApplySelectedProfile(item, newConfigText);
            await LoadAiSummaryAsync(item).ConfigureAwait(true);
            await LoadAiEditorAsync(item).ConfigureAwait(true);
            await LoadSkillsAsync(item).ConfigureAwait(true);
            _logger.Info("PROFILE_CREATED", "环境已创建。", profile.Id, new { profile.DataRoot });
        }
        catch (Exception ex)
        {
            if (profileAdded)
            {
                _state.Profiles.Remove(profile);
                _state.SelectedProfileId = previousSelectedProfileId;
            }

            if (!rootExistedBefore && preparedPaths is not null)
            {
                TryRemoveUncommittedProfileRoot(preparedPaths, profile.Id);
            }

            HandleOperationError("PROFILE_CREATE_FAILED", "环境创建失败", ex, profile.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void TryRemoveUncommittedProfileRoot(ProfilePaths paths, Guid profileId)
    {
        try
        {
            if (!Directory.Exists(paths.DataRoot) || !File.Exists(paths.MarkerFile))
            {
                return;
            }

            using var marker = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paths.MarkerFile));
            if (!marker.RootElement.TryGetProperty("profileId", out var idElement) ||
                !Guid.TryParse(idElement.GetString(), out var markerProfileId) ||
                markerProfileId != profileId)
            {
                _logger.Warning(
                    "PROFILE_CREATE_ROLLBACK_SKIPPED",
                    "未提交目录的身份标记不匹配，已保留磁盘内容。",
                    profileId,
                    new { paths.DataRoot });
                return;
            }

            Directory.Delete(paths.DataRoot, recursive: true);
            _logger.Info(
                "PROFILE_CREATE_ROLLED_BACK",
                "环境记录提交失败，已移除本次新建且未提交的数据目录。",
                profileId,
                new { paths.DataRoot });
        }
        catch (Exception cleanupException) when (
            cleanupException is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.Error(
                "PROFILE_CREATE_ROLLBACK_FAILED",
                cleanupException.Message,
                cleanupException,
                profileId,
                new { paths.DataRoot });
        }
    }

    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (!await GuardDirtyConfigBeforeNavigationAsync().ConfigureAwait(true))
        {
            return;
        }

        var item = SelectedProfile;
        if (item.IsRunning)
        {
            _dialogs.ShowError(
                "无法删除环境",
                "请先关闭此环境的 Codex。",
                "运行中或状态无法确认时，启动器不会删除环境记录与数据。");
            return;
        }

        var dataRoot = item.DataRoot;
        if (!_dialogs.Confirm(
                "删除环境及数据",
                $"将永久删除环境“{item.Name}”的启动器记录，以及目录中的全部数据：{Environment.NewLine}{dataRoot}{Environment.NewLine}{Environment.NewLine}此操作不可恢复。",
                confirmText: "删除环境及数据",
                isDestructive: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Delete disk first while registry still points at the path; only after success remove the record.
            await DeleteProfileDataRootAsync(item.Profile).ConfigureAwait(true);

            var nextProfile = Profiles.FirstOrDefault(profile => !ReferenceEquals(profile, item));
            var nextConfigText = nextProfile is null
                ? string.Empty
                : await ReadProfileConfigAsync(nextProfile).ConfigureAwait(true);
            var index = _state.Profiles.IndexOf(item.Profile);
            var previousSelectedProfileId = _state.SelectedProfileId;
            _state.Profiles.Remove(item.Profile);
            _state.SelectedProfileId = nextProfile?.Profile.Id;
            try
            {
                await SaveStateAsync().ConfigureAwait(true);
            }
            catch
            {
                _state.Profiles.Insert(Math.Max(index, 0), item.Profile);
                _state.SelectedProfileId = previousSelectedProfileId;
                throw;
            }

            Profiles.Remove(item);
            OnPropertyChanged(nameof(IsEmpty));
            Interlocked.Increment(ref _selectionGeneration);
            ApplySelectedProfile(nextProfile, nextConfigText);
            await LoadAiSummaryAsync(nextProfile).ConfigureAwait(true);
            await LoadAiEditorAsync(nextProfile).ConfigureAwait(true);
            await LoadSkillsAsync(nextProfile).ConfigureAwait(true);
            _logger.Info("PROFILE_REMOVED", "环境记录与磁盘数据已删除。", item.Profile.Id, new
            {
                DataRoot = dataRoot,
            });
            _dialogs.ShowInformation("环境已删除", $"已删除启动器记录及数据目录：{dataRoot}");
        }
        catch (Exception ex)
        {
            HandleOperationError("PROFILE_DELETE_FAILED", "环境删除失败", ex, item.Profile.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Permanently deletes a profile data root after verifying the on-disk marker matches the profile id.
    /// </summary>
    private async Task DeleteProfileDataRootAsync(CodexProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var paths = ProfilePaths.FromRoot(profile.DataRoot);

        if (!Directory.Exists(paths.DataRoot))
        {
            _logger.Warning(
                "PROFILE_DELETE_DATA_MISSING",
                "环境数据目录不存在，跳过磁盘删除。",
                profile.Id,
                new { paths.DataRoot });
            return;
        }

        if (!File.Exists(paths.MarkerFile))
        {
            throw new InvalidOperationException(
                $"拒绝删除：数据目录缺少环境身份标记（.codex-profile.json）。路径：{paths.DataRoot}");
        }

        try
        {
            await using var stream = File.OpenRead(paths.MarkerFile);
            using var marker = await System.Text.Json.JsonDocument.ParseAsync(stream).ConfigureAwait(true);
            if (!marker.RootElement.TryGetProperty("profileId", out var idElement) ||
                !Guid.TryParse(idElement.GetString(), out var markerProfileId) ||
                markerProfileId != profile.Id)
            {
                throw new InvalidOperationException(
                    $"拒绝删除：目录身份标记与当前环境不匹配，避免误删其它数据。路径：{paths.DataRoot}");
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"拒绝删除：无法校验环境身份标记。路径：{paths.DataRoot}",
                ex);
        }

        // Offload recursive delete so UI stays responsive on large profiles.
        await Task.Run(() => Directory.Delete(paths.DataRoot, recursive: true)).ConfigureAwait(true);
        _logger.Info(
            "PROFILE_DATA_DELETED",
            "环境数据目录已删除。",
            profile.Id,
            new { paths.DataRoot });
    }

    private async Task PrimaryActionAsync()
    {
        switch (_primaryMode)
        {
            case PrimaryActionMode.RefreshCodex:
                await RefreshCodexAsync(showDialogOnFailure: true).ConfigureAwait(true);
                break;
            case PrimaryActionMode.Activate:
                ActivateSelectedCodex();
                break;
            case PrimaryActionMode.EditProfile:
                await EditProfileAsync().ConfigureAwait(true);
                break;
            case PrimaryActionMode.OpenConfig:
                if (SelectedProfile is not null)
                {
                    OpenPath(SelectedProfile.ConfigPath);
                }
                break;
            case PrimaryActionMode.Launch:
                await LaunchSelectedAsync().ConfigureAwait(true);
                break;
        }
    }

    private async Task LaunchSelectedAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        IsBusy = true;
        var item = SelectedProfile;
        var childStarted = false;
        try
        {
            await ReconcileRuntimeAsync(item, persistStoppedReceipt: true).ConfigureAwait(true);
            if (item.RuntimeState == ProfileRuntimeState.Running)
            {
                ActivateSelectedCodex();
                return;
            }

            if (item.RuntimeState == ProfileRuntimeState.Unknown)
            {
                SetStatus(
                    "运行状态无法确认",
                    "为避免同一环境被重复启动，请先处理现有进程。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked);
                return;
            }

            var validation = await ProfilePathPolicy
                .PrepareAsync(item.Profile, _state.Profiles)
                .ConfigureAwait(true);
            if (!validation.IsValid)
            {
                ShowValidationFailure(validation);
                SetStatus(
                    "环境设置需要处理",
                    validation.Issues[0].Message,
                    "\uE7BA",
                    PrimaryActionMode.EditProfile,
                    string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Details)));
                return;
            }

            var paths = ProfilePaths.FromRoot(item.DataRoot);
            await ConfigIsolationAuditor.EnsureInitializedAsync(paths).ConfigureAwait(true);
            var aiLaunchConfiguration = await _aiSettingsService
                .ResolveLaunchConfigurationAsync(paths)
                .ConfigureAwait(true);
            var workingDirectory = string.IsNullOrWhiteSpace(item.WorkingDirectory)
                ? paths.DataRoot
                : item.WorkingDirectory;
            var isolation = await ConfigIsolationAuditor
                .AuditFileAsync(paths, workingDirectory)
                .ConfigureAwait(true);
            if (!isolation.IsIsolated)
            {
                ShowIsolationFailure(isolation, item.Profile.Id);
                SetStatus(
                    "配置需要处理",
                    isolation.Issues[0].Message,
                    "\uE7BA",
                    PrimaryActionMode.OpenConfig,
                    string.Join(Environment.NewLine, isolation.Issues.Select(issue => issue.Details)));
                return;
            }

            _installation ??= await _appLocator.ResolveAsync().ConfigureAwait(true);
            item.UpdateRuntime(ProfileRuntimeState.Launching, "正在准备运行副本");
            SetStatus(
                "正在准备 Codex 运行副本",
                "首次使用当前 Store 版本需要复制并校验本机运行文件；后续启动会直接复用。",
                "\uE895",
                PrimaryActionMode.Blocked);
            var runtimeInstallation = await _runtimeMirrorManager
                .EnsureMirrorAsync(_installation, _disposeCancellation.Token)
                .ConfigureAwait(true);
            item.UpdateRuntime(ProfileRuntimeState.Launching, "正在启动");
            SetStatus("正在启动 Codex", "正在提交启动意图并验证两层独立数据。", "\uE895", PrimaryActionMode.Blocked);

            var launchedUtc = DateTimeOffset.UtcNow;
            var launchId = Guid.NewGuid();
            var expectedIntentRevision = _state.Revision;
            var previousActiveInstance = item.Profile.ActiveInstance;
            var previousLastStartedUtc = item.Profile.LastStartedUtc;
            item.Profile.ActiveInstance = CreateLaunchIntent(
                item.Profile,
                runtimeInstallation,
                paths,
                launchId,
                launchedUtc);
            try
            {
                // Persist before the desktop-parent path creates a suspended child. Another launcher
                // with a stale revision must fail before it can start the same profile.
                await SaveStateAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                var resolution = await ResolveFailedLaunchIntentSaveAsync(
                        item,
                        launchId,
                        expectedIntentRevision,
                        previousActiveInstance,
                        previousLastStartedUtc)
                    .ConfigureAwait(true);
                if (resolution == LaunchIntentSaveResolution.CommitConfirmed)
                {
                    _logger.Warning(
                        "LAUNCH_INTENT_SAVE_CONFIRMED_AFTER_ERROR",
                        "启动意图保存返回错误，但重新读取已确认本次唯一 revision 持久化成功。",
                        item.Profile.Id,
                        new { Exception = ex.ToString(), launchId, expectedIntentRevision });
                }
                else
                {
                    var canRetry =
                        resolution == LaunchIntentSaveResolution.NotCommitted ||
                        (resolution == LaunchIntentSaveResolution.StateReloaded &&
                         SelectedProfile?.RuntimeState == ProfileRuntimeState.Ready);
                    if (canRetry)
                    {
                        item.UpdateRuntime(ProfileRuntimeState.Error, "启动未发生");
                    }
                    else if (resolution == LaunchIntentSaveResolution.Indeterminate)
                    {
                        item.UpdateRuntime(ProfileRuntimeState.Unknown, "启动意图待确认");
                    }

                    SetStatus(
                        canRetry ? "Codex 未启动" : "启动状态需要确认",
                        canRetry
                            ? resolution == LaunchIntentSaveResolution.StateReloaded
                                ? "环境列表已从磁盘重新加载，没有创建 Codex 进程；可以重试。"
                                : "已确认启动意图未写入，没有创建 Codex 进程；可以重试。"
                            : resolution == LaunchIntentSaveResolution.StateReloaded
                                ? "环境列表已从磁盘重新加载；请先处理当前显示的运行状态。"
                                : "无法确认启动意图是否写入；已阻止重复启动。",
                        canRetry ? "\uEA39" : "\uE7BA",
                        canRetry ? PrimaryActionMode.Launch : PrimaryActionMode.Blocked,
                        ex.ToString());
                    HandleOperationError(
                        "LAUNCH_INTENT_SAVE_FAILED",
                        "Codex 启动已阻止",
                        ex,
                        item.Profile.Id,
                        "无法保存启动状态。Codex 没有在未受控状态下继续启动。");
                    return;
                }
            }

            ProcessLaunchHandle launch;
            string? desktopLaunchDetails = null;
            var startInfo = CodexProcessLauncher.BuildStartInfo(
                item.Profile,
                runtimeInstallation,
                aiLaunchConfiguration);
            try
            {
                var compatibilityResult = await StartCompatibilityLaunchAsync(
                        item.Profile.Id,
                        startInfo,
                        item.Profile.ActiveInstance!,
                        () => childStarted = true)
                    .ConfigureAwait(true);
                launch = compatibilityResult.Launch;
                desktopLaunchDetails = compatibilityResult.Details;
            }
            catch
            {
                if (!childStarted)
                {
                    item.Profile.ActiveInstance = previousActiveInstance;
                    item.Profile.LastStartedUtc = previousLastStartedUtc;
                    try
                    {
                        await SaveStateAsync().ConfigureAwait(true);
                    }
                    catch (Exception rollbackException)
                    {
                        item.UpdateRuntime(ProfileRuntimeState.Unknown, "启动回滚待确认");
                        SetStatus(
                            "启动状态需要确认",
                            "Codex 未能启动，但启动意图回滚失败；已阻止再次启动。",
                            "\uE7BA",
                            PrimaryActionMode.Blocked,
                            rollbackException.ToString());
                        HandleOperationError(
                            "LAUNCH_INTENT_ROLLBACK_FAILED",
                            "启动状态回滚失败",
                            rollbackException,
                            item.Profile.Id);
                        return;
                    }
                }

                throw;
            }

            TrackProcess(item.Profile.Id, launch.Process, launch.Receipt.LaunchId);
            launch.Receipt.ObservedProcesses =
                _processInspector.CaptureProcessTree(CreateRootIdentity(launch.Receipt)).Identities.ToList();
            item.Profile.ActiveInstance = launch.Receipt;

            var verification = await _startupVerifier
                .VerifyAsync(launch.Process, launch.Receipt, paths, launchedUtc)
                .ConfigureAwait(true);
            var isCompatibilityLaunch = ProcessOwnershipModes.IsLegacy(launch.Receipt);
            var reportedVerificationDetails = desktopLaunchDetails is null
                ? verification.Details
                : $"{verification.Details}{Environment.NewLine}{Environment.NewLine}" +
                  $"桌面启动方式：{desktopLaunchDetails}";
            if (!verification.IsVerified)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "状态待确认");
                SetStatus(
                    verification.Message,
                    "已阻止重复启动；可尝试正常关闭当前实例。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    reportedVerificationDetails);
                _logger.Warning(
                    "STARTUP_ISOLATION_UNVERIFIED",
                    verification.Message,
                    item.Profile.Id,
                    new
                    {
                        Details = reportedVerificationDetails,
                        launch.Receipt.RootProcessId,
                        IsCompatibilityLaunch = isCompatibilityLaunch,
                    });
                return;
            }

            launch.Receipt.ObservedProcesses = WindowsProcessInspector
                .MergeIdentities(launch.Receipt.ObservedProcesses, verification.ObservedProcesses)
                .ToList();
            launch.Receipt.IsIsolationVerified = true;
            item.Profile.LastVerifiedCodexVersion = _installation.DisplayVersion;
            // User-facing "last started" records only launches that passed the
            // complete isolation gate; failed/pre-child attempts never advance it.
            item.Profile.LastStartedUtc = launchedUtc;
            item.UpdateRuntime(
                ProfileRuntimeState.Running,
                RunningDisplayStatus(launch.Receipt));
            item.RefreshProfileProperties();
            try
            {
                await SaveStateAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "验证记录待提交");
                SetStatus(
                    "隔离已验证，记录保存失败",
                    "为避免状态漂移，已阻止重复启动。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    ex.ToString());
                HandleOperationError("LAUNCH_VERIFICATION_SAVE_FAILED", "验证记录保存失败", ex, item.Profile.Id);
                return;
            }
            SetStatus(
                isCompatibilityLaunch ? "Codex 正在运行（桌面模式）" : "Codex 正在运行",
                isCompatibilityLaunch
                    ? "环境数据隔离已验证；进程由 Windows 桌面 shell 创建，不再依赖启动器所在的外层 Job。"
                    : "此环境的数据与其他环境分开保存。",
                "\uE768",
                PrimaryActionMode.Activate,
                reportedVerificationDetails);
            _logger.Info(
                isCompatibilityLaunch ? "LAUNCH_VERIFIED_DESKTOP_PARENT" : "LAUNCH_VERIFIED",
                isCompatibilityLaunch
                    ? "Codex 已在桌面父进程模式运行，运行副本与数据隔离写入均已确认。"
                    : "Codex 启动与隔离写入已确认。",
                item.Profile.Id,
                new
                {
                    launch.Receipt.RootProcessId,
                    launch.Receipt.CodexVersion,
                    launch.Receipt.CodexHomePath,
                    launch.Receipt.AppDataPath,
                    IsCompatibilityLaunch = isCompatibilityLaunch,
                });
        }
        catch (CodexRuntimeMirrorException ex)
        {
            item.UpdateRuntime(ProfileRuntimeState.Error, "运行副本准备失败");
            var details = $"Code: {ex.Code}{Environment.NewLine}{ex.Details}";
            SetStatus(
                "Codex 运行副本准备失败",
                ex.Message,
                "\uEA39",
                PrimaryActionMode.Launch,
                details);
            _dialogs.ShowError("无法准备 Codex 运行副本", ex.Message, details);
            HandleOperationError(
                ex.Code,
                "Codex 运行副本准备失败",
                ex,
                item.Profile.Id,
                ex.Details);
        }
        catch (CodexAppLocatorException ex)
        {
            SetCodexUnavailable(ex);
            _dialogs.ShowError("未找到 Codex", ex.Message, ex.Details);
        }
        catch (Exception ex)
        {
            if (childStarted || item.Profile.ActiveInstance is not null)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "启动状态待确认");
                SetStatus(
                    "Codex 启动状态需要确认",
                    "检测到已提交的启动状态；已阻止重复启动。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    ex.ToString());
            }
            else
            {
                item.UpdateRuntime(ProfileRuntimeState.Error, "启动失败");
                SetStatus("Codex 启动失败", ex.Message, "\uEA39", PrimaryActionMode.Launch, ex.ToString());
            }

            HandleOperationError("LAUNCH_FAILED", "Codex 启动失败", ex, item.Profile.Id);
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private static RunningInstanceReceipt CreateLaunchIntent(
        CodexProfile profile,
        CodexInstallation installation,
        ProfilePaths paths,
        Guid launchId,
        DateTimeOffset launchedUtc) =>
        new()
        {
            ProfileId = profile.Id,
            LaunchId = launchId,
            OwnershipMode = ProcessOwnershipModes.LegacyProcessTree,
            OwnershipVersion = ProcessOwnershipModes.LegacyProcessTreeVersion,
            WindowsSessionId = -1,
            IsLaunchPending = true,
            RootProcessId = 0,
            ProcessStartUtcTicks = 0,
            ExecutablePath = PathUtilities.Normalize(installation.ExecutablePath),
            CodexVersion = installation.DisplayVersion,
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
            LaunchedUtc = launchedUtc,
        };

    internal static bool ShouldUseCompatibilityLaunch(
        WindowsJobObjectException exception) =>
        exception.Code.Equals(
            "JOB_BROKER_BREAKAWAY_INCOMPLETE",
            StringComparison.Ordinal) ||
        exception.Code.Equals(
            "PROCESS_CREATE_SUSPENDED_ACCESS_DENIED",
            StringComparison.Ordinal);

    internal static void InitializeCompatibilityLaunchIntent(
        RunningInstanceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.OwnershipMode = ProcessOwnershipModes.LegacyProcessTree;
        receipt.OwnershipVersion = ProcessOwnershipModes.LegacyProcessTreeVersion;
        receipt.LaunchPhase = string.Empty;
        receipt.JobObjectName = string.Empty;
        receipt.ReadyEventName = string.Empty;
        receipt.WindowsSessionId = -1;
        receipt.BrokerProcessId = 0;
        receipt.BrokerProcessStartUtcTicks = 0;
        receipt.IsLaunchPending = true;
        receipt.IsIsolationVerified = false;
        receipt.RootProcessId = 0;
        receipt.ProcessStartUtcTicks = 0;
        receipt.ObservedProcesses = [];
    }

    private static string RunningDisplayStatus(RunningInstanceReceipt receipt) =>
        ProcessOwnershipModes.IsLegacy(receipt)
            ? "运行中（桌面模式）"
            : "运行中";

    private async Task<(ProcessLaunchHandle Launch, string Details)> StartCompatibilityLaunchAsync(
        Guid profileId,
        ProcessStartInfo startInfo,
        RunningInstanceReceipt receipt,
        Action markChildStarted)
    {
        if (!ProcessOwnershipModes.IsLegacy(receipt) ||
            !receipt.IsLaunchPending ||
            receipt.RootProcessId != 0 ||
            receipt.ProcessStartUtcTicks != 0)
        {
            throw new InvalidOperationException(
                "桌面父进程启动要求已持久化且尚未创建 root 的兼容模式启动意图。");
        }

        var compatibility = _jobManager.StartDesktopParentCompatibilityProcess(startInfo);
        markChildStarted();
        try
        {
            receipt.IsLaunchPending = false;
            receipt.RootProcessId = compatibility.Identity.ProcessId;
            receipt.ProcessStartUtcTicks = compatibility.Identity.ProcessStartUtcTicks;
            receipt.ExecutablePath = PathUtilities.Normalize(
                compatibility.Identity.ExecutablePath);
            receipt.ObservedProcesses =
            [
                new ObservedProcessIdentity
                {
                    ProcessId = compatibility.Identity.ProcessId,
                    ProcessStartUtcTicks = compatibility.Identity.ProcessStartUtcTicks,
                    ExecutablePath = compatibility.Identity.ExecutablePath,
                },
            ];
            await SaveStateAsync().ConfigureAwait(true);
        }
        catch
        {
            // Disposing the managed wrapper does not terminate the created app.
            // The durable pending intent remains available for argv-based recovery.
            compatibility.Process.Dispose();
            throw;
        }

        var details = compatibility.Details;
        try
        {
            _logger.Info(
                "DESKTOP_PARENT_LAUNCH",
                "Codex 已从校验后的本地运行副本通过桌面父进程启动。",
                profileId,
                new
                {
                    compatibility.Details,
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is diagnostic, not an ownership transaction. Preserve the
            // failure in user-visible details without orphaning a recorded process.
            details += $"{Environment.NewLine}桌面模式日志写入失败：{ex}";
        }

        return (new(compatibility.Process, receipt), details);
    }

    private void TrackProcess(Guid profileId, Process process, Guid launchId)
    {
        if (_ownedProcesses.Remove(profileId, out var previous) &&
            !ReferenceEquals(previous.Process, process))
        {
            previous.Process.Dispose();
        }

        var tracked = new TrackedProcess(launchId, process);
        _ownedProcesses[profileId] = tracked;
        process.Exited += (_, _) =>
            Application.Current.Dispatcher.InvokeAsync(
                () => HandleOwnedProcessExitedAsync(profileId, launchId));
        try
        {
            process.EnableRaisingEvents = true;
        }
        catch
        {
            if (_ownedProcesses.TryGetValue(profileId, out var current) &&
                ReferenceEquals(current.Process, process))
            {
                _ownedProcesses.Remove(profileId);
            }

            process.Dispose();
            throw;
        }
    }

    private void ActivateSelectedCodex()
    {
        if (SelectedProfile?.Profile.ActiveInstance is not { } receipt)
        {
            return;
        }

        if (ProcessOwnershipModes.IsWindowsJob(receipt))
        {
            ActivateJobBackedCodex(SelectedProfile, receipt);
            return;
        }

        var check = ProcessReceiptVerifier.Check(receipt);
        using var process = check.Process;
        if (check.Status != ProcessReceiptStatus.VerifiedRunning || process is null)
        {
            SetStatus("运行状态无法确认", check.Message, "\uE7BA", PrimaryActionMode.Blocked, check.Details);
            return;
        }

        if (!_windowController.Activate(process))
        {
            SetStatus(
                "Codex 正在运行",
                "没有找到可激活的窗口。",
                "\uE7BA",
                PrimaryActionMode.Blocked,
                $"PID={process.Id}");
        }
    }

    private void ActivateJobBackedCodex(
        ProfileItemViewModel item,
        RunningInstanceReceipt receipt)
    {
        if (!JobReceiptRecoveryPolicy.CanUseInteractiveWindowControl(
                receipt.WindowsSessionId,
                _jobManager.CurrentWindowsSessionId))
        {
            SetStatus(
                "Codex 在另一 Windows 会话中运行",
                "当前会话不能打开它的窗口；仍可在确认后关闭此环境的 Codex。",
                "\uE7BA",
                PrimaryActionMode.Blocked,
                $"ReceiptSession={receipt.WindowsSessionId}, CurrentSession={_jobManager.CurrentWindowsSessionId}。");
            return;
        }

        if (!TryGetJobNames(item.Profile, receipt, out var names, out var nameDetails))
        {
            SetStatus("运行状态无法确认", "运行记录与当前环境不一致。", "\uE7BA", PrimaryActionMode.Blocked, nameDetails);
            return;
        }

        try
        {
            var ownership = _jobManager.InspectOwnership(names);
            var ownershipErrors = GetOwnershipInspectionErrors(ownership);
            var brokerVerified = _jobManager.VerifyBrokerIdentity(receipt, out var brokerDetails);
            if (ownershipErrors.Count != 0 ||
                !ownership.Job.Exists ||
                !ownership.Job.KillOnJobClose ||
                !ownership.ReadyEvent.Exists ||
                !ownership.ReadyEvent.IsSignaled ||
                !ownership.CancelEvent.Exists ||
                !brokerVerified)
            {
                SetStatus(
                    "运行状态无法确认",
                    "无法确认此环境的进程归属。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    string.Join(Environment.NewLine, ownershipErrors.Append(brokerDetails)));
                return;
            }

            var check = ProcessReceiptVerifier.Check(receipt);
            using var process = check.Process;
            var paths = ProfilePaths.FromRoot(item.DataRoot);
            var membershipVerified = _jobManager.VerifyMembership(receipt, out var membershipDetails);
            var argumentsVerified = _processInspector.VerifyProfileRootArguments(
                receipt.RootProcessId,
                paths.AppData,
                out var argumentDetails);
            if (check.Status != ProcessReceiptStatus.VerifiedRunning ||
                process is null ||
                !membershipVerified ||
                !argumentsVerified)
            {
                SetStatus(
                    "运行状态无法确认",
                    "运行记录、启动参数或进程归属不匹配。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    string.Join(Environment.NewLine, check.Details, membershipDetails, argumentDetails));
                return;
            }

            if (!_windowController.Activate(process))
            {
                SetStatus(
                    "Codex 正在运行",
                    "没有找到可激活的窗口。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    $"PID={process.Id}");
            }
        }
        catch (Exception ex) when (ex is WindowsJobObjectException or ArgumentException or Win32Exception)
        {
            SetStatus(
                "运行状态无法确认",
                "无法核验此环境的进程归属。",
                "\uE7BA",
                PrimaryActionMode.Blocked,
                ex is WindowsJobObjectException jobError
                    ? $"{jobError.Message} {jobError.Details}"
                    : ex.ToString());
        }
    }

    private async Task CloseJobBackedCodexAsync(
        ProfileItemViewModel item,
        RunningInstanceReceipt receipt)
    {
        if (!TryGetJobNames(item.Profile, receipt, out var names, out var nameDetails))
        {
            SetStatus("无法安全关闭", "运行记录与当前环境不一致。", "\uE7BA", PrimaryActionMode.Blocked, nameDetails);
            return;
        }

        WindowsJobOwnershipInspection ownership;
        try
        {
            ownership = _jobManager.InspectOwnership(names);
        }
        catch (Exception ex) when (ex is WindowsJobObjectException or ArgumentException or Win32Exception)
        {
            SetStatus("无法安全关闭", "无法读取此环境的进程归属信息。", "\uE7BA", PrimaryActionMode.Blocked, ex.ToString());
            return;
        }

        var ownershipErrors = GetOwnershipInspectionErrors(ownership);
        if (ownershipErrors.Count != 0)
        {
            SetStatus(
                "无法安全关闭",
                "无法完整核验此环境的进程归属。",
                "\uE7BA",
                PrimaryActionMode.Blocked,
                string.Join(Environment.NewLine, ownershipErrors));
            return;
        }

        if (!ownership.Job.Exists)
        {
            await ReconcileJobRuntimeAsync(
                    item,
                    ProfilePaths.FromRoot(item.DataRoot),
                    receipt,
                    persist: true)
                .ConfigureAwait(true);
            if (item.Profile.ActiveInstance is not null)
            {
                SetStatus(
                    "关闭状态待确认",
                    "运行记录缺少必要的归属证据，正在安全确认当前状态。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked);
            }

            return;
        }

        if (!ownership.Job.KillOnJobClose ||
            !ownership.ReadyEvent.Exists ||
            !ownership.ReadyEvent.IsSignaled ||
            !ownership.CancelEvent.Exists)
        {
            SetStatus(
                "无法安全关闭",
                "运行记录缺少安全关闭所需的归属证据。",
                "\uE7BA",
                PrimaryActionMode.Blocked);
            return;
        }

        if (ownership.Job.IsEmpty)
        {
            _ = await CompleteJobStopIfEmptyAsync(item, receipt).ConfigureAwait(true);
            return;
        }

        var paths = ProfilePaths.FromRoot(item.DataRoot);
        var sameSession = JobReceiptRecoveryPolicy.CanUseInteractiveWindowControl(
            receipt.WindowsSessionId,
            _jobManager.CurrentWindowsSessionId);
        var rootCheck = ProcessReceiptVerifier.Check(receipt);
        using var rootProcess = rootCheck.Process;
        var canGracefullyClose =
            sameSession &&
            rootCheck.Status == ProcessReceiptStatus.VerifiedRunning &&
            rootProcess is not null &&
            _jobManager.VerifyBrokerIdentity(receipt, out _) &&
            _jobManager.VerifyMembership(receipt, out _) &&
            _processInspector.VerifyProfileRootArguments(rootProcess.Id, paths.AppData, out _);

        if (canGracefullyClose)
        {
            if (!_dialogs.Confirm(
                    "关闭 Codex",
                    "将关闭此环境的 Codex。未保存的输入可能丢失。",
                    confirmText: "关闭 Codex"))
            {
                return;
            }

            // The dialog may remain open indefinitely. Re-check every
            // ownership fact before posting WM_CLOSE.
            var confirmedOwnership = _jobManager.InspectOwnership(names);
            var confirmedCheck = ProcessReceiptVerifier.Check(receipt);
            using var confirmedProcess = confirmedCheck.Process;
            var confirmed =
                confirmedOwnership.Job.Exists &&
                confirmedOwnership.Job.KillOnJobClose &&
                confirmedOwnership.Job.ProcessIds.Contains(receipt.RootProcessId) &&
                GetOwnershipInspectionErrors(confirmedOwnership).Count == 0 &&
                confirmedCheck.Status == ProcessReceiptStatus.VerifiedRunning &&
                confirmedProcess is not null &&
                _jobManager.VerifyBrokerIdentity(receipt, out _) &&
                _jobManager.VerifyMembership(receipt, out _) &&
                _processInspector.VerifyProfileRootArguments(confirmedProcess.Id, paths.AppData, out _);
            if (!confirmed)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "确认期间状态已变化");
                SetStatus(
                    "关闭已取消",
                    "确认期间运行状态发生变化。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked);
                return;
            }

            IsBusy = true;
            try
            {
                item.UpdateRuntime(ProfileRuntimeState.Launching, "正在关闭");
                SetStatus("正在关闭 Codex", "正在等待应用与后台进程退出。", "\uE895", PrimaryActionMode.Blocked);
                var windowCount = _windowController.RequestClose(confirmedProcess!);
                ReceiptJobOperationResult? gracefulResult = null;
                if (windowCount > 0)
                {
                    gracefulResult = await _jobManager
                        .ConfirmVerifiedReceiptStableEmptyAsync(receipt, TimeSpan.FromSeconds(20))
                        .ConfigureAwait(true);
                }

                if (gracefulResult?.Succeeded == true)
                {
                    await ClearReceiptIfMatchesAsync(item.Profile.Id, receipt.LaunchId).ConfigureAwait(true);
                    item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
                    SetStatus("可以启动 Codex", "该环境的 Codex 已完全退出。", "\uE73E", PrimaryActionMode.Launch);
                    _logger.Info(
                        "JOB_STOP_VERIFIED",
                        "Codex Windows Job 已正常关闭并连续稳定为空。",
                        item.Profile.Id,
                        new { receipt.RootProcessId, Force = false });
                    return;
                }

                if (gracefulResult is not null)
                {
                    _logger.Warning(
                        gracefulResult.Code,
                        "正常关闭后未取得 pinned Job 稳定空证据。",
                        item.Profile.Id,
                        new { gracefulResult.Details, gracefulResult.VerifiedMemberProcessIds });
                }
            }
            catch (WindowsJobObjectException ex)
            {
                _logger.Warning(ex.Code, ex.Message, item.Profile.Id, new { ex.Details });
            }
            finally
            {
                IsBusy = false;
            }
        }

        var latest = _jobManager.Inspect(receipt.JobObjectName);
        var brokerStillVerified = _jobManager.VerifyBrokerIdentity(receipt, out var forceBrokerDetails);
        if (!latest.Exists || latest.InspectionErrors.Count != 0 || !brokerStillVerified)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "进程归属待确认");
            SetStatus(
                "无法安全结束",
                "强制关闭前无法重新核验此环境的进程归属。",
                "\uE7BA",
                PrimaryActionMode.Blocked,
                string.Join(Environment.NewLine, latest.InspectionErrors.Append(forceBrokerDetails)));
            return;
        }

        var crossSessionReason = sameSession
            ? "Codex 未能正常退出。"
            : "该 Codex 运行在另一个 Windows 会话，无法请求正常关闭。";
        if (!_dialogs.Confirm(
                "强制关闭 Codex",
                $"{crossSessionReason} 强制关闭将结束此环境仍在运行的 {latest.Members.Count} 个相关进程，未保存的输入会丢失。",
                confirmText: "强制关闭",
                cancelText: "返回",
                isDestructive: true))
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "等待关闭");
            return;
        }

        IsBusy = true;
        try
        {
            var confirmedJob = _jobManager.Inspect(receipt.JobObjectName);
            var confirmedBrokerVerified = _jobManager.VerifyBrokerIdentity(
                receipt,
                out var confirmedBrokerDetails);
            if (!confirmedJob.Exists ||
                !confirmedJob.KillOnJobClose ||
                confirmedJob.InspectionErrors.Count != 0 ||
                !confirmedBrokerVerified)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "确认期间状态已变化");
                SetStatus(
                    "强制结束已取消",
                    "确认期间运行状态发生变化。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    string.Join(Environment.NewLine, confirmedJob.InspectionErrors.Append(confirmedBrokerDetails)));
                return;
            }

            var termination = await _jobManager
                .TerminateVerifiedReceiptAndWaitForStableEmptyAsync(receipt, TimeSpan.FromSeconds(20))
                .ConfigureAwait(true);
            if (!termination.Succeeded)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "关闭状态待确认");
                SetStatus(
                    "仍有进程未确认退出",
                    "强制关闭后仍未确认所有相关进程退出。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    $"{termination.Code}: {termination.Details}");
                return;
            }

            await ClearReceiptIfMatchesAsync(item.Profile.Id, receipt.LaunchId).ConfigureAwait(true);
            item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
            SetStatus("可以启动 Codex", "该环境的 Codex 已完全关闭。", "\uE73E", PrimaryActionMode.Launch);
            _logger.Warning(
                "JOB_STOP_FORCED_VERIFIED",
                "用户确认后已通过 TerminateJobObject 结束整个 Codex Job。",
                item.Profile.Id,
                new
                {
                    receipt.RootProcessId,
                    ProcessIds = termination.VerifiedMemberProcessIds,
                    Force = true,
                });
        }
        catch (Exception ex)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "关闭失败");
            SetStatus("Codex 关闭失败", "未能确认此环境的相关进程全部退出。", "\uEA39", PrimaryActionMode.Blocked, ex.ToString());
            HandleOperationError(
                "JOB_STOP_FAILED",
                "Codex 关闭失败",
                ex,
                item.Profile.Id,
                "未能确认此环境的相关进程全部退出；已保留运行记录并阻止重复启动。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CloseCodexAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var item = SelectedProfile;
        await ReconcileRuntimeAsync(item, persistStoppedReceipt: true).ConfigureAwait(true);
        if (item.Profile.ActiveInstance is not { } receipt)
        {
            UpdateStatusForSelected();
            return;
        }

        if (ProcessOwnershipModes.IsWindowsJob(receipt) && receipt.IsLaunchPending)
        {
            SetStatus(
                "启动状态仍在恢复",
                "尚未取得可安全关闭或重新启动所需的完整证据。",
                "\uE7BA",
                PrimaryActionMode.Blocked);
            return;
        }

        if (ProcessOwnershipModes.IsWindowsJob(receipt))
        {
            await CloseJobBackedCodexAsync(item, receipt).ConfigureAwait(true);
            return;
        }

        var paths = ProfilePaths.FromRoot(item.DataRoot);
        var initialCheck = ProcessReceiptVerifier.Check(receipt);
        if (initialCheck.Status == ProcessReceiptStatus.Stopped)
        {
            initialCheck.Process?.Dispose();
            await ForceTerminateObservedAsync(
                    item,
                    receipt,
                    "Codex 根进程已经退出，但仍检测到经过身份核验的后代进程。")
                .ConfigureAwait(true);
            return;
        }

        using var initialProcess = initialCheck.Process;
        if (initialCheck.Status != ProcessReceiptStatus.VerifiedRunning || initialProcess is null)
        {
            SetStatus("无法安全关闭", initialCheck.Message, "\uE7BA", PrimaryActionMode.Blocked, initialCheck.Details);
            _dialogs.ShowError("无法安全关闭", initialCheck.Message, initialCheck.Details);
            return;
        }

        if (!_processInspector.VerifyProfileRootArguments(initialProcess.Id, paths.AppData, out var argumentDetails))
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "启动参数不匹配");
            SetStatus("无法安全关闭", "进程参数与此环境不匹配。", "\uE7BA", PrimaryActionMode.Blocked, argumentDetails);
            _dialogs.ShowError("无法安全关闭", "进程参数与此环境不匹配。", argumentDetails);
            return;
        }

        if (!_dialogs.Confirm(
                "关闭 Codex",
                "将关闭此环境的 Codex。未保存的输入可能丢失。",
                confirmText: "关闭 Codex"))
        {
            return;
        }

        // A confirmation dialog may remain open indefinitely. Do not reuse the
        // pre-confirmation Process object for any side effect.
        var confirmedCheck = ProcessReceiptVerifier.Check(receipt);
        using var confirmedProcess = confirmedCheck.Process;
        var confirmedArgumentDetails = confirmedCheck.Details;
        var confirmedArgumentsMatch =
            confirmedCheck.Status == ProcessReceiptStatus.VerifiedRunning &&
            confirmedProcess is not null &&
            _processInspector.VerifyProfileRootArguments(
                confirmedProcess.Id,
                paths.AppData,
                out confirmedArgumentDetails);
        if (!confirmedArgumentsMatch)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "确认期间状态已变化");
            SetStatus(
                "关闭已取消",
                "确认期间进程身份发生变化，未向任何窗口发送关闭消息。",
                "\uE7BA",
                PrimaryActionMode.Blocked,
                confirmedArgumentDetails);
            return;
        }

        var processToClose = confirmedProcess!;

        IsBusy = true;
        try
        {
            var treeInspection = _processInspector.CaptureProcessTree(CreateRootIdentity(receipt));
            if (treeInspection.InspectionErrors.Count > 0)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "进程树检查失败");
                SetStatus(
                    "关闭已阻止",
                    "无法完整核验当前 Codex 进程树。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    string.Join(Environment.NewLine, treeInspection.InspectionErrors));
                return;
            }

            receipt.ObservedProcesses = WindowsProcessInspector
                .MergeIdentities(
                    receipt.ObservedProcesses,
                    treeInspection.Identities)
                .ToList();
            await SaveStateAsync().ConfigureAwait(true);

            item.UpdateRuntime(ProfileRuntimeState.Launching, "正在关闭");
            SetStatus("正在关闭 Codex", "正在等待应用与后台进程退出。", "\uE895", PrimaryActionMode.Blocked);
            var windowCount = _windowController.RequestClose(processToClose);
            var exited = windowCount > 0 && await _windowController
                .WaitForExitAsync(processToClose, TimeSpan.FromSeconds(20))
                .ConfigureAwait(true);

            var postGracefulTree = await RefreshObservedProcessTreeAsync(
                    item,
                    receipt,
                    persist: true)
                .ConfigureAwait(true);
            if (postGracefulTree is null)
            {
                SetStatus(
                    "关闭状态待确认",
                    "正常关闭等待结束后无法完整重新发现 Codex 进程树。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked);
                return;
            }

            if (exited && await CompleteStopIfNoProcessesAsync(item, receipt).ConfigureAwait(true))
            {
                _logger.Info("STOP_VERIFIED", "Codex 根进程和已观察到的后代均已退出。", item.Profile.Id, new
                {
                    receipt.RootProcessId,
                    Force = false,
                });
                return;
            }

            item.UpdateRuntime(ProfileRuntimeState.Unknown, "等待关闭");
            var reason = windowCount == 0
                ? "没有找到可关闭的窗口。"
                : "正常关闭请求发出 20 秒后进程仍未完全退出。";
            _ = await ForceTerminateObservedAsync(item, receipt, reason, manageBusyState: false)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "关闭失败");
            SetStatus("Codex 关闭失败", ex.Message, "\uEA39", PrimaryActionMode.Blocked, ex.ToString());
            HandleOperationError("STOP_FAILED", "Codex 关闭失败", ex, item.Profile.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ForceTerminateObservedAsync(
        ProfileItemViewModel item,
        RunningInstanceReceipt receipt,
        string reason,
        bool manageBusyState = true)
    {
        var preForceTree = await RefreshObservedProcessTreeAsync(item, receipt, persist: true)
            .ConfigureAwait(true);
        if (preForceTree is null)
        {
            SetStatus(
                "无法安全结束残留进程",
                "强制结束前无法完整重新发现 Codex 进程树。",
                "\uE7BA",
                PrimaryActionMode.Blocked);
            return false;
        }

        var liveCheck = _processInspector.FindLiveIdentities(receipt.ObservedProcesses);
        if (liveCheck.InspectionErrors.Count > 0)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "残留进程检查失败");
            SetStatus(
                "无法安全结束残留进程",
                "至少一个持久化进程身份无法重新核验。",
                "\uE7BA",
                PrimaryActionMode.Blocked,
                string.Join(Environment.NewLine, liveCheck.InspectionErrors));
            return false;
        }

        if (liveCheck.LiveIdentities.Count == 0)
        {
            return await CompleteStopIfNoProcessesAsync(item, receipt).ConfigureAwait(true);
        }

        if (!_dialogs.Confirm(
                "强制关闭 Codex",
                $"Codex 未能正常退出。强制关闭将结束此环境仍在运行的 {liveCheck.LiveIdentities.Count} 个相关进程，未保存的输入会丢失。",
                confirmText: "强制关闭",
                cancelText: "返回",
                isDestructive: true))
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "等待关闭");
            SetStatus(
                "Codex 尚未退出",
                "已保留运行记录并阻止重复启动。",
                "\uE7BA",
                PrimaryActionMode.Blocked,
                reason);
            return false;
        }

        var previousBusy = IsBusy;
        if (manageBusyState)
        {
            IsBusy = true;
        }

        try
        {
            var terminatedProcessIds = new HashSet<int>();
            var terminationErrors = new List<string>();
            var rootIdentity = CreateRootIdentity(receipt);

            // The root is intentionally terminated after its known children.
            // Re-scan after every pass so any child it respawned before its own
            // termination is discovered and handled in the next bounded pass.
            for (var pass = 0; pass < 3; pass++)
            {
                var currentTree = await RefreshObservedProcessTreeAsync(item, receipt, persist: true)
                    .ConfigureAwait(true);
                if (currentTree is null)
                {
                    SetStatus(
                        "无法安全结束残留进程",
                        "强制结束过程中无法完整重新发现 Codex 进程树。",
                        "\uE7BA",
                        PrimaryActionMode.Blocked);
                    return false;
                }

                var currentLive = _processInspector.FindLiveIdentities(receipt.ObservedProcesses);
                if (currentLive.InspectionErrors.Count > 0)
                {
                    SetStatus(
                        "无法安全结束残留进程",
                        "至少一个持久化进程身份无法重新核验。",
                        "\uE7BA",
                        PrimaryActionMode.Blocked,
                        string.Join(Environment.NewLine, currentLive.InspectionErrors));
                    return false;
                }

                if (currentLive.LiveIdentities.Count == 0)
                {
                    break;
                }

                var termination = _processInspector.TerminateVerifiedIdentities(
                    rootIdentity,
                    receipt.ObservedProcesses);
                terminatedProcessIds.UnionWith(termination.TerminatedProcessIds);
                terminationErrors.AddRange(termination.InspectionErrors);
                var observedAfterTermination = WindowsProcessInspector.MergeIdentities(
                    receipt.ObservedProcesses,
                    termination.ObservedIdentities);
                if (!IdentitySetsEqual(receipt.ObservedProcesses, observedAfterTermination))
                {
                    receipt.ObservedProcesses = observedAfterTermination.ToList();
                    await SaveStateAsync().ConfigureAwait(true);
                }

                if (!CanContinueAfterTermination(termination))
                {
                    SetStatus(
                        "强制结束状态待确认",
                        "完整进程所有权租约或终止检查失败，已保留运行记录。",
                        "\uE7BA",
                        PrimaryActionMode.Blocked,
                        string.Join(Environment.NewLine, termination.InspectionErrors));
                    return false;
                }

                await Task.Delay(250).ConfigureAwait(true);
            }

            var postForceTree = await RefreshObservedProcessTreeAsync(item, receipt, persist: true)
                .ConfigureAwait(true);
            if (postForceTree is null)
            {
                SetStatus(
                    "仍有进程未确认",
                    "强制结束后无法完整重新发现 Codex 进程树。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked);
                return false;
            }

            await Task.Delay(500).ConfigureAwait(true);
            if (!await CompleteStopIfNoProcessesAsync(item, receipt).ConfigureAwait(true))
            {
                SetStatus(
                    "仍有进程未退出",
                    "已保留运行记录并阻止重复启动。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked,
                    string.Join(Environment.NewLine, terminationErrors));
                return false;
            }

            _logger.Warning(
                "STOP_FORCED_VERIFIED",
                "用户确认后已逐一核验并结束 Codex 进程，复查无残留。",
                item.Profile.Id,
                new
                {
                    receipt.RootProcessId,
                    TerminatedProcessIds = terminatedProcessIds.OrderBy(processId => processId).ToArray(),
                    InspectionErrors = terminationErrors,
                });
            return true;
        }
        finally
        {
            if (manageBusyState)
            {
                IsBusy = previousBusy;
            }
        }
    }

    private async Task HandleOwnedProcessExitedAsync(Guid profileId, Guid launchId)
    {
        try
        {
            var item = Profiles.FirstOrDefault(profile => profile.Profile.Id == profileId);
            if (item?.Profile.ActiveInstance?.LaunchId != launchId)
            {
                return;
            }

            if (_ownedProcesses.TryGetValue(profileId, out var tracked) &&
                tracked.LaunchId == launchId)
            {
                _ownedProcesses.Remove(profileId);
                tracked.Process.Dispose();
            }

            await Task.Delay(750).ConfigureAwait(true);
            if (item.Profile.ActiveInstance?.LaunchId == launchId)
            {
                await ReconcileRuntimeAsync(item, persistStoppedReceipt: true).ConfigureAwait(true);
                if (SelectedProfile == item)
                {
                    UpdateStatusForSelected();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("PROCESS_EXIT_RECONCILE_FAILED", ex.Message, ex, profileId);
        }
    }

    private async Task<ProcessTreeInspectionResult?> RefreshObservedProcessTreeAsync(
        ProfileItemViewModel item,
        RunningInstanceReceipt receipt,
        bool persist)
    {
        var treeInspection = _processInspector.CaptureProcessTree(CreateRootIdentity(receipt));
        if (treeInspection.InspectionErrors.Count > 0)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "进程树检查失败");
            return null;
        }

        var observed = WindowsProcessInspector.MergeIdentities(
            receipt.ObservedProcesses,
            treeInspection.Identities);
        var changed = !IdentitySetsEqual(receipt.ObservedProcesses, observed);
        receipt.ObservedProcesses = observed.ToList();
        if (changed && persist)
        {
            await SaveStateAsync().ConfigureAwait(true);
        }

        return treeInspection;
    }

    private async Task<bool> CompleteStopIfNoProcessesAsync(
        ProfileItemViewModel item,
        RunningInstanceReceipt receipt)
    {
        var paths = ProfilePaths.FromRoot(item.DataRoot);
        var emptySnapshots = new List<bool>(capacity: 2);
        for (var snapshot = 0; snapshot < 2; snapshot++)
        {
            var finalTree = await RefreshObservedProcessTreeAsync(item, receipt, persist: true)
                .ConfigureAwait(true);
            if (finalTree is null)
            {
                return false;
            }

            var discovery = DiscoverProfileProcesses(paths, receipt);
            var liveIdentityCheck = _processInspector.FindLiveIdentities(receipt.ObservedProcesses);
            var isEmpty = IsStopSnapshotEmpty(finalTree, discovery, liveIdentityCheck);
            emptySnapshots.Add(isEmpty);
            if (!isEmpty)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "残留进程待确认");
                return false;
            }

            if (snapshot == 0)
            {
                await Task.Delay(350).ConfigureAwait(true);
            }
        }

        if (!HasStableEmptyStopSnapshots(emptySnapshots))
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "停止状态尚未稳定");
            return false;
        }

        await ClearReceiptIfMatchesAsync(item.Profile.Id, receipt.LaunchId).ConfigureAwait(true);
        item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
        if (SelectedProfile == item)
        {
            SetStatus("可以启动 Codex", "将使用此环境的独立数据启动。", "\uE73E", PrimaryActionMode.Launch);
        }

        return true;
    }

    private async Task ClearReceiptIfMatchesAsync(Guid profileId, Guid launchId)
    {
        var profile = _state.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile?.ActiveInstance?.LaunchId != launchId)
        {
            return;
        }

        profile.ActiveInstance = null;
        await SaveStateAsync().ConfigureAwait(true);
    }

    private async Task ReconcileRuntimeAsync(
        ProfileItemViewModel item,
        bool persistStoppedReceipt)
    {
        var paths = ProfilePaths.FromRoot(item.DataRoot);
        var receipt = item.Profile.ActiveInstance;
        if (receipt is not null && ProcessOwnershipModes.IsWindowsJob(receipt))
        {
            await ReconcileJobRuntimeAsync(item, paths, receipt, persistStoppedReceipt)
                .ConfigureAwait(true);
            return;
        }

        var discovery = DiscoverProfileProcesses(paths, receipt);

        if (discovery.Matches.Count > 1)
        {
            await EnsureBlockingIntentAsync(item, paths, receipt, persistStoppedReceipt).ConfigureAwait(true);
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "发现多个实例");
            return;
        }

        if (receipt is null || receipt.IsLaunchPending)
        {
            if (discovery.Matches.Count == 1)
            {
                await RecoverReceiptAsync(item, paths, discovery.Matches[0], persistStoppedReceipt)
                    .ConfigureAwait(true);
                return;
            }

            if (discovery.InspectionErrors.Count > 0)
            {
                await EnsureBlockingIntentAsync(item, paths, receipt, persistStoppedReceipt).ConfigureAwait(true);
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "进程检查失败");
                return;
            }

            if (receipt is not null &&
                DateTimeOffset.UtcNow - receipt.LaunchedUtc < TimeSpan.FromMinutes(2))
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "启动意图待恢复");
                return;
            }

            if (receipt is not null)
            {
                item.Profile.ActiveInstance = null;
                if (persistStoppedReceipt)
                {
                    await SaveStateAsync().ConfigureAwait(true);
                }
            }

            item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
            return;
        }

        var check = ProcessReceiptVerifier.Check(receipt);
        switch (check.Status)
        {
            case ProcessReceiptStatus.Stopped:
                check.Process?.Dispose();
                if (discovery.Matches.Count == 1)
                {
                    await RecoverReceiptAsync(item, paths, discovery.Matches[0], persistStoppedReceipt)
                        .ConfigureAwait(true);
                    return;
                }

                if (!await CompleteStopIfNoProcessesAsync(item, receipt).ConfigureAwait(true))
                {
                    return;
                }

                break;
            case ProcessReceiptStatus.VerifiedRunning:
                if (!_processInspector.VerifyProfileRootArguments(
                        receipt.RootProcessId,
                        paths.AppData,
                        out _))
                {
                    check.Process?.Dispose();
                    item.UpdateRuntime(ProfileRuntimeState.Unknown, "启动参数不匹配");
                    return;
                }

                if (check.Process is { } verifiedProcess)
                {
                    var treeInspection = _processInspector.CaptureProcessTree(CreateRootIdentity(receipt));
                    var currentTree = treeInspection.Identities;
                    var observed = WindowsProcessInspector.MergeIdentities(
                        receipt.ObservedProcesses,
                        currentTree);
                    var changed = !IdentitySetsEqual(receipt.ObservedProcesses, observed);
                    receipt.ObservedProcesses = observed.ToList();
                    TrackProcess(item.Profile.Id, verifiedProcess, receipt.LaunchId);

                    if (treeInspection.InspectionErrors.Count > 0)
                    {
                        receipt.IsIsolationVerified = false;
                        item.UpdateRuntime(ProfileRuntimeState.Unknown, "进程树检查失败");
                        if (persistStoppedReceipt)
                        {
                            await SaveStateAsync().ConfigureAwait(true);
                        }

                        return;
                    }

                    if (!receipt.IsIsolationVerified)
                    {
                        await VerifyRecoveredIsolationAsync(
                                item,
                                paths,
                                receipt,
                                verifiedProcess,
                                persistStoppedReceipt)
                            .ConfigureAwait(true);
                        return;
                    }

                    if (!HasCodexAppServer(currentTree))
                    {
                        receipt.IsIsolationVerified = false;
                        item.UpdateRuntime(ProfileRuntimeState.Unknown, "app-server 未确认");
                        if (persistStoppedReceipt)
                        {
                            await SaveStateAsync().ConfigureAwait(true);
                        }

                        return;
                    }

                    if (changed && persistStoppedReceipt)
                    {
                        await SaveStateAsync().ConfigureAwait(true);
                    }
                }

                item.UpdateRuntime(ProfileRuntimeState.Running, RunningDisplayStatus(receipt));
                break;
            default:
                check.Process?.Dispose();
                if (discovery.Matches.Count == 1)
                {
                    await RecoverReceiptAsync(item, paths, discovery.Matches[0], persistStoppedReceipt)
                        .ConfigureAwait(true);
                    return;
                }

                item.UpdateRuntime(ProfileRuntimeState.Unknown, "状态待确认");
                break;
        }
    }

    private async Task ReconcileJobRuntimeAsync(
        ProfileItemViewModel item,
        ProfilePaths paths,
        RunningInstanceReceipt receipt,
        bool persist)
    {
        if (!TryGetJobNames(item.Profile, receipt, out var names, out var nameDetails))
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Job 名称不匹配");
            _logger.Warning("JOB_RECEIPT_NAME_MISMATCH", "Job receipt 名称校验失败。", item.Profile.Id, new { nameDetails });
            return;
        }

        WindowsJobOwnershipInspection ownership;
        try
        {
            ownership = _jobManager.InspectOwnership(names);
        }
        catch (Exception ex) when (ex is WindowsJobObjectException or ArgumentException or Win32Exception)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Job 检查失败");
            _logger.Warning("JOB_OWNERSHIP_INSPECTION_FAILED", ex.Message, item.Profile.Id, new { Details = ex.ToString() });
            return;
        }

        if (receipt.IsLaunchPending ||
            receipt.LaunchPhase.Equals(JobLaunchPhases.PendingIntent, StringComparison.Ordinal))
        {
            await ReconcilePendingJobAsync(item, paths, receipt, names, ownership, persist)
                .ConfigureAwait(true);
            return;
        }

        if (!receipt.LaunchPhase.Equals(JobLaunchPhases.Resumed, StringComparison.Ordinal))
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Job 阶段未知");
            return;
        }

        var ownershipErrors = GetOwnershipInspectionErrors(ownership);
        if (ownershipErrors.Count != 0)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Job 检查失败");
            return;
        }

        if (!ownership.Job.Exists)
        {
            await ReconcileMissingResumedJobAsync(item, paths, receipt, names, ownership, persist)
                .ConfigureAwait(true);
            return;
        }

        if (!ownership.Job.KillOnJobClose ||
            !ownership.ReadyEvent.Exists ||
            !ownership.ReadyEvent.IsSignaled ||
            !ownership.CancelEvent.Exists)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Job 所有权不完整");
            return;
        }

        var observed = WindowsProcessInspector.MergeIdentities(
            receipt.ObservedProcesses,
            ownership.Job.Members.Select(ToObservedIdentity));
        var observedChanged = !IdentitySetsEqual(receipt.ObservedProcesses, observed);
        receipt.ObservedProcesses = observed.ToList();

        if (ownership.Job.IsEmpty)
        {
            _ = await CompleteJobStopIfEmptyAsync(item, receipt).ConfigureAwait(true);
            return;
        }

        var brokerRecovery = _jobManager.RecoverBroker(names);
        var brokerVerified = _jobManager.VerifyBrokerIdentity(receipt, out var brokerDetails);
        var brokerMatches =
            brokerRecovery.State == WindowsJobBrokerRecoveryState.Found &&
            brokerRecovery.Broker is { } broker &&
            broker.ProcessId == receipt.BrokerProcessId &&
            broker.ProcessStartUtcTicks == receipt.BrokerProcessStartUtcTicks &&
            broker.WindowsSessionId == receipt.WindowsSessionId &&
            brokerVerified;
        if (!brokerMatches)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Broker 身份不匹配");
            _logger.Warning(
                "JOB_BROKER_IDENTITY_UNVERIFIED",
                "Job broker 精确身份未通过持续核验。",
                item.Profile.Id,
                new
                {
                    brokerDetails,
                    brokerRecovery.State,
                    brokerRecovery.InspectionErrors,
                });
            return;
        }

        var check = ProcessReceiptVerifier.Check(receipt);
        switch (check.Status)
        {
            case ProcessReceiptStatus.VerifiedRunning when check.Process is { } process:
            {
                var rootMember = ownership.Job.Members.FirstOrDefault(member =>
                    member.ProcessId == receipt.RootProcessId &&
                    member.ProcessStartUtcTicks == receipt.ProcessStartUtcTicks);
                var nativeMembershipVerified = _jobManager.VerifyMembership(
                    receipt,
                    out var membershipDetails);
                var membershipVerified =
                    rootMember is not null &&
                    rootMember.WindowsSessionId == receipt.WindowsSessionId &&
                    nativeMembershipVerified;
                var argumentsVerified = _processInspector.VerifyProfileRootArguments(
                    receipt.RootProcessId,
                    paths.AppData,
                    out var argumentDetails);
                if (!membershipVerified || !argumentsVerified)
                {
                    process.Dispose();
                    item.UpdateRuntime(ProfileRuntimeState.Unknown, "根进程所有权不匹配");
                    _logger.Warning(
                        "JOB_ROOT_IDENTITY_UNVERIFIED",
                        "根进程 identity/argv/job membership 未通过核验。",
                        item.Profile.Id,
                        new { membershipDetails, argumentDetails });
                    return;
                }

                if (!JobReceiptRecoveryPolicy.CanUseInteractiveWindowControl(
                        receipt.WindowsSessionId,
                        _jobManager.CurrentWindowsSessionId))
                {
                    if (observedChanged && persist)
                    {
                        await SaveStateAsync().ConfigureAwait(true);
                    }

                    item.UpdateRuntime(
                        ProfileRuntimeState.Unknown,
                        $"在 Windows 会话 {receipt.WindowsSessionId} 中运行");
                    process.Dispose();
                    return;
                }

                TrackProcess(item.Profile.Id, process, receipt.LaunchId);
                if (!receipt.IsIsolationVerified)
                {
                    await VerifyRecoveredIsolationAsync(item, paths, receipt, process, persist)
                        .ConfigureAwait(true);
                    return;
                }

                if (!HasCodexAppServer(ownership.Job.Members.Select(ToObservedIdentity)))
                {
                    receipt.IsIsolationVerified = false;
                    item.UpdateRuntime(ProfileRuntimeState.Unknown, "app-server 未确认");
                    if (persist)
                    {
                        await SaveStateAsync().ConfigureAwait(true);
                    }

                    return;
                }

                if (observedChanged && persist)
                {
                    await SaveStateAsync().ConfigureAwait(true);
                }

                item.UpdateRuntime(ProfileRuntimeState.Running, RunningDisplayStatus(receipt));
                return;
            }
            case ProcessReceiptStatus.Stopped:
                check.Process?.Dispose();
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "根进程已退，Job 后代仍在运行");
                if (observedChanged && persist)
                {
                    await SaveStateAsync().ConfigureAwait(true);
                }

                return;
            default:
                check.Process?.Dispose();
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "根进程状态待确认");
                return;
        }
    }

    private async Task ReconcilePendingJobAsync(
        ProfileItemViewModel item,
        ProfilePaths paths,
        RunningInstanceReceipt receipt,
        WindowsJobNames names,
        WindowsJobOwnershipInspection ownership,
        bool persist)
    {
        var errors = GetOwnershipInspectionErrors(ownership);
        var readyState = ToReadySignalState(ownership.ReadyEvent);
        var age = DateTimeOffset.UtcNow - receipt.LaunchedUtc;
        if (!ownership.Job.Exists &&
            readyState == JobReadySignalState.Missing &&
            ownership.CancelEvent.Exists)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Pending 命名对象单侧丢失");
            return;
        }

        var absence = default(JobAbsenceEvidence);
        if (!ownership.Job.Exists && readyState == JobReadySignalState.Missing)
        {
            absence = await CollectJobAbsenceEvidenceAsync(
                    paths,
                    receipt,
                    names,
                    ownership,
                    requireSecondMissingSnapshot: true)
                .ConfigureAwait(true);
        }
        else if (errors.Count != 0)
        {
            absence = new(0, false, false, false, true);
        }

        var action = JobReceiptRecoveryPolicy.DecidePending(
            ownership.Job.Exists,
            readyState,
            age,
            absence);
        switch (action)
        {
            case PendingJobRecoveryAction.AbortOwnedJob:
            {
                try
                {
                    if (!ownership.Job.KillOnJobClose)
                    {
                        item.UpdateRuntime(ProfileRuntimeState.Unknown, "Pending Job 缺少终止保证");
                        return;
                    }

                    var brokerRecovery = _jobManager.RecoverBroker(names);
                    var recoveredBroker = brokerRecovery.Broker;
                    var recoveredBrokerMatches =
                        brokerRecovery.State == WindowsJobBrokerRecoveryState.Found &&
                        recoveredBroker is not null &&
                        recoveredBroker.WindowsSessionId == receipt.WindowsSessionId &&
                        (receipt.BrokerProcessId == 0 ||
                         (recoveredBroker.ProcessId == receipt.BrokerProcessId &&
                          recoveredBroker.ProcessStartUtcTicks == receipt.BrokerProcessStartUtcTicks));
                    if (!recoveredBrokerMatches)
                    {
                        item.UpdateRuntime(ProfileRuntimeState.Unknown, "Pending broker 待确认");
                        return;
                    }

                    receipt.BrokerProcessId = recoveredBroker!.ProcessId;
                    receipt.BrokerProcessStartUtcTicks = recoveredBroker.ProcessStartUtcTicks;
                    receipt.WindowsSessionId = recoveredBroker.WindowsSessionId;
                    var termination = await _jobManager
                        .TerminateVerifiedReceiptAndWaitForStableEmptyAsync(
                            receipt,
                            TimeSpan.FromSeconds(20))
                        .ConfigureAwait(true);
                    if (!termination.Succeeded)
                    {
                        item.UpdateRuntime(ProfileRuntimeState.Unknown, "Pending Job 回滚待确认");
                        _logger.Warning(
                            termination.Code,
                            "Pending Job pinned 回滚未确认。",
                            item.Profile.Id,
                            new { termination.Details, termination.VerifiedMemberProcessIds });
                        return;
                    }

                    await ClearReceiptIfMatchesAsync(item.Profile.Id, receipt.LaunchId).ConfigureAwait(true);
                    item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
                    _logger.Warning(
                        "PENDING_JOB_ABORTED",
                        "检测到未提交完成的 Job 启动；已整组终止并验证稳定为空。",
                        item.Profile.Id,
                        new { receipt.LaunchId, ownership.Job.ProcessIds });
                    return;
                }
                catch (Exception ex) when (ex is WindowsJobObjectException or ArgumentException or Win32Exception)
                {
                    item.UpdateRuntime(ProfileRuntimeState.Unknown, "Pending Job 回滚待确认");
                    _logger.Warning(
                        "PENDING_JOB_ABORT_FAILED",
                        ex.Message,
                        item.Profile.Id,
                        new
                        {
                            Details = ex is WindowsJobObjectException jobError
                                ? jobError.Details
                                : ex.ToString(),
                        });
                    return;
                }
            }
            case PendingJobRecoveryAction.ReclaimExpiredUnready:
            {
                var reclaim = await _jobManager
                    .ReclaimExpiredPendingUnreadyAsync(receipt, TimeSpan.FromSeconds(20))
                    .ConfigureAwait(true);
                if (!reclaim.Reclaimed)
                {
                    item.UpdateRuntime(ProfileRuntimeState.Unknown, "Unready Job 回收待确认");
                    _logger.Warning(
                        reclaim.Code,
                        "过期 unready Pending Job 未能安全回收。",
                        item.Profile.Id,
                        new { reclaim.Details });
                    return;
                }

                await ClearReceiptIfMatchesAsync(item.Profile.Id, receipt.LaunchId).ConfigureAwait(true);
                item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
                _logger.Warning(
                    reclaim.Code,
                    "过期 unready Pending Job 已通过 pinned generation 安全回收。",
                    item.Profile.Id,
                    new { reclaim.Details });
                return;
            }
            case PendingJobRecoveryAction.ClearReceipt:
                item.Profile.ActiveInstance = null;
                if (persist)
                {
                    await SaveStateAsync().ConfigureAwait(true);
                }

                item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
                return;
            case PendingJobRecoveryAction.Wait:
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "启动事务恢复窗口内");
                SchedulePendingRecovery(item, receipt, age);
                return;
            default:
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "Pending Job 状态不完整");
                return;
        }
    }

    private async Task ReconcileMissingResumedJobAsync(
        ProfileItemViewModel item,
        ProfilePaths paths,
        RunningInstanceReceipt receipt,
        WindowsJobNames names,
        WindowsJobOwnershipInspection firstOwnership,
        bool persist)
    {
        if (firstOwnership.ReadyEvent.Exists || firstOwnership.CancelEvent.Exists)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Job 名称单侧丢失");
            return;
        }

        var absence = await CollectJobAbsenceEvidenceAsync(
                paths,
                receipt,
                names,
                firstOwnership,
                requireSecondMissingSnapshot: true)
            .ConfigureAwait(true);
        var action = JobReceiptRecoveryPolicy.DecideResumedMissing(absence);
        if (action == MissingJobCompletionAction.ClearReceipt)
        {
            item.Profile.ActiveInstance = null;
            if (persist)
            {
                await SaveStateAsync().ConfigureAwait(true);
            }

            item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
            _logger.Info(
                "MISSING_JOB_DRAIN_VERIFIED",
                "Job 名称丢失后已确认 broker、根/已观察身份和 profile argv 连续稳定无残留。",
                item.Profile.Id,
                new { receipt.LaunchId });
            return;
        }

        item.UpdateRuntime(
            ProfileRuntimeState.Unknown,
            action == MissingJobCompletionAction.Wait ? "Job 退出状态稳定中" : "Job 所有权丢失");
    }

    private async Task<JobAbsenceEvidence> CollectJobAbsenceEvidenceAsync(
        ProfilePaths paths,
        RunningInstanceReceipt receipt,
        WindowsJobNames names,
        WindowsJobOwnershipInspection firstOwnership,
        bool requireSecondMissingSnapshot)
    {
        var stableMissing =
            !firstOwnership.Job.Exists &&
            !firstOwnership.ReadyEvent.Exists &&
            !firstOwnership.CancelEvent.Exists
                ? 1
                : 0;
        var hasError = GetOwnershipInspectionErrors(firstOwnership).Count != 0;
        var firstDrain = CaptureIdentityDrainSnapshot(paths, receipt);
        hasError |= firstDrain.HasError;
        var brokerAbsent = firstDrain.BrokerAbsent;
        var rootAbsent = firstDrain.RootAndObservedAbsent;
        var profileEmpty = firstDrain.ProfileEmpty;
        if (requireSecondMissingSnapshot && stableMissing == 1)
        {
            await Task.Delay(250).ConfigureAwait(true);
            try
            {
                var second = _jobManager.InspectOwnership(names);
                hasError |= GetOwnershipInspectionErrors(second).Count != 0;
                if (!second.Job.Exists && !second.ReadyEvent.Exists && !second.CancelEvent.Exists)
                {
                    stableMissing++;
                }
            }
            catch (Exception ex) when (ex is WindowsJobObjectException or ArgumentException or Win32Exception)
            {
                hasError = true;
            }

            var secondDrain = CaptureIdentityDrainSnapshot(paths, receipt);
            hasError |= secondDrain.HasError;
            brokerAbsent &= secondDrain.BrokerAbsent;
            rootAbsent &= secondDrain.RootAndObservedAbsent;
            profileEmpty &= secondDrain.ProfileEmpty;
        }

        return new(
            stableMissing,
            brokerAbsent,
            rootAbsent,
            profileEmpty,
            hasError);
    }

    private (bool BrokerAbsent, bool RootAndObservedAbsent, bool ProfileEmpty, bool HasError)
        CaptureIdentityDrainSnapshot(
            ProfilePaths paths,
            RunningInstanceReceipt receipt)
    {
        var brokerAbsent = IsPersistedBrokerDefinitivelyAbsent(receipt, out var brokerInspectionError);
        var hasError = brokerInspectionError;
        var rootAbsent = true;
        if (receipt.RootProcessId > 0 && receipt.ProcessStartUtcTicks > 0)
        {
            var rootCheck = ProcessReceiptVerifier.Check(receipt);
            rootCheck.Process?.Dispose();
            rootAbsent = rootCheck.Status == ProcessReceiptStatus.Stopped;
            hasError |= rootCheck.Status == ProcessReceiptStatus.Unknown;
        }

        var observed = _processInspector.FindLiveIdentities(receipt.ObservedProcesses);
        hasError |= observed.InspectionErrors.Count != 0;
        rootAbsent &= observed.LiveIdentities.Count == 0;

        var discovery = DiscoverProfileProcesses(paths, receipt);
        hasError |= discovery.InspectionErrors.Count != 0;
        var profileEmpty = discovery.Matches.Count == 0;
        return (brokerAbsent, rootAbsent, profileEmpty, hasError);
    }

    private bool IsPersistedBrokerDefinitivelyAbsent(
        RunningInstanceReceipt receipt,
        out bool inspectionError)
    {
        var inspection = _jobManager.InspectBrokerIdentity(receipt);
        inspectionError = inspection.State == WindowsJobBrokerIdentityState.InspectionError;
        return inspection.State == WindowsJobBrokerIdentityState.DefinitelyAbsent;
    }

    private void SchedulePendingRecovery(
        ProfileItemViewModel item,
        RunningInstanceReceipt receipt,
        TimeSpan currentAge)
    {
        lock (_scheduledPendingRecoveries)
        {
            if (!_scheduledPendingRecoveries.Add(receipt.LaunchId))
            {
                return;
            }
        }

        var remaining = JobReceiptRecoveryPolicy.PendingRecoveryWindow - currentAge;
        if (remaining < TimeSpan.FromMilliseconds(250))
        {
            remaining = TimeSpan.FromMilliseconds(250);
        }

        _ = ReconcilePendingAfterDelayAsync(
            item.Profile.Id,
            receipt.LaunchId,
            remaining,
            _disposeCancellation.Token);
    }

    private async Task ReconcilePendingAfterDelayAsync(
        Guid profileId,
        Guid launchId,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            var item = Profiles.FirstOrDefault(candidate => candidate.Profile.Id == profileId);
            if (item?.Profile.ActiveInstance is not { } receipt ||
                receipt.LaunchId != launchId ||
                !receipt.IsLaunchPending)
            {
                return;
            }

            await ReconcileRuntimeAsync(item, persistStoppedReceipt: true).ConfigureAwait(true);
            if (SelectedProfile == item)
            {
                UpdateStatusForSelected();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // View model disposal intentionally cancels delayed recovery.
        }
        catch (Exception ex)
        {
            _logger.Error(
                "PENDING_JOB_DELAYED_RECONCILE_FAILED",
                ex.Message,
                ex,
                profileId);
        }
        finally
        {
            lock (_scheduledPendingRecoveries)
            {
                _scheduledPendingRecoveries.Remove(launchId);
            }
        }
    }

    private async Task<bool> CompleteJobStopIfEmptyAsync(
        ProfileItemViewModel item,
        RunningInstanceReceipt receipt)
    {
        try
        {
            var confirmation = await _jobManager
                .ConfirmVerifiedReceiptStableEmptyAsync(receipt, TimeSpan.FromSeconds(3))
                .ConfigureAwait(true);
            if (!confirmation.Succeeded)
            {
                item.UpdateRuntime(ProfileRuntimeState.Unknown, "Job 空状态未稳定");
                _logger.Warning(
                    confirmation.Code,
                    "Job 空完成未绑定到 exact broker generation。",
                    item.Profile.Id,
                    new { confirmation.Details, confirmation.VerifiedMemberProcessIds });
                return false;
            }

            await ClearReceiptIfMatchesAsync(item.Profile.Id, receipt.LaunchId).ConfigureAwait(true);
            item.UpdateRuntime(ProfileRuntimeState.Ready, "已就绪");
            if (SelectedProfile == item)
            {
                SetStatus("可以启动 Codex", "Windows Job 已连续稳定为空。", "\uE73E", PrimaryActionMode.Launch);
            }

            return true;
        }
        catch (Exception ex) when (ex is WindowsJobObjectException or ArgumentException or Win32Exception)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "Job 空状态检查失败");
            _logger.Warning(
                ex is WindowsJobObjectException jobError ? jobError.Code : "JOB_EMPTY_INSPECTION_FAILED",
                ex.Message,
                item.Profile.Id,
                new
                {
                    Details = ex is WindowsJobObjectException error
                        ? error.Details
                        : ex.ToString(),
                });
            return false;
        }
    }

    private bool TryGetJobNames(
        CodexProfile profile,
        RunningInstanceReceipt receipt,
        out WindowsJobNames names,
        out string details)
    {
        try
        {
            names = _jobManager.CreateNames(profile.Id, receipt.LaunchId);
            if (!names.JobObjectName.Equals(receipt.JobObjectName, StringComparison.Ordinal) ||
                !names.ReadyEventName.Equals(receipt.ReadyEventName, StringComparison.Ordinal))
            {
                details = $"期望 Job={names.JobObjectName}, Ready={names.ReadyEventName}；" +
                    $"记录 Job={receipt.JobObjectName}, Ready={receipt.ReadyEventName}。";
                return false;
            }

            details = "Job/ready/cancel 名称与当前 SID、profile、launch 精确匹配。";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            names = new(string.Empty, string.Empty, string.Empty);
            details = ex.Message;
            return false;
        }
    }

    private static List<string> GetOwnershipInspectionErrors(
        WindowsJobOwnershipInspection ownership)
    {
        var errors = new List<string>(ownership.Job.InspectionErrors);
        if (ownership.ReadyEvent.Error is not null)
        {
            errors.Add(ownership.ReadyEvent.Error);
        }

        if (ownership.CancelEvent.Error is not null)
        {
            errors.Add(ownership.CancelEvent.Error);
        }

        return errors;
    }

    private static JobReadySignalState ToReadySignalState(
        WindowsNamedSignalInspection inspection) =>
        inspection.Error is not null
            ? JobReadySignalState.InspectionError
            : inspection.Exists
                ? inspection.IsSignaled
                    ? JobReadySignalState.PresentSignaled
                    : JobReadySignalState.PresentUnsignaled
                : JobReadySignalState.Missing;

    private static ObservedProcessIdentity ToObservedIdentity(
        WindowsJobProcessIdentity identity) =>
        new()
        {
            ProcessId = identity.ProcessId,
            ProcessStartUtcTicks = identity.ProcessStartUtcTicks,
            ExecutablePath = identity.ExecutablePath,
        };

    private async Task EnsureBlockingIntentAsync(
        ProfileItemViewModel item,
        ProfilePaths paths,
        RunningInstanceReceipt? existing,
        bool persist)
    {
        if (existing is not null)
        {
            return;
        }

        var installation = _installation;
        item.Profile.ActiveInstance = new()
        {
            ProfileId = item.Profile.Id,
            LaunchId = Guid.NewGuid(),
            OwnershipMode = ProcessOwnershipModes.LegacyProcessTree,
            OwnershipVersion = ProcessOwnershipModes.LegacyProcessTreeVersion,
            IsLaunchPending = true,
            ExecutablePath = installation is null
                ? string.Empty
                : PathUtilities.Normalize(installation.ExecutablePath),
            CodexVersion = installation?.DisplayVersion ?? string.Empty,
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
            LaunchedUtc = DateTimeOffset.UtcNow,
        };

        if (persist)
        {
            await SaveStateAsync().ConfigureAwait(true);
        }
    }

    private async Task RecoverReceiptAsync(
        ProfileItemViewModel item,
        ProfilePaths paths,
        DiscoveredCodexProcess discovered,
        bool persist)
    {
        var existingLaunchId = item.Profile.ActiveInstance?.LaunchId ?? Guid.NewGuid();
        var observed = _processInspector.CaptureProcessTree(discovered.Identity);
        var receipt = new RunningInstanceReceipt
        {
            ProfileId = item.Profile.Id,
            LaunchId = existingLaunchId,
            OwnershipMode = ProcessOwnershipModes.LegacyProcessTree,
            OwnershipVersion = ProcessOwnershipModes.LegacyProcessTreeVersion,
            IsLaunchPending = false,
            IsIsolationVerified = false,
            RootProcessId = discovered.Identity.ProcessId,
            ProcessStartUtcTicks = discovered.Identity.ProcessStartUtcTicks,
            ExecutablePath = discovered.Identity.ExecutablePath,
            CodexVersion = _installation?.DisplayVersion ?? string.Empty,
            CodexHomePath = paths.CodexHome,
            AppDataPath = paths.AppData,
            LaunchedUtc = new DateTimeOffset(
                new DateTime(discovered.Identity.ProcessStartUtcTicks, DateTimeKind.Utc)),
            ObservedProcesses = observed.Identities.ToList(),
        };
        item.Profile.ActiveInstance = receipt;
        item.UpdateRuntime(ProfileRuntimeState.Unknown, "正在恢复隔离证据");

        if (persist)
        {
            await SaveStateAsync().ConfigureAwait(true);
        }

        try
        {
            var process = Process.GetProcessById(receipt.RootProcessId);
            TrackProcess(item.Profile.Id, process, receipt.LaunchId);
            await VerifyRecoveredIsolationAsync(item, paths, receipt, process, persist)
                .ConfigureAwait(true);
        }
        catch (ArgumentException)
        {
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "恢复时进程已退出");
        }
    }

    private async Task VerifyRecoveredIsolationAsync(
        ProfileItemViewModel item,
        ProfilePaths paths,
        RunningInstanceReceipt receipt,
        Process process,
        bool persist)
    {
        var verification = await _startupVerifier
            .VerifyAsync(process, receipt, paths, receipt.LaunchedUtc)
            .ConfigureAwait(true);
        if (!verification.IsVerified)
        {
            receipt.IsIsolationVerified = false;
            item.UpdateRuntime(ProfileRuntimeState.Unknown, "隔离证据待确认");
            if (persist)
            {
                await SaveStateAsync().ConfigureAwait(true);
            }

            _logger.Warning(
                "RECOVERED_PROCESS_ISOLATION_UNVERIFIED",
                verification.Message,
                item.Profile.Id,
                new { verification.Details, receipt.RootProcessId });
            return;
        }

        receipt.IsIsolationVerified = true;
        receipt.ObservedProcesses = WindowsProcessInspector
            .MergeIdentities(receipt.ObservedProcesses, verification.ObservedProcesses)
            .ToList();
        item.Profile.LastVerifiedCodexVersion = receipt.CodexVersion;
        item.UpdateRuntime(ProfileRuntimeState.Running, RunningDisplayStatus(receipt));
        if (persist)
        {
            await SaveStateAsync().ConfigureAwait(true);
        }

        _logger.Info(
            "RECOVERED_PROCESS_ISOLATION_VERIFIED",
            "已恢复并重新验证现存 Codex 进程的完整隔离证据。",
            item.Profile.Id,
            new { verification.Details, receipt.RootProcessId });
    }

    private static bool HasCodexAppServer(IEnumerable<ObservedProcessIdentity> identities) =>
        identities.Any(identity =>
            Path.GetFileName(identity.ExecutablePath)
                .Equals("codex.exe", StringComparison.OrdinalIgnoreCase));

    private static ObservedProcessIdentity CreateRootIdentity(RunningInstanceReceipt receipt) =>
        new()
        {
            ProcessId = receipt.RootProcessId,
            ProcessStartUtcTicks = receipt.ProcessStartUtcTicks,
            ExecutablePath = receipt.ExecutablePath,
        };

    internal static bool IsStopSnapshotEmpty(
        ProcessTreeInspectionResult tree,
        ProcessDiscoveryResult discovery,
        LiveIdentityInspectionResult liveIdentityCheck) =>
        tree.Identities.Count == 0 &&
        tree.InspectionErrors.Count == 0 &&
        discovery.Matches.Count == 0 &&
        discovery.InspectionErrors.Count == 0 &&
        liveIdentityCheck.LiveIdentities.Count == 0 &&
        liveIdentityCheck.InspectionErrors.Count == 0;

    internal static bool HasStableEmptyStopSnapshots(IReadOnlyList<bool> snapshots) =>
        snapshots.Count >= 2 && snapshots[^1] && snapshots[^2];

    internal static bool CanContinueAfterTermination(ProcessTerminationResult result) =>
        result.InspectionErrors.Count == 0;

    private static bool IdentitySetsEqual(
        IEnumerable<ObservedProcessIdentity> first,
        IEnumerable<ObservedProcessIdentity> second)
    {
        var firstKeys = first
            .Select(identity => (identity.ProcessId, identity.ProcessStartUtcTicks, identity.ExecutablePath))
            .OrderBy(value => value.ProcessId)
            .ThenBy(value => value.ProcessStartUtcTicks)
            .ToArray();
        var secondKeys = second
            .Select(identity => (identity.ProcessId, identity.ProcessStartUtcTicks, identity.ExecutablePath))
            .OrderBy(value => value.ProcessId)
            .ThenBy(value => value.ProcessStartUtcTicks)
            .ToArray();
        return firstKeys.SequenceEqual(secondKeys);
    }

    private ProcessDiscoveryResult DiscoverProfileProcesses(
        ProfilePaths paths,
        RunningInstanceReceipt? receipt)
    {
        // An active receipt names the exact mirrored executable generation that
        // was launched. A newer Store package may already be installed while
        // that older runtime is still running, so the receipt is authoritative.
        var executablePath = receipt?.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = _installation?.ExecutablePath;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return receipt is null
                ? new([], [])
                : new([], ["Codex 安装信息不可用，且运行记录缺少可核验的可执行文件路径。"]);
        }

        try
        {
            return _processInspector.FindProfileRoots(executablePath, paths.AppData);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new([], [$"Codex 进程发现失败：{ex.Message}"]);
        }
    }

    private async Task RefreshCodexAsync(bool showDialogOnFailure)
    {
        var previousBusy = IsBusy;
        IsBusy = true;
        try
        {
            _installation = await _appLocator.ResolveAsync().ConfigureAwait(true);
            CodexDetectionText = "已找到 Codex";
            CodexVersionText = $"版本 {_installation.DisplayVersion}";
            CodexExecutablePath = _installation.ExecutablePath;
            if (HasSelection && SelectedProfile!.RuntimeState == ProfileRuntimeState.Ready)
            {
                UpdateStatusForSelected();
            }
        }
        catch (CodexAppLocatorException ex)
        {
            SetCodexUnavailable(ex);
            _logger.Warning(ex.Code, ex.Message, details: ex.Details);
            if (showDialogOnFailure)
            {
                _dialogs.ShowError("Codex 检测失败", ex.Message, ex.Details);
            }
        }
        finally
        {
            IsBusy = previousBusy;
        }
    }

    private void SetCodexUnavailable(CodexAppLocatorException ex)
    {
        _installation = null;
        CodexDetectionText = "未找到 Codex";
        CodexVersionText = string.Empty;
        CodexExecutablePath = string.Empty;
        if (HasSelection)
        {
            SetStatus("未找到 Codex", ex.Message, "\uE7BA", PrimaryActionMode.RefreshCodex, ex.Details);
        }
    }

    public async Task<bool> TryPrepareCloseAsync()
    {
        if (_disposed)
        {
            return true;
        }

        if (IsBusy)
        {
            _dialogs.ShowInformation("暂时无法关闭", "启动器正在执行操作，请等待当前操作完成后再关闭。");
            return false;
        }

        if (SelectedProfile is null)
        {
            return true;
        }

        var hasDirty =
            IsConfigDirty ||
            AiSettingsEditor?.IsDirty == true ||
            (IsSkillMarkdownDirty && SelectedSkill is not null);
        if (!hasDirty)
        {
            return true;
        }

        IsBusy = true;
        try
        {
            return await ResolveAllDirtyStateAsync(SelectedProfile).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> GuardDirtyConfigBeforeNavigationAsync()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            return true;
        }

        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            return await ResolveAllDirtyStateAsync(profile).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ResolveAllDirtyStateAsync(ProfileItemViewModel? profile)
    {
        if (profile is null)
        {
            return true;
        }

        if (AiSettingsEditor?.IsDirty == true)
        {
            var aiChoice = _dialogs.ConfirmWithAlternate(
                "保存 AI 配置？",
                $"环境“{profile.Name}”的 AI 配置有尚未保存的更改。",
                confirmText: "保存更改",
                alternateText: "放弃更改",
                cancelText: "留在此页");
            if (aiChoice == ConfirmationDialogChoice.Confirm)
            {
                if (!await AiSettingsEditor.SaveAsync().ConfigureAwait(true))
                {
                    return false;
                }

                await LoadAiSummaryAsync(profile).ConfigureAwait(true);
            }
            else if (aiChoice != ConfirmationDialogChoice.Secondary)
            {
                return false;
            }
        }

        if (IsSkillMarkdownDirty && SelectedSkill is not null)
        {
            var skillChoice = _dialogs.ConfirmWithAlternate(
                "保存技能更改？",
                $"技能“{SelectedSkill.Name}”的 SKILL.md 有尚未保存的更改。",
                confirmText: "保存更改",
                alternateText: "放弃更改",
                cancelText: "留在此页");
            if (skillChoice == ConfirmationDialogChoice.Confirm)
            {
                await SaveSelectedSkillAsync().ConfigureAwait(true);
            }
            else if (skillChoice != ConfirmationDialogChoice.Secondary)
            {
                return false;
            }
        }

        return await ResolveDirtyConfigAsync(profile).ConfigureAwait(true);
    }

    private async Task<bool> ResolveDirtyConfigAsync(ProfileItemViewModel? profile)
    {
        if (profile is null || !ReferenceEquals(_selectedProfile, profile) || !IsConfigDirty)
        {
            return true;
        }

        return PromptDirtyConfigAction(profile.Name) switch
        {
            DirtyConfigAction.Save => await SaveConfigForProfileAsync(profile).ConfigureAwait(true),
            DirtyConfigAction.Discard => true,
            _ => false,
        };
    }

    private DirtyConfigAction PromptDirtyConfigAction(string profileName)
    {
        var result = _dialogs.ConfirmWithAlternate(
            "保存配置更改？",
            $"环境“{profileName}”的 config.toml 有尚未保存的更改。",
            confirmText: "保存更改",
            alternateText: "放弃更改",
            cancelText: "留在此页");
        return result switch
        {
            ConfirmationDialogChoice.Confirm => DirtyConfigAction.Save,
            ConfirmationDialogChoice.Secondary => DirtyConfigAction.Discard,
            _ => DirtyConfigAction.Cancel,
        };
    }

    private async Task SaveConfigAsync()
    {
        var profile = SelectedProfile;
        if (profile is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await SaveConfigForProfileAsync(profile).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> SaveConfigForProfileAsync(ProfileItemViewModel profile)
    {
        if (!ReferenceEquals(_selectedProfile, profile))
        {
            return false;
        }

        var editedText = _configText;
        var savedText = _savedConfigText;
        try
        {
            var paths = ProfilePaths.FromRoot(profile.DataRoot);
            var workingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                ? paths.DataRoot
                : profile.WorkingDirectory;
            var currentDiskText = File.Exists(paths.ConfigFile)
                ? await File.ReadAllTextAsync(paths.ConfigFile).ConfigureAwait(true)
                : ConfigIsolationAuditor.CreateDefaultConfig();
            if (!currentDiskText.Equals(savedText, StringComparison.Ordinal))
            {
                SetStatus(
                    "配置文件已在外部更改",
                    "为避免覆盖较新的磁盘内容，本次保存已被阻止。请切换环境或重启启动器后再编辑。",
                    "\uE7BA",
                    PrimaryActionMode.OpenConfig,
                    paths.ConfigFile);
                _dialogs.ShowError(
                    "配置保存已阻止",
                    "config.toml 在编辑期间被其他程序修改。",
                    "启动器没有覆盖磁盘内容。请重新加载后再合并你的更改。");
                return false;
            }

            await ConfigIsolationAuditor.SaveValidatedAsync(
                editedText,
                paths,
                workingDirectory).ConfigureAwait(true);
            _savedConfigText = editedText;
            OnPropertyChanged(nameof(IsConfigDirty));
            _saveConfigCommand.RaiseCanExecuteChanged();
            SetStatus("配置已保存", "此环境仍满足严格隔离要求。", "\uE73E", PrimaryActionMode.Launch);
            _logger.Info("CONFIG_SAVED", "环境配置已保存并通过隔离审计。", profile.Profile.Id);
            return true;
        }
        catch (IsolationValidationException ex)
        {
            ShowIsolationFailure(ex.Report, profile.Profile.Id);
            SetStatus(
                "配置需要处理",
                ex.Report.Issues[0].Message,
                "\uE7BA",
                PrimaryActionMode.OpenConfig,
                string.Join(Environment.NewLine, ex.Report.Issues.Select(issue => issue.Details)));
            return false;
        }
        catch (Exception ex)
        {
            HandleOperationError("CONFIG_SAVE_FAILED", "配置保存失败", ex, profile.Profile.Id);
            return false;
        }
    }

    private async Task SaveStateAsync()
    {
        await _stateGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var expectedRevision = _state.Revision;
            var saved = await _repository
                .SaveAsync(_state, expectedRevision)
                .ConfigureAwait(true);
            _state.Revision = saved.Revision;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<LaunchIntentSaveResolution> ResolveFailedLaunchIntentSaveAsync(
        ProfileItemViewModel item,
        Guid launchId,
        long expectedRevision,
        RunningInstanceReceipt? previousActiveInstance,
        DateTimeOffset? previousLastStartedUtc)
    {
        LauncherState authoritative;
        try
        {
            authoritative = await _repository.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception reloadException)
        {
            _logger.Error(
                "LAUNCH_INTENT_RELOAD_FAILED",
                "启动意图保存异常后无法重新读取权威状态。",
                reloadException,
                item.Profile.Id,
                new { launchId, expectedRevision });
            return LaunchIntentSaveResolution.Indeterminate;
        }

        var resolution = ClassifyLaunchIntentSaveResolution(
            authoritative,
            item.Profile.Id,
            launchId,
            expectedRevision);
        switch (resolution)
        {
            case LaunchIntentSaveResolution.CommitConfirmed:
                // Only expectedRevision + 1 with this unguessable launchId can be
                // attributed to this write. The in-memory graph already matches it.
                _state.Revision = authoritative.Revision;
                return resolution;

            case LaunchIntentSaveResolution.NotCommitted:
                item.Profile.ActiveInstance = previousActiveInstance;
                item.Profile.LastStartedUtc = previousLastStartedUtc;
                item.RefreshProfileProperties();
                return resolution;

            default:
                await ApplyAuthoritativeStateAsync(authoritative, item.Profile.Id).ConfigureAwait(true);
                return LaunchIntentSaveResolution.StateReloaded;
        }
    }

    private async Task ApplyAuthoritativeStateAsync(LauncherState authoritative, Guid preferredProfileId)
    {
        _state = authoritative;
        Profiles.Clear();
        foreach (var profile in authoritative.Profiles.OrderBy(profile => profile.CreatedUtc))
        {
            var profileItem = new ProfileItemViewModel(profile);
            if (profile.ActiveInstance is not null)
            {
                profileItem.UpdateRuntime(ProfileRuntimeState.Unknown, "运行状态待确认");
            }

            Profiles.Add(profileItem);
        }

        OnPropertyChanged(nameof(IsEmpty));
        var selected = Profiles.FirstOrDefault(profile => profile.Profile.Id == preferredProfileId)
            ?? (authoritative.SelectedProfileId is { } selectedId
                ? Profiles.FirstOrDefault(profile => profile.Profile.Id == selectedId)
                : null)
            ?? Profiles.FirstOrDefault();
        var configText = selected is null
            ? string.Empty
            : await ReadProfileConfigAsync(selected).ConfigureAwait(true);
        Interlocked.Increment(ref _selectionGeneration);
        ApplySelectedProfile(selected, configText);
    }

    internal static LaunchIntentSaveResolution ClassifyLaunchIntentSaveResolution(
        LauncherState authoritative,
        Guid profileId,
        Guid launchId,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        var persistedLaunchId = authoritative.Profiles
            .FirstOrDefault(profile => profile.Id == profileId)?
            .ActiveInstance?
            .LaunchId;

        if (expectedRevision < long.MaxValue &&
            authoritative.Revision == expectedRevision + 1 &&
            persistedLaunchId == launchId)
        {
            return LaunchIntentSaveResolution.CommitConfirmed;
        }

        if (authoritative.Revision == expectedRevision && persistedLaunchId != launchId)
        {
            return LaunchIntentSaveResolution.NotCommitted;
        }

        return LaunchIntentSaveResolution.StateReloaded;
    }

    private void UpdateStatusForSelected()
    {
        if (SelectedProfile is null)
        {
            SetStatus("请选择环境", "选择或创建环境后即可启动独立 Codex。", "\uE946", PrimaryActionMode.Blocked);
            return;
        }

        var isCompatibilityMode = SelectedProfile.Profile.ActiveInstance is { } receipt &&
                                  ProcessOwnershipModes.IsLegacy(receipt);
        switch (SelectedProfile.RuntimeState)
        {
            case ProfileRuntimeState.Running:
                SetStatus(
                    isCompatibilityMode ? "Codex 正在运行（桌面模式）" : "Codex 正在运行",
                    isCompatibilityMode
                        ? "环境数据隔离已验证；进程由 Windows 桌面 shell 创建，不再依赖启动器所在的外层 Job。"
                        : "此环境的数据与其他环境分开保存。",
                    "\uE768",
                    PrimaryActionMode.Activate);
                break;
            case ProfileRuntimeState.Unknown:
                SetStatus(
                    "运行状态需要确认",
                    "已阻止重复启动；可尝试正常关闭当前实例。",
                    "\uE7BA",
                    PrimaryActionMode.Blocked);
                break;
            case ProfileRuntimeState.Error:
                SetStatus("上次启动失败", "修复问题后可以重试。", "\uEA39", PrimaryActionMode.Launch);
                break;
            default:
                if (_installation is null)
                {
                    SetStatus("未找到 Codex", "安装后点击重新检测。", "\uE7BA", PrimaryActionMode.RefreshCodex);
                }
                else
                {
                    SetStatus("可以启动 Codex", "将使用此环境的独立数据启动。", "\uE73E", PrimaryActionMode.Launch);
                }
                break;
        }
    }

    private void SetStatus(
        string title,
        string message,
        string glyph,
        PrimaryActionMode primaryMode,
        string? extraDetails = null)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusGlyph = glyph;
        _primaryMode = primaryMode;
        OnPropertyChanged(nameof(PrimaryActionAutomationId));
        PrimaryActionText = primaryMode switch
        {
            PrimaryActionMode.Activate => "打开 Codex",
            PrimaryActionMode.RefreshCodex => "重新检测",
            PrimaryActionMode.EditProfile => "编辑环境",
            PrimaryActionMode.OpenConfig => "打开配置文件",
            _ => "启动 Codex",
        };
        OnPropertyChanged(nameof(CanPrimaryAction));
        RefreshRuntimeDetails(extraDetails);
        UpdateCommandStates();
    }

    private void RefreshRuntimeDetails(string? extraDetails)
    {
        if (SelectedProfile is null)
        {
            RuntimeDetails = extraDetails ?? "尚无运行记录。";
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.CurrentCulture, $"状态：{SelectedProfile.DisplayStatus}");
        if (SelectedProfile.Profile.ActiveInstance is { } receipt)
        {
            builder.AppendLine(CultureInfo.CurrentCulture, $"PID：{receipt.RootProcessId}");
            if (ProcessOwnershipModes.IsWindowsJob(receipt))
            {
                builder.AppendLine(CultureInfo.CurrentCulture, $"所有权：Windows Job v{receipt.OwnershipVersion}");
                builder.AppendLine(CultureInfo.CurrentCulture, $"Broker PID：{receipt.BrokerProcessId}");
                builder.AppendLine(CultureInfo.CurrentCulture, $"Windows 会话：{receipt.WindowsSessionId}");
            }
            else
            {
                builder.AppendLine("所有权：桌面父进程模式（精确进程树跟踪，不依赖启动器外层 Job）");
            }

            builder.AppendLine(CultureInfo.CurrentCulture, $"Codex 版本：{receipt.CodexVersion}");
            builder.AppendLine(CultureInfo.CurrentCulture, $"启动时间：{receipt.LaunchedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine(CultureInfo.CurrentCulture, $"程序路径：{receipt.ExecutablePath}");
        }

        if (!string.IsNullOrWhiteSpace(extraDetails))
        {
            builder.AppendLine();
            builder.AppendLine(extraDetails);
        }

        RuntimeDetails = builder.ToString().TrimEnd();
    }

    private void ShowValidationFailure(ProfileValidationResult validation)
    {
        var message = validation.Issues[0].Message;
        var details = string.Join(
            Environment.NewLine + Environment.NewLine,
            validation.Issues.Select(issue => $"{issue.Code}: {issue.Details}"));
        _dialogs.ShowError("环境设置无效", message, details);
    }

    private void ShowIsolationFailure(IsolationReport report, Guid profileId)
    {
        var message = report.Issues[0].Message;
        var details = string.Join(
            Environment.NewLine + Environment.NewLine,
            report.Issues.Select(issue => $"{issue.Code}: {issue.Details}"));
        _logger.Warning("PROFILE_ISOLATION_AUDIT_FAILED", message, profileId, new
        {
            Issues = report.Issues.Select(issue => new { issue.Code, issue.Message, issue.Details }).ToArray(),
        });
        _dialogs.ShowError("配置未通过隔离检查", message, details);
    }

    private void HandleOperationError(
        string eventId,
        string title,
        Exception exception,
        Guid? profileId = null,
        string? userMessage = null)
    {
        var jobError = exception as WindowsJobObjectException;
        _logger.Error(
            eventId,
            exception.Message,
            exception,
            profileId,
            jobError is null ? null : new { jobError.Code, jobError.Details });
        _dialogs.ShowError(title, userMessage ?? exception.Message, exception.ToString());
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _shell.OpenPath(path);
        }
        catch (Exception ex)
        {
            HandleOperationError("OPEN_PATH_FAILED", "无法打开路径", ex, SelectedProfile?.Profile.Id);
        }
    }

    private async Task LoadAiSummaryAsync(ProfileItemViewModel? profile)
    {
        if (profile is null)
        {
            AiSettingsSummary = "API 未启用 · Key 未保存 · 系统提示词未启用";
            HasAiKey = false;
            return;
        }

        var settings = await _aiSettingsService
            .LoadAsync(ProfilePaths.FromRoot(profile.DataRoot))
            .ConfigureAwait(true);
        HasAiKey = !string.IsNullOrEmpty(settings.ApiKey);
        var host = Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : settings.BaseUrl;
        AiSettingsSummary = $"API {(settings.ApiEnabled ? "已启用" : "未启用")} · {host} · " +
            $"Key {(HasAiKey ? "已保存（明文）" : "未保存")} · " +
            $"系统提示词{(settings.SystemPromptEnabled ? "已启用" : "未启用")}";
    }

    private async Task OpenAiSettingsAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedEnvironmentTab = EnvironmentConfigTab.Ai;
        if (AiSettingsEditor is null)
        {
            await LoadAiEditorAsync(SelectedProfile).ConfigureAwait(true);
        }
    }

    public async Task NotifyAiSettingsSavedAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        await LoadAiSummaryAsync(SelectedProfile).ConfigureAwait(true);
    }

    private async Task LoadAiEditorAsync(ProfileItemViewModel? profile)
    {
        if (profile is null)
        {
            AiSettingsEditor = null;
            return;
        }

        try
        {
            AiSettingsEditor = await _aiSettingsCoordinator
                .CreateViewModelAsync(profile)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AiSettingsEditor = null;
            HandleOperationError("AI_SETTINGS_LOAD_FAILED", "AI 配置加载失败", ex, profile.Profile.Id);
        }
    }

    private async Task LoadSkillsAsync(ProfileItemViewModel? profile)
    {
        Skills.Clear();
        SelectedSkill = null;
        SelectedSkillMarkdown = string.Empty;
        _savedSkillMarkdown = string.Empty;
        OnPropertyChanged(nameof(IsSkillMarkdownDirty));
        OnPropertyChanged(nameof(FilteredSkills));

        if (profile is null)
        {
            SkillsSummary = "尚未加载技能";
            return;
        }

        try
        {
            var paths = ProfilePaths.FromRoot(profile.DataRoot);
            var snapshot = _skillsService.List(paths);
            foreach (var skill in snapshot.Skills)
            {
                Skills.Add(new SkillItemViewModel(skill, OnSkillEnabledChangedAsync));
            }

            SkillsSummary =
                $"已启用 {snapshot.EnabledCount} / 共 {snapshot.Skills.Count} · 目录 {snapshot.SkillsDirectory}";
            OnPropertyChanged(nameof(FilteredSkills));
            if (Skills.Count > 0)
            {
                SelectedSkill = Skills[0];
            }
        }
        catch (Exception ex)
        {
            SkillsSummary = "技能列表加载失败";
            HandleOperationError("SKILLS_LOAD_FAILED", "技能列表加载失败", ex, profile.Profile.Id);
        }
    }

    private async Task OnSkillEnabledChangedAsync(SkillItemViewModel item, bool enabled)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            var paths = ProfilePaths.FromRoot(SelectedProfile.DataRoot);
            await _skillsService.SetEnabledAsync(paths, item.Id, enabled).ConfigureAwait(true);
            var refreshed = _skillsService.List(paths).Skills
                .FirstOrDefault(s => s.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null)
            {
                item.ApplyDescriptor(refreshed);
            }

            SkillsSummary =
                $"已启用 {Skills.Count(s => s.IsEnabled)} / 共 {Skills.Count} · 目录 {paths.SkillsDirectory}";
            SkillEditorHint = enabled
                ? "已写入本环境 skills；若 Codex 已在运行，请重开会话后生效。"
                : "已移出启用目录；若 Codex 已在运行，请重开会话后生效。";
            if (ReferenceEquals(SelectedSkill, item))
            {
                await LoadSelectedSkillMarkdownAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            HandleOperationError("SKILL_TOGGLE_FAILED", "切换技能失败", ex, SelectedProfile.Profile.Id);
            throw;
        }
    }

    private async Task LoadSelectedSkillMarkdownAsync()
    {
        if (SelectedProfile is null || SelectedSkill is null)
        {
            SelectedSkillMarkdown = string.Empty;
            _savedSkillMarkdown = string.Empty;
            OnPropertyChanged(nameof(IsSkillMarkdownDirty));
            SkillEditorHint = "选择左侧技能以查看或编辑 SKILL.md。";
            return;
        }

        try
        {
            if (!SelectedSkill.IsEnabled && !SelectedSkill.IsBuiltinAvailable &&
                SelectedSkill.Source == SkillSource.Builtin)
            {
                // Builtin-only (not installed): read from builtin path via reset/read after enable, or show empty
            }

            var paths = ProfilePaths.FromRoot(SelectedProfile.DataRoot);
            if (SelectedSkill.IsEnabled || SelectedSkill.Source == SkillSource.Disabled)
            {
                var text = await _skillsService.ReadSkillMarkdownAsync(paths, SelectedSkill.Id).ConfigureAwait(true);
                _savedSkillMarkdown = text;
                SelectedSkillMarkdown = text;
            }
            else if (SelectedSkill.IsBuiltinAvailable)
            {
                var builtinFile = Path.Combine(
                    _skillsService.ResolveBuiltinRoot(),
                    SelectedSkill.Id,
                    ProfileSkillsService.SkillMarkdownFileName);
                var text = File.Exists(builtinFile)
                    ? await File.ReadAllTextAsync(builtinFile).ConfigureAwait(true)
                    : string.Empty;
                _savedSkillMarkdown = text;
                SelectedSkillMarkdown = text;
                SkillEditorHint = "当前为内置模板预览。启用后可保存修改到本环境。";
                return;
            }
            else
            {
                _savedSkillMarkdown = string.Empty;
                SelectedSkillMarkdown = string.Empty;
            }

            SkillEditorHint = SelectedSkill.IsEnabled
                ? "编辑的是本环境副本；可「重置为内置」恢复模板。"
                : "技能未启用时仅可预览；启用后才能保存修改。";
            OnPropertyChanged(nameof(IsSkillMarkdownDirty));
        }
        catch (Exception ex)
        {
            SelectedSkillMarkdown = string.Empty;
            _savedSkillMarkdown = string.Empty;
            SkillEditorHint = $"无法读取 SKILL.md：{ex.Message}";
        }
    }

    private async Task InstallAllSkillsAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var paths = ProfilePaths.FromRoot(SelectedProfile.DataRoot);
            await _skillsService.InstallAllBuiltinAsync(paths).ConfigureAwait(true);
            await LoadSkillsAsync(SelectedProfile).ConfigureAwait(true);
            SkillEditorHint = "已安装全部内置技能到本环境。";
        }
        catch (Exception ex)
        {
            HandleOperationError("SKILLS_INSTALL_FAILED", "安装内置技能失败", ex, SelectedProfile.Profile.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportSkillAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            var folder = _dialogs.PickFolder("选择包含 SKILL.md 的技能目录");
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            IsBusy = true;
            var paths = ProfilePaths.FromRoot(SelectedProfile.DataRoot);
            await _skillsService.ImportFromFolderAsync(paths, folder).ConfigureAwait(true);
            await LoadSkillsAsync(SelectedProfile).ConfigureAwait(true);
            SkillEditorHint = "技能已导入并启用。";
        }
        catch (Exception ex)
        {
            HandleOperationError("SKILL_IMPORT_FAILED", "导入技能失败", ex, SelectedProfile.Profile.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenSkillsDirectory()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var paths = ProfilePaths.FromRoot(SelectedProfile.DataRoot);
        Directory.CreateDirectory(paths.SkillsDirectory);
        OpenPath(paths.SkillsDirectory);
    }

    private async Task SaveSelectedSkillAsync()
    {
        if (SelectedProfile is null || SelectedSkill is null)
        {
            return;
        }

        try
        {
            var paths = ProfilePaths.FromRoot(SelectedProfile.DataRoot);
            if (!SelectedSkill.IsEnabled)
            {
                await _skillsService.SetEnabledAsync(paths, SelectedSkill.Id, enabled: true).ConfigureAwait(true);
            }

            await _skillsService
                .SaveSkillMarkdownAsync(paths, SelectedSkill.Id, SelectedSkillMarkdown)
                .ConfigureAwait(true);
            _savedSkillMarkdown = SelectedSkillMarkdown;
            OnPropertyChanged(nameof(IsSkillMarkdownDirty));
            await LoadSkillsAsync(SelectedProfile).ConfigureAwait(true);
            SelectedSkill = Skills.FirstOrDefault(s =>
                s.Id.Equals(SelectedSkill.Id, StringComparison.OrdinalIgnoreCase));
            SkillEditorHint = "SKILL.md 已保存到本环境。";
        }
        catch (Exception ex)
        {
            HandleOperationError("SKILL_SAVE_FAILED", "保存技能失败", ex, SelectedProfile.Profile.Id);
        }
    }

    private async Task ResetSelectedSkillAsync()
    {
        if (SelectedProfile is null || SelectedSkill is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "重置为内置",
                $"将用内置模板覆盖「{SelectedSkill.Name}」在本环境中的副本。",
                confirmText: "重置"))
        {
            return;
        }

        try
        {
            var paths = ProfilePaths.FromRoot(SelectedProfile.DataRoot);
            await _skillsService.ResetToBuiltinAsync(paths, SelectedSkill.Id).ConfigureAwait(true);
            await LoadSkillsAsync(SelectedProfile).ConfigureAwait(true);
            SelectedSkill = Skills.FirstOrDefault(s =>
                s.Id.Equals(SelectedSkill.Id, StringComparison.OrdinalIgnoreCase));
            SkillEditorHint = "已重置为内置模板。";
        }
        catch (Exception ex)
        {
            HandleOperationError("SKILL_RESET_FAILED", "重置技能失败", ex, SelectedProfile.Profile.Id);
        }
    }

    private async Task CopySelectedAiKey()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            var settings = await _aiSettingsService
                .LoadAsync(ProfilePaths.FromRoot(SelectedProfile.DataRoot))
                .ConfigureAwait(true);
            if (!string.IsNullOrEmpty(settings.ApiKey))
            {
                _shell.CopyText(settings.ApiKey);
            }
        }
        catch (Exception ex)
        {
            HandleOperationError("AI_KEY_COPY_FAILED", "复制 API Key 失败", ex, SelectedProfile.Profile.Id);
        }
    }

    private void UpdateCommandStates()
    {
        _primaryActionCommand.RaiseCanExecuteChanged();
        _closeCodexCommand.RaiseCanExecuteChanged();
        _createProfileCommand.RaiseCanExecuteChanged();
        _editProfileCommand.RaiseCanExecuteChanged();
        _duplicateProfileCommand.RaiseCanExecuteChanged();
        _deleteProfileCommand.RaiseCanExecuteChanged();
        _refreshCodexCommand.RaiseCanExecuteChanged();
        _saveConfigCommand.RaiseCanExecuteChanged();
        _openDataRootCommand.RaiseCanExecuteChanged();
        _openWorkingDirectoryCommand.RaiseCanExecuteChanged();
        _copyDataRootCommand.RaiseCanExecuteChanged();
        _openAiSettingsCommand.RaiseCanExecuteChanged();
        _copyAiKeyCommand.RaiseCanExecuteChanged();
        _installAllSkillsCommand.RaiseCanExecuteChanged();
        _importSkillCommand.RaiseCanExecuteChanged();
        _openSkillsDirectoryCommand.RaiseCanExecuteChanged();
        _saveSelectedSkillCommand.RaiseCanExecuteChanged();
        _resetSelectedSkillCommand.RaiseCanExecuteChanged();
        _goToAiTabCommand.RaiseCanExecuteChanged();
        _goToSkillsTabCommand.RaiseCanExecuteChanged();
    }

    private enum PrimaryActionMode
    {
        Launch,
        Activate,
        RefreshCodex,
        EditProfile,
        OpenConfig,
        Blocked,
    }

    private enum DirtyConfigAction
    {
        Save,
        Discard,
        Cancel,
    }

    private sealed record TrackedProcess(Guid LaunchId, Process Process);

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _selectionGeneration);
        _disposeCancellation.Cancel();
        foreach (var tracked in _ownedProcesses.Values)
        {
            tracked.Process.Dispose();
        }

        _ownedProcesses.Clear();
        _disposeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
