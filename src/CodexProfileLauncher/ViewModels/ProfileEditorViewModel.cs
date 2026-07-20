using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.Core.Validation;

// PathUtilities lives in Models namespace.

namespace CodexProfileLauncher.ViewModels;

public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly CodexProfile? _existing;
    private readonly IReadOnlyList<CodexProfile> _allProfiles;
    private string _name;
    private string _dataRoot;
    private string _workingDirectory;
    private string _validationMessage = string.Empty;
    private string _actualDataRoot = string.Empty;
    private bool _usesManagedDataRootChild;
    private string _dataRootResolutionError = string.Empty;

    public ProfileEditorViewModel(
        CodexProfile? existing,
        IReadOnlyList<CodexProfile> allProfiles,
        Guid newProfileId,
        string suggestedName,
        string suggestedDataRoot,
        string suggestedWorkingDirectory)
    {
        _existing = existing;
        _allProfiles = allProfiles;
        ProfileId = existing?.Id ?? newProfileId;
        _name = existing?.Name ?? suggestedName;
        _dataRoot = existing?.DataRoot ?? suggestedDataRoot;
        _workingDirectory = existing?.WorkingDirectory ?? suggestedWorkingDirectory;
        RefreshDataRootResolution();
    }

    public Guid ProfileId { get; }

    public bool IsEditMode => _existing is not null;

    /// <summary>
    /// Data root may be changed in edit mode; UI warns and host migrates data on save.
    /// </summary>
    public bool CanEditDataRoot => true;

    public string OriginalDataRoot => _existing?.DataRoot ?? string.Empty;

    /// <summary>
    /// Exact launcher-owned directory that will be persisted. When the selected
    /// location is non-empty and unowned, this is a deterministic child path.
    /// </summary>
    public string ActualDataRoot => _actualDataRoot;

    public bool UsesManagedDataRootChild => _usesManagedDataRootChild;

    public string DataRootResolutionNote => !string.IsNullOrWhiteSpace(_dataRootResolutionError)
        ? $"暂时无法确定实际目录：{_dataRootResolutionError}"
        : UsesManagedDataRootChild
            ? "所选位置已有内容。启动器不会接管这些内容，只会管理上面的独占子目录。"
            : "保存时仍会校验目录身份、路径安全性和写入权限。";

    public bool IsDataRootChanged
    {
        get
        {
            if (!IsEditMode || string.IsNullOrWhiteSpace(OriginalDataRoot))
            {
                return false;
            }

            try
            {
                return !string.Equals(
                    PathUtilities.Normalize(ActualDataRoot),
                    PathUtilities.Normalize(OriginalDataRoot),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return !string.Equals(
                    DataRoot.Trim(),
                    OriginalDataRoot.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                ValidationMessage = string.Empty;
            }
        }
    }

    public string DataRoot
    {
        get => _dataRoot;
        set
        {
            if (SetProperty(ref _dataRoot, value))
            {
                ValidationMessage = string.Empty;
                RefreshDataRootResolution();
                OnPropertyChanged(nameof(IsDataRootChanged));
            }
        }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (SetProperty(ref _workingDirectory, value))
            {
                ValidationMessage = string.Empty;
            }
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        set => SetProperty(ref _validationMessage, value);
    }

    public bool Validate()
    {
        RefreshDataRootResolution();
        if (!string.IsNullOrWhiteSpace(_dataRootResolutionError))
        {
            ValidationMessage = $"实际环境目录无法确定：{_dataRootResolutionError}";
            return false;
        }

        var candidate = BuildProfile();
        var result = ProfilePathPolicy.Validate(candidate, _allProfiles);
        ValidationMessage = result.IsValid
            ? string.Empty
            : string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
        return result.IsValid;
    }

    public CodexProfile BuildProfile()
    {
        return new CodexProfile
        {
            Id = ProfileId,
            Name = Name.Trim(),
            DataRoot = ActualDataRoot,
            WorkingDirectory = WorkingDirectory.Trim(),
            CreatedUtc = _existing?.CreatedUtc ?? DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastStartedUtc = _existing?.LastStartedUtc,
            LastVerifiedCodexVersion = _existing?.LastVerifiedCodexVersion,
            ActiveInstance = _existing?.ActiveInstance,
        };
    }

    private void RefreshDataRootResolution()
    {
        try
        {
            var resolved = ProfileDataRootSelectionResolver.Resolve(
                DataRoot,
                ProfileId,
                OriginalDataRoot);
            _actualDataRoot = resolved.DataRoot;
            _usesManagedDataRootChild = resolved.UsesManagedChild;
            _dataRootResolutionError = string.Empty;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or
                IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _actualDataRoot = DataRoot.Trim();
            _usesManagedDataRootChild = false;
            _dataRootResolutionError = ex.Message;
        }

        OnPropertyChanged(nameof(ActualDataRoot));
        OnPropertyChanged(nameof(UsesManagedDataRootChild));
        OnPropertyChanged(nameof(DataRootResolutionNote));
    }
}
