using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace CodexProfileLauncher.Infrastructure;

public sealed class ShellService
{
    public void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。", nameof(path));
        }

        var normalized = Path.GetFullPath(path);
        if (!Directory.Exists(normalized) && !File.Exists(normalized))
        {
            throw new FileNotFoundException("目标路径不存在。", normalized);
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = normalized,
            UseShellExecute = true,
        });
    }

    public void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        WriteClipboardWithRetry(
            () => Clipboard.SetDataObject(text, copy: true),
            milliseconds => Thread.Sleep(milliseconds));
    }

    internal static void WriteClipboardWithRetry(
        Action write,
        Action<int> delay,
        int maxAttempts = 5)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (ExternalException) when (attempt < maxAttempts)
            {
                delay(35 * attempt);
            }
            catch (ExternalException ex)
            {
                throw new InvalidOperationException(
                    "剪贴板正被其他程序占用，请稍后重试。",
                    ex);
            }
        }
    }
}
