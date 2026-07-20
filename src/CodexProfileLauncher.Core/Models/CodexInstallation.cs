namespace CodexProfileLauncher.Core.Models;

public sealed record CodexInstallation(
    string PackageFullName,
    string PackageFamilyName,
    Version Version,
    string InstallRoot,
    string ExecutablePath)
{
    public string DisplayVersion => Version.ToString();
}

public sealed record ProcessLaunchHandle(
    System.Diagnostics.Process Process,
    RunningInstanceReceipt Receipt);
