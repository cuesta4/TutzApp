using System;
using System.Collections.Generic;
using System.IO;

namespace TutzApp.Tests;

public sealed class ContextMenuRegistryCoverageTests
{
    [Fact]
    public void ClassicContextMenu_UsesTwoIndependentVerifiedStaticVerbs()
    {
        string source = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));

        Assert.Contains("ClassicTerminalNormalKeyName = \"TutzApp.OpenTerminal\"", source);
        Assert.Contains("ClassicTerminalElevatedKeyName = \"TutzApp.OpenTerminalAdmin\"", source);
        Assert.Contains("new(@\"Directory\\Background\\shell\", \"%V\", \"DirectoryBackground\")", source);
        Assert.Contains("$verb = $shellKey.CreateSubKey($normalName, $true)", source);
        Assert.Contains("$verb = $shellKey.CreateSubKey($elevatedName, $true)", source);
        Assert.Contains("$verb.SetValue('MUIVerb', 'Abrir no Terminal'", source);
        Assert.Contains("$verb.SetValue('MUIVerb', 'Abrir no Terminal como administrador'", source);
        Assert.Contains("$command.SetValue('', [string]$op.NormalCommand", source);
        Assert.Contains("$command.SetValue('', [string]$op.ElevatedCommand", source);
        Assert.Contains("$\"{quotedExecutable} --open-terminal {QuoteWindowsArgument(target.ArgumentToken)}\"", source);
        Assert.Contains("$\"{quotedExecutable} --open-terminal-admin {QuoteWindowsArgument(target.ArgumentToken)}\"", source);
        Assert.DoesNotContain("$classesRoot.CreateSubKey($parentName", source);
        Assert.DoesNotContain("$parentKey.CreateSubKey", source);
        Assert.DoesNotContain("SetValue('SubCommands'", source);
        Assert.DoesNotContain("SetValue('Position'", source);
    }

    [Fact]
    public void ClassicStaticVerbs_InvokeTutzAppAsAOneShotCommandHost()
    {
        string contextMenuSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));
        string appSource = File.ReadAllText(FindProjectFile("App.xaml.cs"));
        string systemControlSource = File.ReadAllText(FindProjectFile(
            "src", "Infrastructure", "System", "SystemControlService.cs"));

        int oneShotCall = appSource.IndexOf(
            "TryRunOneShotTerminalCommand(e.Args",
            StringComparison.Ordinal);
        int baseStartup = appSource.IndexOf("base.OnStartup(e);", StringComparison.Ordinal);
        int mutexAcquisition = appSource.IndexOf("TryAcquireSingleInstance();", StringComparison.Ordinal);

        Assert.True(oneShotCall >= 0);
        Assert.True(baseStartup > oneShotCall);
        Assert.True(mutexAcquisition > oneShotCall);
        Assert.Contains("TryParseOneShotTerminalCommand", appSource);
        Assert.Contains("--open-terminal-admin", appSource);
        Assert.Contains("SystemControlService.TryStartTerminalProcess", appSource);
        Assert.Contains("internal static bool TryStartTerminalProcess", systemControlSource);
        Assert.Contains("WorkingDirectory = resolvedDirectory", systemControlSource);
        Assert.Contains("psi.Verb = \"runas\"", systemControlSource);

        Assert.DoesNotContain("ClassicTerminalLauncherScript", contextMenuSource);
        Assert.DoesNotContain("GetWindowsPowerShellPath", contextMenuSource);
        Assert.DoesNotContain("launcherBase64", contextMenuSource);
        Assert.DoesNotContain("WriteAllBytes($legacyLauncherPath", contextMenuSource);
        Assert.Contains("Remove-Item -LiteralPath $legacyLauncherPath -Force", contextMenuSource);
    }

    [Fact]
    public void ElevatedNormalTerminalLaunch_UsesMicrosoftExecuteInExplorerPattern()
    {
        string systemControlSource = File.ReadAllText(FindProjectFile(
            "src", "Infrastructure", "System", "SystemControlService.cs"));
        string launcherSource = File.ReadAllText(FindProjectFile(
            "src", "Infrastructure", "System", "ExplorerProcessLauncher.cs"));

        Assert.Contains("if (!elevated && IsCurrentProcessElevated())", systemControlSource);
        Assert.Contains("ExplorerProcessLauncher.TryShellExecute", systemControlSource);
        Assert.Contains("psi.Verb = \"runas\"", systemControlSource);

        Assert.Contains("new CShellWindows()", launcherSource);
        Assert.Contains("FindWindowSW", launcherSource);
        Assert.Contains("SidSTopLevelBrowser", launcherSource);
        Assert.Contains("QueryActiveShellView", launcherSource);
        Assert.Contains("GetItemObject(", launcherSource);
        Assert.Contains("SvgioBackground", launcherSource);
        Assert.Contains("shellDispatch.ShellExecute", launcherSource);
        Assert.Contains("Thread.CurrentThread.GetApartmentState() == ApartmentState.STA", launcherSource);
        Assert.DoesNotContain("CreateProcessAsUser", launcherSource);
        Assert.DoesNotContain("powershell.exe", launcherSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassicStaticVerbs_CannotChangeTheDefaultFolderVerb()
    {
        string source = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));
        int activeTargetsStart = source.IndexOf(
            "private static readonly TerminalContextMenuTarget[] TerminalContextMenuTargets",
            StringComparison.Ordinal);
        int cleanupTargetsStart = source.IndexOf(
            "private static readonly TerminalContextMenuTarget[] ClassicTerminalContextMenuCleanupTargets",
            activeTargetsStart,
            StringComparison.Ordinal);
        string activeTargets = source[activeTargetsStart..cleanupTargetsStart];

        Assert.Contains("ClassicTerminalContextMenuCleanupTargets", source);
        Assert.Contains("HasAnyPerUserClassicTerminalContextMenuResidue", source);
        Assert.Contains("DeleteEmptyCurrentUserRegistryPath", source);
        Assert.Contains("RegistryHive.LocalMachine", source);
        Assert.Contains("matching the verified Python probe", source);
        Assert.Contains(@"Directory\Background\shell", activeTargets);
        Assert.DoesNotContain(@"Directory\shell", activeTargets);
        Assert.DoesNotContain(@"Folder\shell", activeTargets);
        Assert.DoesNotContain(@"Drive\shell", activeTargets);
        Assert.DoesNotContain("ClassicShellNoDefaultVerb", source);
        Assert.DoesNotContain("shellKey.SetValue('',", source);
        Assert.DoesNotContain("shellKey.SetValue(null", source);
        Assert.DoesNotContain("shellKey.DeleteValue('',", source);
        Assert.DoesNotContain("Registry.CurrentUser.CreateSubKey(shellPath", source);
    }

    [Fact]
    public void RecoveryScript_RemovesStalePerUserShellOverridesWithoutDeletingWholeClasses()
    {
        string source = File.ReadAllText(FindProjectFile("Repair-TutzApp-Classic-Menu.ps1"));

        Assert.Contains("Software\\Classes\\Folder", source);
        Assert.Contains("Software\\Classes\\Drive", source);
        Assert.Contains("TutzApp.Terminal", source);
        Assert.Contains("TutzApp.OpenTerminal", source);
        Assert.Contains("default value of every shell container is intentionally untouched", source);
        Assert.Contains("RegistryView]::Registry64", source);
        Assert.Contains("RegistryView]::Registry32", source);
        Assert.Contains("SHChangeNotify", source);
        Assert.DoesNotContain("DeleteValue('',", source);
        Assert.DoesNotContain("SetValue('',", source);
        Assert.DoesNotContain("Remove-Item -LiteralPath 'HKCU:\\Software\\Classes\\Folder' -Recurse", source);
        Assert.DoesNotContain("Remove-Item -LiteralPath 'HKCU:\\Software\\Classes\\Drive' -Recurse", source);
    }

    [Fact]
    public void ExternalMenuScanner_CoversDynamicClassicRegistrationLocationsAndRegistryViews()
    {
        string source = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ExternalContextMenu.cs"));

        Assert.Contains("DiscoverClassicContextMenuClassRoots", source);
        Assert.Contains("SystemFileAssociations", source);
        Assert.Contains("Applications", source);
        Assert.Contains("RegistryView.Registry64", source);
        Assert.Contains("RegistryView.Registry32", source);
        Assert.Contains("DelegateExecute", source);
        Assert.Contains("ExplorerCommandHandler", source);
        Assert.Contains("ExtendedSubCommandsKey", source);
        Assert.Contains(@"shellex\ContextMenuHandlers", source);
    }


    [Fact]
    public void ExternalMenuChanges_AreGroupedByApplicationAndAppliedAsOneBatch()
    {
        string source = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ExternalContextMenu.cs"));

        Assert.Contains("ExternalMenuControlApplicationGroup", source);
        Assert.Contains("AggregateExplorerContextMenuEntries", source);
        Assert.Contains("SetExplorerContextMenuApplicationEnabled", source);
        Assert.Contains("SetMachineClassicVerbEntriesLegacyDisable", source);
        Assert.Contains("canonicalProducts", source);
    }

    [Fact]
    public void SparsePackageDeployment_DoesNotTerminateOrRelaunchTheGui()
    {
        string serviceSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));
        string appSource = File.ReadAllText(FindProjectFile("App.xaml.cs"));

        Assert.Contains("ScheduleTerminalContextMenuPackageOperation", serviceSource);
        Assert.Contains("MonitorTerminalContextMenuPackageOperationAsync", serviceSource);
        Assert.Contains("The GUI remains running; only the dedicated COM surrogate will be stopped.", serviceSource);
        Assert.Contains("O TutzApp permanecerá aberto", serviceSource);
        Assert.DoesNotContain("$parentPid", serviceSource);
        Assert.DoesNotContain("$appExecutable", serviceSource);
        Assert.DoesNotContain("ApplicationRestartRequested", serviceSource);
        Assert.DoesNotContain("ApplicationRestartRequested", appSource);
        Assert.DoesNotContain("ApplicationRestartRequested", File.ReadAllText(FindProjectFile(
            "src", "Infrastructure", "System", "ISystemControlService.cs")));
        Assert.DoesNotContain("Start-Process -FilePath $appExecutable", serviceSource);
        Assert.DoesNotContain("TutzApp relaunch requested", serviceSource);
    }

    [Fact]
    public void ClassicMenuPowerShell_FlattensJsonArraysBeforeUsingRegistryPaths()
    {
        string contextMenuSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));
        string externalMenuSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ExternalContextMenu.cs"));

        Assert.Contains("$parsedOperations = ConvertFrom-Json", contextMenuSource);
        Assert.Contains("$operations = @($parsedOperations | ForEach-Object { $_ })", contextMenuSource);
        Assert.Contains("Quantidade inválida de alvos do menu clássico", contextMenuSource);
        Assert.Contains("$path = [string]$op.ClassShellPath", contextMenuSource);
        Assert.Contains("$classesRoot.CreateSubKey($path, $true)", contextMenuSource);
        Assert.Contains("$parsedLegacyNames = ConvertFrom-Json", contextMenuSource);
        Assert.Contains("$legacyNames = @($parsedLegacyNames | ForEach-Object { [string]$_ })", contextMenuSource);
        Assert.Contains("Quantidade inválida de alterações de menu", externalMenuSource);
        Assert.Contains("$operations = @($parsedOperations | ForEach-Object { $_ })", externalMenuSource);
        Assert.DoesNotContain("@(ConvertFrom-Json", contextMenuSource);
        Assert.DoesNotContain("@(ConvertFrom-Json", externalMenuSource);
    }

    [Fact]
    public void ClassicStaticVerbs_UseTheNativeClassesViewAndCommandStoreOnlyForCleanup()
    {
        string source = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));

        Assert.Contains("$classView = if ([Environment]::Is64BitOperatingSystem)", source);
        Assert.Contains("[Microsoft.Win32.RegistryView]::Registry64", source);
        Assert.Contains("Software\\Classes is shared between registry views", source);
        Assert.Contains("CommandStore is cleanup-only", source);
        Assert.Contains("$commandStore.DeleteSubKeyTree($legacyName, $false)", source);
        Assert.DoesNotContain("$commandStore.CreateSubKey", source);
        Assert.DoesNotContain("CreateSubKey('ExtendedSubCommandsKey'", source);
        Assert.DoesNotContain("CreateSubKey('Shell'", source);
    }

    [Fact]
    public void ShellChanges_UseSynchronousAssociationFlushAndSettingBroadcasts()
    {
        string contextMenuSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));
        string nativeSource = File.ReadAllText(FindProjectFile(
            "src", "Core", "Interop", "NativeMethods.cs"));

        Assert.Contains("SHCNF_FLUSH", contextMenuSource);
        Assert.Contains("Software\\Classes", contextMenuSource);
        Assert.Contains("Shell Extensions", contextMenuSource);
        Assert.Contains("SendMessageTimeout", nativeSource);
    }

    [Fact]
    public void SparsePackageDeployment_WaitsForAStableStateWithoutRelaunching()
    {
        string source = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));

        Assert.Contains("$stableChecks = 0", source);
        Assert.Contains("while ($stableChecks -lt 8)", source);
        Assert.Contains("if (($present -eq $expectedPresent) -and $statusIsHealthy)", source);
        Assert.Contains("$finalPackage.Status", source);
        Assert.Contains("Start-Sleep -Seconds 5", source);
        Assert.Contains("$succeeded = $true", source);
        Assert.Contains("if ($succeeded) { exit 0 } else { exit 1 }", source);
        Assert.DoesNotContain("-ForceApplicationShutdown", source);
        Assert.DoesNotContain("Automatic relaunch", source);
        Assert.DoesNotContain("Start-Process -FilePath $appPath", source);
        Assert.DoesNotContain("Wait-Process -Id $mainPid", source);
    }

    [Fact]
    public void NativeExplorerCommand_RemainsCompatibleWithExceptionsDisabledBuild()
    {
        string source = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Native", "ExplorerCommand.cpp"));

        Assert.DoesNotContain("try\n", source);
        Assert.DoesNotContain("catch (", source);
        Assert.Contains(
            "const HRESULT hr = LaunchTerminal(items, kind_ == CommandKind::Elevated);",
            source);
        Assert.Contains("TraceNativeEvent(L\"Invoke\", kind_, hr);", source);
        Assert.Contains("return hr;", source);
    }

    [Fact]
    public void ManagedClassicEntries_PersistTheirRegistryViewForCorrectRestoration()
    {
        string modelSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Models", "ExplorerContextMenuEntry.cs"));
        string serviceSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Services", "SystemControlService.ExternalContextMenu.cs"));

        Assert.Contains("public string? RegistryView", modelSource);
        Assert.Contains("ParseRegistryView(entry.RegistryView)", serviceSource);
        Assert.Contains("SetValue(\"RegistryView\"", serviceSource);
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
