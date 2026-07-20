using System.Collections.ObjectModel;

namespace CodexProfileLauncher.ViewModels;

public sealed record AiSettingsEditorState(
    bool ApiEnabled,
    string BaseUrl,
    string ApiKey,
    string SelectedModel,
    string ModelReasoningEffort,
    bool SystemPromptEnabled,
    string SystemPrompt,
    bool KeysmithModeEnabled,
    string RevisionToken);

public sealed record AiConnectionTestDisplay(
    bool IsSuccess,
    string RequestUrl,
    int? StatusCode,
    long ElapsedMilliseconds,
    int? ModelCount,
    IReadOnlyList<string> ModelIds,
    string Message);

public sealed class AiSettingsConflictException(
    string message,
    AiSettingsEditorState diskState) : Exception(message)
{
    public AiSettingsEditorState DiskState { get; } = diskState;
}

public sealed class AiSettingsDialogViewModel : ObservableObject
{
    public const string DefaultBaseUrl = "https://ai98pro.xyz/";

    private readonly Func<CancellationToken, Task<AiSettingsEditorState>> _reload;
    private readonly Func<AiSettingsEditorState, CancellationToken, Task<AiSettingsEditorState>> _save;
    private readonly Func<string, string, CancellationToken, Task<AiConnectionTestDisplay>> _test;
    private readonly Action<string> _copyText;
    private readonly Action<string> _openPath;
    private readonly string _settingsFilePath;
    private readonly string _promptFilePath;
    private AiSettingsEditorState _loadedState;
    private string _previousPrompt;
    private bool _apiEnabled;
    private string _baseUrl;
    private string _apiKey;
    private string _selectedModel;
    private string _modelReasoningEffort;
    private bool _systemPromptEnabled;
    private string _systemPrompt;
    private bool _keysmithModeEnabled;
    private bool _isBusy;
    private bool _hasConflict;
    private string _statusText;
    private string _testResultText = "尚未测试。";
    private CancellationTokenSource? _fetchCts;

    public AiSettingsDialogViewModel(
        AiSettingsEditorState initialState,
        bool isProfileRunning,
        string profileName,
        string settingsFilePath,
        string promptFilePath,
        Func<CancellationToken, Task<AiSettingsEditorState>> reload,
        Func<AiSettingsEditorState, CancellationToken, Task<AiSettingsEditorState>> save,
        Func<string, string, CancellationToken, Task<AiConnectionTestDisplay>> test,
        Action<string> copyText,
        Action<string> openPath)
    {
        _loadedState = initialState;
        _apiEnabled = initialState.ApiEnabled;
        _baseUrl = initialState.BaseUrl;
        _apiKey = initialState.ApiKey;
        _selectedModel = initialState.SelectedModel;
        _modelReasoningEffort = string.IsNullOrWhiteSpace(initialState.ModelReasoningEffort)
            ? "medium"
            : initialState.ModelReasoningEffort;
        _systemPromptEnabled = initialState.SystemPromptEnabled;
        _systemPrompt = initialState.SystemPrompt;
        _keysmithModeEnabled = initialState.KeysmithModeEnabled;
        _previousPrompt = initialState.SystemPrompt;
        _reload = reload;
        _save = save;
        _test = test;
        _copyText = copyText;
        _openPath = openPath;
        _settingsFilePath = settingsFilePath;
        _promptFilePath = promptFilePath;
        IsProfileRunning = isProfileRunning;
        ProfileContextText = $"仅应用于「{profileName}」";
        _statusText = isProfileRunning
            ? "保存后将在下次启动 Codex 时生效。"
            : "更改仅影响当前环境。";
        AvailableModels = [];
        ReasoningEffortOptions = ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
    }

    public ObservableCollection<string> AvailableModels { get; }

    public IReadOnlyList<string> ReasoningEffortOptions { get; }

    public bool ApiEnabled
    {
        get => _apiEnabled;
        set => SetEditableProperty(ref _apiEnabled, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetEditableProperty(ref _baseUrl, value ?? string.Empty);
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetEditableProperty(ref _apiKey, value ?? string.Empty);
    }

    public string SelectedModel
    {
        get => _selectedModel;
        set => SetEditableProperty(ref _selectedModel, value ?? string.Empty);
    }

    public string ModelReasoningEffort
    {
        get => _modelReasoningEffort;
        set => SetEditableProperty(ref _modelReasoningEffort, string.IsNullOrWhiteSpace(value) ? "medium" : value.Trim());
    }

    public bool SystemPromptEnabled
    {
        get => _systemPromptEnabled;
        // Product policy: always replace built-in model instructions.
        set => SetEditableProperty(ref _systemPromptEnabled, true);
    }

    public bool KeysmithModeEnabled
    {
        get => _keysmithModeEnabled;
        set
        {
            if (SetEditableProperty(ref _keysmithModeEnabled, value) && value)
            {
                // Keep instruction surface consistent when enabling keysmith mode.
                SystemPromptEnabled = true;
            }
        }
    }

    public string SystemPrompt
    {
        get => _systemPrompt;
        set
        {
            if (SetEditableProperty(ref _systemPrompt, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(PromptCharacterCountText));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public bool IsProfileRunning { get; }

    public string ProfileContextText { get; }

    public bool HasConflict
    {
        get => _hasConflict;
        private set => SetProperty(ref _hasConflict, value);
    }

    public bool IsDirty => !BuildState().Equals(_loadedState);

    public bool HasAvailableModels => AvailableModels.Count > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string TestResultText
    {
        get => _testResultText;
        private set => SetProperty(ref _testResultText, value);
    }

    public string PromptCharacterCountText => $"{SystemPrompt.Length:N0} 个字符";

    public void CopyKey()
    {
        if (string.IsNullOrEmpty(ApiKey))
        {
            StatusText = "当前没有可复制的 API Key。";
            return;
        }

        _copyText(ApiKey);
        StatusText = "API Key 已复制。";
    }

    public void OpenSettingsFile()
    {
        if (!File.Exists(_settingsFilePath))
        {
            ReportError("配置文件尚未创建；请先保存一次。");
            return;
        }

        TryOpenPath(_settingsFilePath);
    }

    public void OpenPromptFile()
    {
        if (!File.Exists(_promptFilePath))
        {
            ReportError("提示词文件尚未创建；请先保存包含提示词的配置。");
            return;
        }

        TryOpenPath(_promptFilePath);
    }

    public void ReportError(string message) => StatusText = message;

    public void ClearApi()
    {
        ApiEnabled = false;
        ApiKey = string.Empty;
        SelectedModel = string.Empty;
        AvailableModels.Clear();
        OnPropertyChanged(nameof(HasAvailableModels));
        TestResultText = "API 已清空并停用，保存后生效。";
    }

    public void RememberPromptBeforeImport() => _previousPrompt = SystemPrompt;

    public void UndoPrompt()
    {
        (SystemPrompt, _previousPrompt) = (_previousPrompt, SystemPrompt);
        StatusText = "已撤销最近一次提示词替换或清空。";
    }

    public void ClearPrompt()
    {
        _previousPrompt = SystemPrompt;
        SystemPrompt = string.Empty;
        StatusText = "提示词已清空，保存后生效。";
    }

    public string? Validate()
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "API 地址必须是完整的 http 或 https 地址。";
        }

        if (ApiEnabled && string.IsNullOrWhiteSpace(ApiKey))
        {
            return "启用自定义 API 时必须填写 API Key。";
        }

        if (SystemPromptEnabled && string.IsNullOrWhiteSpace(SystemPrompt))
        {
            return "启用系统提示词替换时，提示词内容不能为空。";
        }

        return null;
    }

    public async Task InitializeLiveModelsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey) ||
            !Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        await RefreshModelsAsync(updateTestResult: false, cancellationToken).ConfigureAwait(true);
    }

    public async Task RefreshModelsAsync(CancellationToken cancellationToken = default) =>
        await RefreshModelsAsync(updateTestResult: true, cancellationToken).ConfigureAwait(true);

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        var validation = Validate();
        if (validation is not null)
        {
            StatusText = validation;
            return false;
        }

        IsBusy = true;
        try
        {
            var saved = await _save(BuildState(), cancellationToken).ConfigureAwait(true);
            ApplyLoadedState(saved);
            HasConflict = false;
            StatusText = IsProfileRunning
                ? "已保存（含实时模型目录），下次启动 Codex 时生效。"
                : "已保存；启用 API 时已实时拉取模型并写入 Codex 目录。";
            return true;
        }
        catch (AiSettingsConflictException ex)
        {
            HasConflict = true;
            StatusText = "配置文件已在外部修改。请重新载入，或再次保存以保留当前输入。";
            _loadedState = ex.DiskState;
            return false;
        }
        catch (Exception ex)
        {
            ReportError($"保存失败：{ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var loaded = await _reload(cancellationToken).ConfigureAwait(true);
            ApplyLoadedState(loaded);
            HasConflict = false;
            StatusText = "已重新载入磁盘配置。";
            await InitializeLiveModelsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportError($"重新载入失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await RefreshModelsAsync(updateTestResult: true, cancellationToken).ConfigureAwait(true);

    public async Task CheckForExternalChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var disk = await _reload(cancellationToken).ConfigureAwait(true);
            if (disk.RevisionToken.Equals(_loadedState.RevisionToken, StringComparison.Ordinal))
            {
                return;
            }

            if (IsDirty)
            {
                HasConflict = true;
                StatusText = "磁盘配置已被外部修改。请选择重新载入，或保存当前输入进行覆盖。";
                return;
            }

            ApplyLoadedState(disk);
            HasConflict = false;
            StatusText = "已自动载入外部修改。";
            await InitializeLiveModelsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportError($"检查外部修改失败：{ex.Message}");
        }
    }

    private async Task RefreshModelsAsync(bool updateTestResult, CancellationToken cancellationToken)
    {
        var validation = ValidateConnectionInput();
        if (validation is not null)
        {
            if (updateTestResult)
            {
                TestResultText = validation;
            }
            else
            {
                StatusText = validation;
            }

            return;
        }

        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _fetchCts.Token;

        IsBusy = true;
        if (updateTestResult)
        {
            TestResultText = "正在实时拉取模型列表…";
        }
        else
        {
            StatusText = "正在实时拉取模型列表…";
        }

        try
        {
            var result = await _test(BaseUrl.Trim(), ApiKey, token).ConfigureAwait(true);
            ApplyFetchedModels(result.ModelIds, preserveSelection: true);

            var status = result.StatusCode is { } statusCode ? $"HTTP {statusCode}" : "无 HTTP 状态";
            var models = result.ModelCount is { } count
                ? $"，{count} 个模型"
                : result.ModelIds.Count > 0
                    ? $"，{result.ModelIds.Count} 个模型"
                    : string.Empty;
            var text = result.IsSuccess
                ? $"连接成功 · {status} · {result.ElapsedMilliseconds:N0} ms{models}\n{result.RequestUrl}"
                : $"连接失败 · {status} · {result.ElapsedMilliseconds:N0} ms\n{result.RequestUrl}\n{result.Message}";

            if (updateTestResult)
            {
                TestResultText = text;
            }

            StatusText = result.IsSuccess
                ? $"已实时拉取 {AvailableModels.Count} 个模型。"
                : $"实时拉取失败：{result.Message}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // replaced by a newer fetch
        }
        catch (Exception ex)
        {
            AvailableModels.Clear();
            OnPropertyChanged(nameof(HasAvailableModels));
            if (updateTestResult)
            {
                TestResultText = $"连接失败\n{ex}";
            }

            StatusText = $"实时拉取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFetchedModels(IReadOnlyList<string> modelIds, bool preserveSelection)
    {
        AvailableModels.Clear();
        foreach (var id in modelIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                AvailableModels.Add(id);
            }
        }

        OnPropertyChanged(nameof(HasAvailableModels));

        if (AvailableModels.Count == 0)
        {
            return;
        }

        if (preserveSelection &&
            !string.IsNullOrWhiteSpace(SelectedModel) &&
            AvailableModels.Contains(SelectedModel))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedModel) ||
            !AvailableModels.Contains(SelectedModel))
        {
            SelectedModel = AvailableModels[0];
        }
    }

    private string? ValidateConnectionInput()
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "API 地址必须是完整的 http 或 https 地址。";
        }

        return string.IsNullOrWhiteSpace(ApiKey) ? "请先填写 API Key。" : null;
    }

    private AiSettingsEditorState BuildState() => new(
        ApiEnabled,
        BaseUrl.Trim(),
        ApiKey,
        SelectedModel.Trim(),
        ModelReasoningEffort.Trim(),
        SystemPromptEnabled,
        SystemPrompt,
        KeysmithModeEnabled,
        _loadedState.RevisionToken);

    private void ApplyLoadedState(AiSettingsEditorState state)
    {
        _loadedState = state;
        _previousPrompt = state.SystemPrompt;
        _apiEnabled = state.ApiEnabled;
        _baseUrl = state.BaseUrl;
        _apiKey = state.ApiKey;
        _selectedModel = state.SelectedModel;
        _modelReasoningEffort = string.IsNullOrWhiteSpace(state.ModelReasoningEffort) ? "medium" : state.ModelReasoningEffort;
        _systemPromptEnabled = state.SystemPromptEnabled;
        _systemPrompt = state.SystemPrompt;
        _keysmithModeEnabled = state.KeysmithModeEnabled;
        OnPropertyChanged(nameof(ApiEnabled));
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(ModelReasoningEffort));
        OnPropertyChanged(nameof(SystemPromptEnabled));
        OnPropertyChanged(nameof(SystemPrompt));
        OnPropertyChanged(nameof(KeysmithModeEnabled));
        OnPropertyChanged(nameof(PromptCharacterCountText));
        OnPropertyChanged(nameof(IsDirty));
    }

    private void TryOpenPath(string path)
    {
        try
        {
            _openPath(path);
        }
        catch (Exception ex)
        {
            ReportError($"打开文件失败：{ex.Message}");
        }
    }

    private bool SetEditableProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        OnPropertyChanged(nameof(IsDirty));
        return true;
    }
}
