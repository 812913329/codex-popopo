using CodexProfileLauncher.Core.Models;

namespace CodexProfileLauncher.ViewModels;

public sealed class SkillItemViewModel : ObservableObject
{
    private readonly Func<SkillItemViewModel, bool, Task> _setEnabledAsync;
    private bool _isEnabled;
    private bool _suppress;
    private string _name;
    private string _description;
    private string _statusText;
    private bool _isCustomized;
    private bool _isBuiltinAvailable;

    public SkillItemViewModel(
        SkillDescriptor descriptor,
        Func<SkillItemViewModel, bool, Task> setEnabledAsync)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _setEnabledAsync = setEnabledAsync ?? throw new ArgumentNullException(nameof(setEnabledAsync));
        Id = descriptor.Id;
        _name = descriptor.Name;
        _description = descriptor.Description;
        _isEnabled = descriptor.IsEnabled;
        _isCustomized = descriptor.IsCustomized;
        _isBuiltinAvailable = descriptor.IsBuiltinAvailable;
        RootPath = descriptor.RootPath;
        Source = descriptor.Source;
        _statusText = BuildStatusText();
    }

    public string Id { get; }

    public string RootPath { get; private set; }

    public SkillSource Source { get; private set; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsCustomized
    {
        get => _isCustomized;
        private set => SetProperty(ref _isCustomized, value);
    }

    public bool IsBuiltinAvailable
    {
        get => _isBuiltinAvailable;
        private set => SetProperty(ref _isBuiltinAvailable, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_suppress || _isEnabled == value)
            {
                return;
            }

            // Optimistic UI; parent reverts on failure.
            var previous = _isEnabled;
            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            StatusText = BuildStatusText();
            _ = ApplyEnabledAsync(previous, value);
        }
    }

    public void ApplyDescriptor(SkillDescriptor descriptor)
    {
        _suppress = true;
        try
        {
            Name = descriptor.Name;
            Description = descriptor.Description;
            RootPath = descriptor.RootPath;
            Source = descriptor.Source;
            IsCustomized = descriptor.IsCustomized;
            IsBuiltinAvailable = descriptor.IsBuiltinAvailable;
            if (_isEnabled != descriptor.IsEnabled)
            {
                _isEnabled = descriptor.IsEnabled;
                OnPropertyChanged(nameof(IsEnabled));
            }

            StatusText = BuildStatusText();
        }
        finally
        {
            _suppress = false;
        }
    }

    private async Task ApplyEnabledAsync(bool previous, bool value)
    {
        try
        {
            await _setEnabledAsync(this, value).ConfigureAwait(true);
        }
        catch
        {
            _suppress = true;
            try
            {
                _isEnabled = previous;
                OnPropertyChanged(nameof(IsEnabled));
                StatusText = BuildStatusText();
            }
            finally
            {
                _suppress = false;
            }

            throw;
        }
    }

    private string BuildStatusText()
    {
        var state = _isEnabled ? "已启用" : "未启用";
        var extra = _isCustomized ? " · 已修改" : _isBuiltinAvailable ? " · 内置" : " · 自定义";
        return state + extra;
    }
}
