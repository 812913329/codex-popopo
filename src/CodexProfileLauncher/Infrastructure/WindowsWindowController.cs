using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexProfileLauncher.Infrastructure;

public sealed partial class WindowsWindowController
{
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;

    public bool Activate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        process.Refresh();
        var window = process.MainWindowHandle;
        if (window == IntPtr.Zero)
        {
            window = FindTopLevelWindows(process.Id).FirstOrDefault();
        }

        if (window == IntPtr.Zero)
        {
            return false;
        }

        _ = ShowWindowAsync(window, SwRestore);
        return SetForegroundWindow(window);
    }

    public int RequestClose(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        process.Refresh();
        if (process.HasExited)
        {
            return 0;
        }

        var windows = FindTopLevelWindows(process.Id);
        var posted = 0;
        foreach (var window in windows)
        {
            process.Refresh();
            if (process.HasExited)
            {
                break;
            }

            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == process.Id && IsWindowVisible(window) &&
                PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero))
            {
                posted++;
            }
        }

        if (windows.Count == 0 && process.MainWindowHandle != IntPtr.Zero)
        {
            return process.CloseMainWindow() ? 1 : 0;
        }

        return posted;
    }

    public async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    private static List<IntPtr> FindTopLevelWindows(int processId)
    {
        var windows = new List<IntPtr>();
        _ = EnumWindows((window, parameter) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == processId && IsWindowVisible(window))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
