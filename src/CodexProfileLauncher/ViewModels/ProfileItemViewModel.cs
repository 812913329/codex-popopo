using CodexProfileLauncher.Core.Models;
using System.Globalization;

namespace CodexProfileLauncher.ViewModels;

public enum ProfileRuntimeState
{
    Ready,
    Launching,
    Running,
    Unknown,
    Error,
}

public sealed class ProfileItemViewModel : ObservableObject
{
    private ProfileRuntimeState _runtimeState = ProfileRuntimeState.Ready;
    private string _runtimeMessage = "已就绪";

    public ProfileItemViewModel(CodexProfile profile)
    {
        Profile = profile;
    }

    public CodexProfile Profile { get; }

    public string Name => Profile.Name;

    public string DataRoot => Profile.DataRoot;

    public string WorkingDirectory => Profile.WorkingDirectory;

    public string CodexHomePath => ProfilePaths.FromRoot(Profile.DataRoot).CodexHome;

    public string AppDataPath => ProfilePaths.FromRoot(Profile.DataRoot).AppData;

    public string ConfigPath => ProfilePaths.FromRoot(Profile.DataRoot).ConfigFile;

    public string DisplayStatus => _runtimeMessage;

    public string StatusGlyph => _runtimeState switch
    {
        ProfileRuntimeState.Running => "\uE768",
        ProfileRuntimeState.Launching => "\uE895",
        ProfileRuntimeState.Unknown => "\uE7BA",
        ProfileRuntimeState.Error => "\uEA39",
        _ => "\uE73E",
    };

    public bool IsRunning =>
        _runtimeState is ProfileRuntimeState.Running or ProfileRuntimeState.Unknown;

    public string LastStartedDisplay => Profile.LastStartedUtc is { } value
        ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : "尚未启动";

    public ProfileRuntimeState RuntimeState => _runtimeState;

    public void UpdateRuntime(ProfileRuntimeState state, string message)
    {
        _runtimeState = state;
        _runtimeMessage = message;
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(IsRunning));
    }

    public void RefreshProfileProperties()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DataRoot));
        OnPropertyChanged(nameof(WorkingDirectory));
        OnPropertyChanged(nameof(CodexHomePath));
        OnPropertyChanged(nameof(AppDataPath));
        OnPropertyChanged(nameof(ConfigPath));
        OnPropertyChanged(nameof(LastStartedDisplay));
    }
}
