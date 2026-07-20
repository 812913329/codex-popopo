using System.Text;

namespace CodexProfileLauncher.Core.Services;

/// <summary>
/// Bundled default system prompt used for every profile when Keysmith mode is off.
/// Source: user CLAUDE.md operator-core (copied into Assets/default-system-prompt).
/// </summary>
public static class DefaultSystemPrompt
{
    public const string FileName = "default-system-prompt.md";

    private static readonly Lazy<string> Bundled = new(Load);

    public static string GetBundled() => Bundled.Value;

    private static string Load()
    {
        var assembly = typeof(DefaultSystemPrompt).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(FileName, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("default-system-prompt.md", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("无法打开默认系统提示词资源流。");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return Normalize(text);
            }
        }

        foreach (var candidate in EnumerateDiskCandidates())
        {
            if (File.Exists(candidate))
            {
                return Normalize(File.ReadAllText(candidate, Encoding.UTF8));
            }
        }

        throw new InvalidOperationException(
            "未找到默认系统提示词 default-system-prompt.md。请确认 Assets/default-system-prompt 已嵌入。");
    }

    private static IEnumerable<string> EnumerateDiskCandidates()
    {
        var bases = new List<string> { AppContext.BaseDirectory };
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                bases.Add(dir.FullName);
            }
        }
        catch
        {
            // ignore
        }

        foreach (var root in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "Assets", "default-system-prompt", FileName);
            yield return Path.Combine(root, "assets", "default-system-prompt", FileName);
            yield return Path.Combine(root, FileName);
        }
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
}
