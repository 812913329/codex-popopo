namespace CodexProfileLauncher.Infrastructure;

public sealed class LauncherPaths
{
    public LauncherPaths()
    {
        BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexProfileLauncher");
        StateDirectory = Path.Combine(BaseDirectory, "state");
        DefaultProfilesDirectory = Path.Combine(BaseDirectory, "profiles");
        LogsDirectory = Path.Combine(BaseDirectory, "logs");
        RuntimeCacheDirectory = Path.Combine(BaseDirectory, "runtime-cache");

        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(DefaultProfilesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RuntimeCacheDirectory);
    }

    public string BaseDirectory { get; }

    public string StateDirectory { get; }

    public string DefaultProfilesDirectory { get; }

    public string LogsDirectory { get; }

    public string RuntimeCacheDirectory { get; }

    public string GetSuggestedProfileRoot(Guid profileId) =>
        Path.Combine(DefaultProfilesDirectory, profileId.ToString("N"));
}
