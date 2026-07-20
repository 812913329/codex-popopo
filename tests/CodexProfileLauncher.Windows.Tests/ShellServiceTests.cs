using System.Runtime.InteropServices;
using CodexProfileLauncher.Infrastructure;

namespace CodexProfileLauncher.Windows.Tests;

[TestClass]
public sealed class ShellServiceTests
{
    [TestMethod]
    public void ClipboardWrite_RetriesTransientContentionThenSucceeds()
    {
        var attempts = 0;
        var delays = new List<int>();

        ShellService.WriteClipboardWithRetry(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new COMException("busy");
                }
            },
            delays.Add);

        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(new[] { 35, 70 }, delays);
    }

    [TestMethod]
    public void ClipboardWrite_StopsAfterBoundedAttemptsWithActionableError()
    {
        var attempts = 0;
        var delays = new List<int>();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ShellService.WriteClipboardWithRetry(
                () =>
                {
                    attempts++;
                    throw new COMException("busy");
                },
                delays.Add,
                maxAttempts: 3));

        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(new[] { 35, 70 }, delays);
        StringAssert.Contains(error.Message, "剪贴板");
        Assert.IsInstanceOfType<ExternalException>(error.InnerException);
    }
}
