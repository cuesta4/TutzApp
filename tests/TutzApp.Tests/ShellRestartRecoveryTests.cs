using System.Collections.Generic;
using System.IO;

namespace TutzApp.Tests;

public sealed class ShellRestartRecoveryTests
{
    [Fact]
    public void TrayIcon_IsRegisteredAgainWhenTheExplorerShellProcessChanges()
    {
        string appSource = File.ReadAllText(FindProjectFile("App.xaml.cs"));

        Assert.Contains("GetShellWindow", appSource);
        Assert.Contains("GetWindowThreadProcessId", appSource);
        Assert.Contains("StartExplorerRestartMonitor(sysControl);", appSource);
        Assert.Contains("processo shell do Explorer mudou de PID", appSource);
        Assert.Contains("_notifyIcon.Visible = false;", appSource);
        Assert.Contains("_notifyIcon.Visible = true;", appSource);
        Assert.Contains("for (int attempt = 1; attempt <= 3", appSource);
        Assert.DoesNotContain("RegisterWindowMessage(\"TaskbarCreated\")", appSource);
        Assert.Contains("_explorerRestartMonitor?.Dispose();", appSource);
    }

    private static string FindProjectFile(params string[] relativeParts)
    {
        IEnumerable<string> startingDirectories = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (string startingDirectory in startingDirectories)
        {
            DirectoryInfo? directory = new(startingDirectory);
            while (directory is not null)
            {
                string candidate = directory.FullName;
                foreach (string part in relativeParts)
                {
                    candidate = Path.Combine(candidate, part);
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate project file: {Path.Combine(relativeParts)}");
    }
}
