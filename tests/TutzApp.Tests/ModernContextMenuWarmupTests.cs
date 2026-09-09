using System.Collections.Generic;
using System.IO;

namespace TutzApp.Tests;

public sealed class ModernContextMenuWarmupTests
{
    [Fact]
    public void AppWarmsThePackagedExplorerCommandBeforeTheFirstUserContextMenu()
    {
        string appSource = File.ReadAllText(FindProjectFile("App.xaml.cs"));
        string warmupSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "ModernContextMenuWarmup.cs"));

        Assert.Contains("ModernContextMenuWarmup.Schedule(sysControl, _cts.Token);", appSource);
        Assert.Contains("F13BF4B5-9064-4971-B73E-1D5EC8F2EAA5", warmupSource);
        Assert.Contains("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9", warmupSource);
        Assert.Contains("CoCreateInstance", warmupSource);
        Assert.Contains("ClsctxLocalServer", warmupSource);
        Assert.Contains("SetApartmentState(ApartmentState.STA)", warmupSource);
        Assert.Contains("WarmupDelayMilliseconds = 250", warmupSource);
        Assert.Contains("RunActivationLoop", warmupSource);
        Assert.Contains("SuspendForPackageDeployment", warmupSource);
        Assert.Contains("ResumeAfterPackageDeployment", warmupSource);
        Assert.Contains("WaitHandle.WaitAny(waitHandles)", warmupSource);
        Assert.Contains("Marshal.Release(commandPointer);", warmupSource);
    }

    [Fact]
    public void PackageDeploymentReleasesAndReactivatesTheWarmupProxyWithoutRestartingTheApp()
    {
        string serviceSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));
        string warmupSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "ModernContextMenuWarmup.cs"));

        Assert.Contains("ModernContextMenuWarmup.SuspendForPackageDeployment();", serviceSource);
        Assert.Contains("ModernContextMenuWarmup.ResumeAfterPackageDeployment();", serviceSource);
        Assert.Contains("StateChanged.Set();", warmupSource);
        Assert.Contains("_deploymentSuspended", warmupSource);
        Assert.Contains("proxy COM liberado para atualização/reinicialização", warmupSource);
    }

    [Fact]
    public void PackageDeploymentStopsOnlyMatchingOldOrCurrentComSurrogates()
    {
        string serviceSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));

        Assert.Contains("TerminalContextMenuComAppId", serviceSource);
        Assert.Contains("LegacyTerminalContextMenuComAppId", serviceSource);
        Assert.Contains("Stop-TutzContextMenuSurrogates", serviceSource);
        Assert.Contains("Get-CimInstance Win32_Process", serviceSource);
        Assert.Contains("Name = 'dllhost.exe'", serviceSource);
        Assert.Contains("$commandLine.IndexOf($appId", serviceSource);
        Assert.DoesNotContain("Get-Process -Name dllhost | Stop-Process", serviceSource);
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
