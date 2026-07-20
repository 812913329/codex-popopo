namespace CodexProfileLauncher.Core.Models;

public enum SkillSource
{
    Builtin,
    Environment,
    Disabled,
}

public sealed record SkillDescriptor(
    string Id,
    string Name,
    string Description,
    SkillSource Source,
    string RootPath,
    bool IsEnabled,
    bool IsCustomized,
    bool IsBuiltinAvailable);

public sealed record ProfileSkillsSnapshot(
    IReadOnlyList<SkillDescriptor> Skills,
    int EnabledCount,
    string SkillsDirectory,
    string BuiltinRoot);

public sealed record SkillFrontmatter(
    string Name,
    string Description);

public sealed class ProfileSkillsException : Exception
{
    public ProfileSkillsException(string code, string message, string details, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public string Details { get; }
}
