using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TutzApp.Models;

namespace TutzApp.Services
{
    public partial class SystemControlService
    {
        private const string ExternalMenuBlockedRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
        private const string ExternalMenuManagedRegistryPath = TerminalContextMenuInstallRegistryPath + @"\ManagedExternalEntries";
        private const string ExternalMenuControlBlockedClsid = "blocked-clsid";
        private const string ExternalMenuControlClassicVerb = "classic-verb";
        private const string ExternalMenuControlApplicationGroup = "application-group";
        private const string WindowsTerminalPackagePrefix = "Microsoft.WindowsTerminal";
        private const string WindowsTerminalContextMenuClsid = "{9F156763-7844-4DC4-B2B1-901F640F5155}";

        private static readonly string[] ExternalMenuClassRoots =
        {
            "*",
            "AllFilesystemObjects",
            "Directory",
            @"Directory\Background",
            "Drive",
            "Folder",
            "LibraryFolder",
            @"LibraryFolder\Background",
            "DesktopBackground"
        };

        private static readonly HashSet<string> ProtectedWindowsVerbNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "open",
            "opennewwindow",
            "explore",
            "find",
            "print",
            "printto",
            "properties",
            "runas",
            "pintohome",
            "pintohomefile",
            "unpinfromhome",
            "copyaspath",
            "windows.share"
        };

        public IReadOnlyList<ExplorerContextMenuEntry> GetExplorerContextMenuEntries()
        {
            try
            {
                return AggregateExplorerContextMenuEntries(GetRawExplorerContextMenuEntries());
            }
            catch (Exception ex)
            {
                LogDebug($"GetExplorerContextMenuEntries: falha geral: {ex}");
                return Array.Empty<ExplorerContextMenuEntry>();
            }
        }

        private IReadOnlyList<ExplorerContextMenuEntry> GetRawExplorerContextMenuEntries()
        {
            var entries = new List<ExplorerContextMenuEntry>();
            var handlers = new Dictionary<string, HandlerScanRecord>(StringComparer.OrdinalIgnoreCase);

            ScanClassicContextMenuHive(RegistryHive.CurrentUser, "HKCU", entries, handlers);
            ScanClassicContextMenuHive(RegistryHive.LocalMachine, "HKLM", entries, handlers);

            foreach (HandlerScanRecord handler in handlers.Values)
            {
                ExplorerContextMenuEntry? entry = CreateHandlerEntry(handler);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }

            entries.AddRange(GetPackagedExplorerContextMenuEntries());

            return entries
                .Where(entry => !entry.ApplicationName.Contains("TutzApp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private IReadOnlyList<ExplorerContextMenuEntry> AggregateExplorerContextMenuEntries(
            IReadOnlyList<ExplorerContextMenuEntry> rawEntries)
        {
            ExplorerContextMenuEntry[] registrations = rawEntries
                .GroupBy(GetExternalControlTargetKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(entry => entry.Kind.StartsWith("Menu moderno", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(entry => entry.ApplicationName.Length)
                    .First())
                .ToArray();

            return registrations
                .GroupBy(GetExternalApplicationGroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(CreateApplicationGroupEntry)
                .OrderBy(entry => entry.ApplicationName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        private ExplorerContextMenuEntry CreateApplicationGroupEntry(
            IGrouping<string, ExplorerContextMenuEntry> group)
        {
            ExplorerContextMenuEntry[] registrations = group.ToArray();
            ExplorerContextMenuEntry representative = registrations
                .OrderByDescending(entry => entry.Kind.StartsWith("Menu moderno", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(entry => entry.IsEnabled)
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .First();

            string[] displayNames = registrations
                .Select(entry => entry.DisplayName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string[] kinds = registrations
                .Select(entry => entry.Kind)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string[] targets = registrations
                .SelectMany(entry => entry.Target.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            int enabledCount = registrations.Count(entry => entry.IsEnabled);
            bool anyManaged = registrations.Any(entry => entry.IsManagedByTutzApp);
            bool anyToggleable = registrations.Any(entry => entry.CanToggle && (entry.IsEnabled || entry.IsManagedByTutzApp));
            bool partiallyEnabled = enabledCount > 0 && enabledCount < registrations.Length;
            string displayName = displayNames.Length switch
            {
                0 => representative.DisplayName,
                1 => displayNames[0],
                _ => $"{displayNames[0]} e mais {displayNames.Length - 1}"
            };
            string kind = kinds.Length == 0 ? representative.Kind : string.Join(" + ", kinds);
            string target = targets.Length switch
            {
                0 => representative.Target,
                <= 4 => string.Join(", ", targets),
                _ => string.Join(", ", targets.Take(4)) + $" e mais {targets.Length - 4}"
            };
            string description = registrations.Length == 1
                ? representative.Description
                : $"{registrations.Length} registros do menu pertencem a este aplicativo. " +
                  $"Comandos: {string.Join(", ", displayNames)}.";

            return new ExplorerContextMenuEntry(
                CreateStableExternalEntryId("application|" + group.Key),
                displayName,
                representative.ApplicationName,
                description,
                kind,
                target,
                isEnabled: enabledCount > 0,
                isManagedByTutzApp: anyManaged,
                canToggle: anyToggleable,
                requiresElevation: registrations.Any(entry => entry.RequiresElevation),
                controlMode: ExternalMenuControlApplicationGroup,
                applicationKey: group.Key,
                registrationCount: registrations.Length,
                isPartiallyEnabled: partiallyEnabled);
        }

        public bool SetExplorerContextMenuEntryEnabled(ExplorerContextMenuEntry entry, bool enabled)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !entry.CanToggle)
            {
                return false;
            }

            try
            {
                bool success = entry.ControlMode.Equals(
                    ExternalMenuControlApplicationGroup,
                    StringComparison.OrdinalIgnoreCase)
                    ? SetExplorerContextMenuApplicationEnabled(entry, enabled)
                    : SetSingleExplorerContextMenuEntryEnabled(entry, enabled);

                // A grouped operation can partially succeed. Flush shell caches even
                // when one registration failed so every successful change is visible.
                NotifyShellAssociationChanged();
                LogDebug($"SetExplorerContextMenuEntryEnabled: id='{entry.Id}', app='{entry.ApplicationName}', enabled={enabled}, mode='{entry.ControlMode}', success={success}.");
                return success;
            }
            catch (Exception ex)
            {
                LogDebug($"SetExplorerContextMenuEntryEnabled: falha para '{entry.DisplayName}': {ex}");
                return false;
            }
        }

        private bool SetExplorerContextMenuApplicationEnabled(
            ExplorerContextMenuEntry aggregateEntry,
            bool enabled)
        {
            ExplorerContextMenuEntry[] targets = GetRawExplorerContextMenuEntries()
                .Where(entry => GetExternalApplicationGroupKey(entry).Equals(
                    aggregateEntry.ApplicationKey,
                    StringComparison.OrdinalIgnoreCase))
                .GroupBy(GetExternalControlTargetKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Where(entry => enabled
                    ? entry.IsManagedByTutzApp
                    : entry.IsEnabled && entry.CanToggle)
                .ToArray();

            if (targets.Length == 0)
            {
                LogDebug($"SetExplorerContextMenuApplicationEnabled: nenhum registro acionável para '{aggregateEntry.ApplicationName}', enabled={enabled}.");
                return false;
            }

            ExplorerContextMenuEntry[] machineClassicTargets = targets
                .Where(entry => entry.ControlMode.Equals(ExternalMenuControlClassicVerb, StringComparison.OrdinalIgnoreCase) &&
                    entry.RegistryHive?.Equals("HKLM", StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            ExplorerContextMenuEntry[] directTargets = targets
                .Except(machineClassicTargets)
                .ToArray();

            if (machineClassicTargets.Length > 0)
            {
                if (!enabled)
                {
                    var savedTargets = new List<ExplorerContextMenuEntry>(machineClassicTargets.Length);
                    foreach (ExplorerContextMenuEntry target in machineClassicTargets)
                    {
                        if (!SaveManagedExternalEntry(target))
                        {
                            foreach (ExplorerContextMenuEntry savedTarget in savedTargets)
                            {
                                DeleteManagedExternalEntry(savedTarget.Id);
                            }

                            LogDebug($"SetExplorerContextMenuApplicationEnabled: falha ao salvar estado reversível de '{target.Id}'.");
                            return false;
                        }

                        savedTargets.Add(target);
                    }
                }

                if (!SetMachineClassicVerbEntriesLegacyDisable(
                    machineClassicTargets,
                    disable: !enabled))
                {
                    if (!enabled)
                    {
                        foreach (ExplorerContextMenuEntry target in machineClassicTargets)
                        {
                            DeleteManagedExternalEntry(target.Id);
                        }
                    }

                    return false;
                }

                if (enabled)
                {
                    foreach (ExplorerContextMenuEntry target in machineClassicTargets)
                    {
                        DeleteManagedExternalEntry(target.Id);
                    }
                }
            }

            bool allSucceeded = true;
            foreach (ExplorerContextMenuEntry target in directTargets)
            {
                if (!SetSingleExplorerContextMenuEntryEnabled(target, enabled))
                {
                    allSucceeded = false;
                    LogDebug($"SetExplorerContextMenuApplicationEnabled: falha no registro '{target.Id}' ({target.Kind}, {target.Target}).");
                }
            }

            LogDebug($"SetExplorerContextMenuApplicationEnabled: app='{aggregateEntry.ApplicationName}', enabled={enabled}, targets={targets.Length}, machineTargets={machineClassicTargets.Length}, success={allSucceeded}.");
            return allSucceeded;
        }

        private bool SetSingleExplorerContextMenuEntryEnabled(
            ExplorerContextMenuEntry entry,
            bool enabled)
        {
            return entry.ControlMode switch
            {
                ExternalMenuControlBlockedClsid => SetBlockedClsidEntryEnabled(entry, enabled),
                ExternalMenuControlClassicVerb => SetClassicVerbEntryEnabled(entry, enabled),
                _ => false
            };
        }

        private void ScanClassicContextMenuHive(
            RegistryHive hive,
            string hiveName,
            ICollection<ExplorerContextMenuEntry> entries,
            IDictionary<string, HandlerScanRecord> handlers)
        {
            foreach (RegistryView registryView in GetRegistryViewsToScan())
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, registryView);
                    using RegistryKey? classesKey = baseKey.OpenSubKey(@"Software\Classes", writable: false);
                    if (classesKey == null)
                    {
                        continue;
                    }

                    foreach (string classRoot in DiscoverClassicContextMenuClassRoots(classesKey))
                    {
                        ScanClassicVerbRoot(classesKey, hiveName, registryView, classRoot, entries, handlers);
                        ScanContextMenuHandlerRoot(classesKey, hiveName, registryView, classRoot, handlers);
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"ScanClassicContextMenuHive: falha em {hiveName} ({GetRegistryViewLabel(registryView)}): {ex.Message}");
                }
            }
        }

        private static IEnumerable<RegistryView> GetRegistryViewsToScan()
        {
            if (!Environment.Is64BitOperatingSystem)
            {
                yield return RegistryView.Default;
                yield break;
            }

            yield return RegistryView.Registry64;

            // Both machine-wide and per-user 32-bit installers can write to the
            // redirected Classes view. Alternative Explorer hosts may read that
            // view even when the native Windows 11 menu does not show the entry.
            yield return RegistryView.Registry32;
        }

        private static IReadOnlyList<string> DiscoverClassicContextMenuClassRoots(RegistryKey classesKey)
        {
            var roots = new HashSet<string>(ExternalMenuClassRoots, StringComparer.OrdinalIgnoreCase);
            var excludedTopLevelRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AppID",
                "ActivatableClasses",
                "CLSID",
                "Component Categories",
                "Installer",
                "Interface",
                "MIME",
                "PackagedCom",
                "TypeLib",
                "WOW6432Node"
            };

            foreach (string rootName in SafeGetSubKeyNames(classesKey))
            {
                if (excludedTopLevelRoots.Contains(rootName) ||
                    rootName.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase) ||
                    rootName.Equals("Applications", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (HasClassicContextMenuRegistration(classesKey, rootName))
                {
                    roots.Add(rootName);
                }
            }

            AddNestedClassicContextMenuRoots(classesKey, "SystemFileAssociations", roots);
            AddNestedClassicContextMenuRoots(classesKey, "Applications", roots);

            return roots
                .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void AddNestedClassicContextMenuRoots(
            RegistryKey classesKey,
            string containerPath,
            ISet<string> roots)
        {
            try
            {
                using RegistryKey? container = classesKey.OpenSubKey(containerPath, writable: false);
                if (container == null)
                {
                    return;
                }

                foreach (string childName in SafeGetSubKeyNames(container))
                {
                    string candidate = containerPath + "\\" + childName;
                    if (HasClassicContextMenuRegistration(classesKey, candidate))
                    {
                        roots.Add(candidate);
                    }
                }
            }
            catch
            {
                // Registry inventories are best effort; inaccessible classes are skipped.
            }
        }

        private static bool HasClassicContextMenuRegistration(RegistryKey classesKey, string classRoot)
        {
            try
            {
                using RegistryKey? shell = classesKey.OpenSubKey(classRoot + @"\shell", writable: false);
                if (shell != null && SafeGetSubKeyNames(shell).Length > 0)
                {
                    return true;
                }

                using RegistryKey? handlers = classesKey.OpenSubKey(
                    classRoot + @"\shellex\ContextMenuHandlers",
                    writable: false);
                return handlers != null && SafeGetSubKeyNames(handlers).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string[] SafeGetSubKeyNames(RegistryKey key)
        {
            try
            {
                return key.GetSubKeyNames();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private void ScanClassicVerbRoot(
            RegistryKey classesKey,
            string hiveName,
            RegistryView registryView,
            string classRoot,
            ICollection<ExplorerContextMenuEntry> entries,
            IDictionary<string, HandlerScanRecord> handlers)
        {
            string shellRelativePath = classRoot + @"\shell";
            using RegistryKey? shellKey = classesKey.OpenSubKey(shellRelativePath, writable: false);
            if (shellKey == null)
            {
                return;
            }

            foreach (string verbName in SafeGetSubKeyNames(shellKey))
            {
                string relativePath = shellRelativePath + "\\" + verbName;
                using RegistryKey? verbKey = classesKey.OpenSubKey(relativePath, writable: false);
                if (verbKey == null || IsTutzAppRegistration(verbName, verbKey))
                {
                    continue;
                }

                string displayName = ReadRegistryText(verbKey, "MUIVerb") ??
                    ReadRegistryText(verbKey, null) ??
                    HumanizeIdentifier(verbName);

                using RegistryKey? commandKey = verbKey.OpenSubKey("command", writable: false);
                string command = ReadRegistryText(commandKey, null) ?? string.Empty;
                string explorerCommandHandler = NormalizeClsid(
                    ReadRegistryText(verbKey, "ExplorerCommandHandler") ??
                    ReadRegistryText(commandKey, "ExplorerCommandHandler"));
                string delegateExecute = NormalizeClsid(ReadRegistryText(commandKey, "DelegateExecute"));
                bool hasCascadingChildren = HasRegistryValue(verbKey, "SubCommands") ||
                    HasRegistryValue(verbKey, "ExtendedSubCommandsKey") ||
                    HasRegistrySubKey(verbKey, "ExtendedSubCommandsKey") ||
                    HasRegistrySubKey(verbKey, "shell");

                if (!string.IsNullOrWhiteSpace(explorerCommandHandler))
                {
                    AddHandlerRecord(
                        handlers,
                        explorerCommandHandler,
                        displayName,
                        verbName,
                        classRoot,
                        hiveName,
                        registryView,
                        command);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(command) &&
                    string.IsNullOrWhiteSpace(delegateExecute) &&
                    !hasCascadingChildren)
                {
                    continue;
                }

                if (ShouldSkipProtectedClassicVerb(verbName, command))
                {
                    continue;
                }

                string applicationHint = command;
                string delegateDisplayName = string.Empty;
                if (string.IsNullOrWhiteSpace(applicationHint) && !string.IsNullOrWhiteSpace(delegateExecute))
                {
                    ResolveComClassMetadata(delegateExecute, out delegateDisplayName, out applicationHint);
                }

                string fullRegistryPath = @"Software\Classes\" + relativePath;
                string idMaterial = registryView == RegistryView.Registry32
                    ? $"verb|{hiveName}|32-bit|{fullRegistryPath}"
                    : $"verb|{hiveName}|{fullRegistryPath}";
                string id = CreateStableExternalEntryId(idMaterial);
                bool isManaged = IsExternalEntryManaged(id);
                bool disabled = HasRegistryValue(verbKey, "LegacyDisable") ||
                    HasRegistryValue(verbKey, "ProgrammaticAccessOnly");
                string effectiveDisplayName = string.IsNullOrWhiteSpace(delegateDisplayName)
                    ? displayName
                    : delegateDisplayName;
                string applicationName = ResolveApplicationName(effectiveDisplayName, applicationHint, verbName);
                string target = FormatTargetWithRegistryView(HumanizeClassRoot(classRoot), registryView);
                string description = hasCascadingChildren
                    ? $"Submenu clássico registrado por {applicationName}."
                    : !string.IsNullOrWhiteSpace(command)
                        ? $"Comando clássico: {AbbreviateCommand(command)}"
                        : $"Comando clássico DelegateExecute {delegateExecute}.";

                entries.Add(new ExplorerContextMenuEntry(
                    id,
                    NormalizeDisplayName(displayName, verbName),
                    applicationName,
                    description,
                    "Verb clássico",
                    target,
                    isEnabled: !disabled,
                    isManagedByTutzApp: isManaged,
                    canToggle: !disabled || isManaged,
                    requiresElevation: hiveName.Equals("HKLM", StringComparison.OrdinalIgnoreCase),
                    controlMode: ExternalMenuControlClassicVerb,
                    registryHive: hiveName,
                    registryPath: fullRegistryPath,
                    registryView: GetRegistryViewLabel(registryView)));
            }
        }

        private void ScanContextMenuHandlerRoot(
            RegistryKey classesKey,
            string hiveName,
            RegistryView registryView,
            string classRoot,
            IDictionary<string, HandlerScanRecord> handlers)
        {
            string handlersRelativePath = classRoot + @"\shellex\ContextMenuHandlers";
            using RegistryKey? handlersKey = classesKey.OpenSubKey(handlersRelativePath, writable: false);
            if (handlersKey == null)
            {
                return;
            }

            foreach (string handlerName in SafeGetSubKeyNames(handlersKey))
            {
                using RegistryKey? handlerKey = handlersKey.OpenSubKey(handlerName, writable: false);
                string clsid = NormalizeClsid(ReadRegistryText(handlerKey, null));
                if (string.IsNullOrWhiteSpace(clsid))
                {
                    clsid = NormalizeClsid(handlerName);
                }

                if (string.IsNullOrWhiteSpace(clsid))
                {
                    continue;
                }

                AddHandlerRecord(
                    handlers,
                    clsid,
                    handlerName,
                    handlerName,
                    classRoot,
                    hiveName,
                    registryView,
                    commandHint: string.Empty);
            }
        }

        private void AddHandlerRecord(
            IDictionary<string, HandlerScanRecord> handlers,
            string clsid,
            string displayName,
            string handlerName,
            string classRoot,
            string hiveName,
            RegistryView registryView,
            string commandHint)
        {
            if (!handlers.TryGetValue(clsid, out HandlerScanRecord? record))
            {
                record = new HandlerScanRecord(clsid, displayName, handlerName, hiveName, commandHint);
                handlers.Add(clsid, record);
            }

            record.Targets.Add(FormatTargetWithRegistryView(HumanizeClassRoot(classRoot), registryView));
            if (string.IsNullOrWhiteSpace(record.DisplayName) || LooksLikeClsid(record.DisplayName))
            {
                record.DisplayName = displayName;
            }
        }

        private static string FormatTargetWithRegistryView(string target, RegistryView registryView)
        {
            return registryView == RegistryView.Registry32
                ? target + " (registro 32-bit)"
                : target;
        }

        private static string GetRegistryViewLabel(RegistryView registryView)
        {
            return registryView switch
            {
                RegistryView.Registry32 => "32-bit",
                RegistryView.Registry64 => "64-bit",
                _ => "padrão"
            };
        }

        private static RegistryView ParseRegistryView(string? registryView)
        {
            return registryView?.Trim().ToLowerInvariant() switch
            {
                "32-bit" => RegistryView.Registry32,
                "64-bit" => RegistryView.Registry64,
                _ => RegistryView.Default
            };
        }

        private ExplorerContextMenuEntry? CreateHandlerEntry(HandlerScanRecord handler)
        {
            ResolveComClassMetadata(handler.Clsid, out string classDisplayName, out string serverPath);
            string displayName = NormalizeDisplayName(
                string.IsNullOrWhiteSpace(classDisplayName) ? handler.DisplayName : classDisplayName,
                handler.HandlerName);
            string applicationName = ResolveApplicationName(displayName, serverPath, handler.HandlerName);

            if (ShouldSkipSystemShellHandler(displayName, applicationName, serverPath, handler.HandlerName))
            {
                return null;
            }

            string id = CreateStableExternalEntryId("clsid|" + handler.Clsid);
            bool managed = IsExternalEntryManaged(id);
            bool blocked = IsClsidBlocked(handler.Clsid);
            string target = string.Join(", ", handler.Targets.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase));
            string description = string.IsNullOrWhiteSpace(serverPath)
                ? $"Extensão COM do Explorer ({handler.Clsid})."
                : $"Extensão COM: {AbbreviatePath(serverPath)}";

            return new ExplorerContextMenuEntry(
                id,
                displayName,
                applicationName,
                description,
                "Extensão do Explorer",
                target,
                isEnabled: !blocked,
                isManagedByTutzApp: managed,
                canToggle: !blocked || managed,
                requiresElevation: false,
                controlMode: ExternalMenuControlBlockedClsid,
                clsid: handler.Clsid);
        }

        private IReadOnlyList<ExplorerContextMenuEntry> GetPackagedExplorerContextMenuEntries()
        {
            const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$items = New-Object System.Collections.Generic.List[object]
$packages = @(Get-AppxPackage)
$packages | ForEach-Object {
    $package = $_
    try {
        [xml]$manifest = Get-AppxPackageManifest -Package $package.PackageFullName
        $displayName = [string]$manifest.Package.Properties.DisplayName
        $publisherDisplayName = [string]$manifest.Package.Properties.PublisherDisplayName
        $extensions = $manifest.SelectNodes(""//*[local-name()='Extension' and @Category='windows.fileExplorerContextMenus']"")
        foreach ($extension in $extensions) {
            $verbs = $extension.SelectNodes("".//*[local-name()='Verb']"")
            foreach ($verb in $verbs) {
                $clsid = [string]$verb.GetAttribute('Clsid')
                if ([string]::IsNullOrWhiteSpace($clsid)) { continue }
                $verbId = [string]$verb.GetAttribute('Id')
                $itemType = ''
                if ($null -ne $verb.ParentNode -and $null -ne $verb.ParentNode.Attributes['Type']) {
                    $itemType = [string]$verb.ParentNode.Attributes['Type'].Value
                }
                $items.Add([pscustomobject]@{
                    PackageName = [string]$package.Name
                    PackageFullName = [string]$package.PackageFullName
                    DisplayName = $displayName
                    PublisherDisplayName = $publisherDisplayName
                    VerbId = $verbId
                    Clsid = $clsid
                    ItemType = $itemType
                })
            }
        }
    } catch { }
}

# Windows Terminal is a Store/MSIX package and its Explorer command uses a stable
# packaged COM class. Keep a fallback because some Windows builds fail to return
# desktop4/desktop5 extension nodes through Get-AppxPackageManifest even though
# the package and shell extension are registered.
$terminalPackage = $packages |
    Where-Object { $_.Name -like 'Microsoft.WindowsTerminal*' } |
    Sort-Object Version -Descending |
    Select-Object -First 1
$terminalClsid = '{9F156763-7844-4DC4-B2B1-901F640F5155}'
$terminalAlreadyListed = @($items | Where-Object {
    ([string]$_.Clsid).Trim().Equals($terminalClsid, [System.StringComparison]::OrdinalIgnoreCase)
}).Count -gt 0
if ($null -ne $terminalPackage -and -not $terminalAlreadyListed) {
    $items.Add([pscustomobject]@{
        PackageName = [string]$terminalPackage.Name
        PackageFullName = [string]$terminalPackage.PackageFullName
        DisplayName = 'Windows Terminal'
        PublisherDisplayName = 'Microsoft Corporation'
        VerbId = 'OpenTerminalHere'
        Clsid = $terminalClsid
        ItemType = 'DirectoryAndBackground'
        Fallback = $true
    })
}
@($items) | ConvertTo-Json -Compress -Depth 4
";

            PowerShellResult result = RunPowerShellCommand(script, 30000);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                LogDebug($"GetPackagedExplorerContextMenuEntries: falha ao enumerar pacotes. exit={result.ExitCode}; stderr='{result.StandardError.Trim()}'. Usando fallback local para o Windows Terminal, se disponível.");
                ExplorerContextMenuEntry? fallback = CreateWindowsTerminalFallbackEntry();
                return fallback == null
                    ? Array.Empty<ExplorerContextMenuEntry>()
                    : new[] { fallback };
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(result.StandardOutput.Trim());
                IEnumerable<JsonElement> records = document.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => document.RootElement.EnumerateArray().ToArray(),
                    JsonValueKind.Object => new[] { document.RootElement },
                    _ => Array.Empty<JsonElement>()
                };

                var entries = new List<ExplorerContextMenuEntry>();
                foreach (JsonElement record in records)
                {
                    string packageName = GetJsonText(record, "PackageName");
                    if (packageName.Equals(TerminalContextMenuPackageName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string clsid = NormalizeClsid(GetJsonText(record, "Clsid"));
                    if (string.IsNullOrWhiteSpace(clsid))
                    {
                        continue;
                    }

                    string verbId = GetJsonText(record, "VerbId");
                    string rawDisplayName = GetJsonText(record, "DisplayName");
                    string publisher = GetJsonText(record, "PublisherDisplayName");
                    string applicationName = FriendlyPackageName(packageName, rawDisplayName);
                    bool isWindowsTerminal = packageName.StartsWith(
                        WindowsTerminalPackagePrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                        clsid.Equals(WindowsTerminalContextMenuClsid, StringComparison.OrdinalIgnoreCase);
                    string displayName = isWindowsTerminal
                        ? "Abrir no Terminal"
                        : string.IsNullOrWhiteSpace(verbId)
                            ? applicationName
                            : NormalizeDisplayName(verbId, applicationName);
                    string itemType = GetJsonText(record, "ItemType");
                    string packageFullName = GetJsonText(record, "PackageFullName");
                    string id = CreateStableExternalEntryId("package-clsid|" + clsid);
                    bool managed = IsExternalEntryManaged(id);
                    bool blocked = IsClsidBlocked(clsid);
                    string target = isWindowsTerminal
                        ? "Pastas e fundo de pasta"
                        : string.IsNullOrWhiteSpace(itemType)
                            ? "Explorer"
                            : HumanizeItemType(itemType);
                    string publisherText = string.IsNullOrWhiteSpace(publisher) || publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : $" · {publisher}";

                    entries.Add(new ExplorerContextMenuEntry(
                        id,
                        displayName,
                        applicationName,
                        $"Comando moderno do pacote {packageName}{publisherText}. {packageFullName}",
                        "Menu moderno (MSIX)",
                        target,
                        isEnabled: !blocked,
                        isManagedByTutzApp: managed,
                        canToggle: !blocked || managed,
                        requiresElevation: false,
                        controlMode: ExternalMenuControlBlockedClsid,
                        clsid: clsid));
                }

                if (!entries.Any(entry =>
                    string.Equals(entry.Clsid, WindowsTerminalContextMenuClsid, StringComparison.OrdinalIgnoreCase)))
                {
                    ExplorerContextMenuEntry? fallback = CreateWindowsTerminalFallbackEntry();
                    if (fallback != null)
                    {
                        entries.Add(fallback);
                    }
                }

                return entries;
            }
            catch (Exception ex)
            {
                LogDebug($"GetPackagedExplorerContextMenuEntries: JSON inválido: {ex.Message}. Saída='{result.StandardOutput.Trim()}'. Usando fallback local para o Windows Terminal, se disponível.");
                ExplorerContextMenuEntry? fallback = CreateWindowsTerminalFallbackEntry();
                return fallback == null
                    ? Array.Empty<ExplorerContextMenuEntry>()
                    : new[] { fallback };
            }
        }

        private ExplorerContextMenuEntry? CreateWindowsTerminalFallbackEntry()
        {
            if (!IsWindowsTerminalAvailable())
            {
                return null;
            }

            string clsid = NormalizeClsid(WindowsTerminalContextMenuClsid);
            string id = CreateStableExternalEntryId("package-clsid|" + clsid);
            bool managed = IsExternalEntryManaged(id);
            bool blocked = IsClsidBlocked(clsid);

            return new ExplorerContextMenuEntry(
                id,
                "Abrir no Terminal",
                "Windows Terminal",
                "Comando moderno do Windows Terminal instalado pela Microsoft Store.",
                "Menu moderno (MSIX)",
                "Pastas e fundo de pasta",
                isEnabled: !blocked,
                isManagedByTutzApp: managed,
                canToggle: !blocked || managed,
                requiresElevation: false,
                controlMode: ExternalMenuControlBlockedClsid,
                clsid: clsid);
        }

        private static bool IsWindowsTerminalAvailable()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            try
            {
                string aliasPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");
                if (File.Exists(aliasPath))
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                string packagesRoot = Path.Combine(localAppData, "Packages");
                if (Directory.Exists(packagesRoot) &&
                    Directory.EnumerateDirectories(
                            packagesRoot,
                            WindowsTerminalPackagePrefix + "*",
                            SearchOption.TopDirectoryOnly)
                        .Any())
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                using RegistryKey? appPath = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\App Paths\wt.exe",
                    writable: false);
                if (!string.IsNullOrWhiteSpace(ReadRegistryText(appPath, null)))
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                string classIndexPath = @"PackagedCom\ClassIndex\" + WindowsTerminalContextMenuClsid;
                using RegistryKey? packagedClass = Registry.ClassesRoot.OpenSubKey(classIndexPath, writable: false);
                if (packagedClass != null)
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                string? path = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    foreach (string segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string candidate = Path.Combine(segment.Trim().Trim('"'), "wt.exe");
                        if (File.Exists(candidate))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private bool SetBlockedClsidEntryEnabled(ExplorerContextMenuEntry entry, bool enabled)
        {
            string clsid = NormalizeClsid(entry.Clsid);
            if (string.IsNullOrWhiteSpace(clsid))
            {
                return false;
            }

            bool managed = IsExternalEntryManaged(entry.Id);
            if (enabled)
            {
                if (!managed)
                {
                    return false;
                }

                using RegistryKey? blockedKey = Registry.CurrentUser.CreateSubKey(
                    ExternalMenuBlockedRegistryPath,
                    writable: true);
                if (blockedKey == null)
                {
                    return false;
                }

                blockedKey.DeleteValue(clsid, throwOnMissingValue: false);
                if (IsClsidBlocked(clsid))
                {
                    return false;
                }

                DeleteManagedExternalEntry(entry.Id);
                return true;
            }

            if (IsClsidBlocked(clsid))
            {
                return managed;
            }

            if (!SaveManagedExternalEntry(entry))
            {
                return false;
            }

            try
            {
                using RegistryKey? blockedKey = Registry.CurrentUser.CreateSubKey(
                    ExternalMenuBlockedRegistryPath,
                    writable: true);
                if (blockedKey == null)
                {
                    DeleteManagedExternalEntry(entry.Id);
                    return false;
                }

                blockedKey.SetValue(clsid, entry.ApplicationName, RegistryValueKind.String);
                if (!IsClsidBlocked(clsid))
                {
                    DeleteManagedExternalEntry(entry.Id);
                    return false;
                }

                return true;
            }
            catch
            {
                DeleteManagedExternalEntry(entry.Id);
                throw;
            }
        }

        private bool SetClassicVerbEntryEnabled(ExplorerContextMenuEntry entry, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(entry.RegistryHive) || string.IsNullOrWhiteSpace(entry.RegistryPath))
            {
                return false;
            }

            bool managed = IsExternalEntryManaged(entry.Id);
            if (enabled && !managed)
            {
                return false;
            }

            if (!enabled && !SaveManagedExternalEntry(entry))
            {
                return false;
            }

            RegistryView registryView = ParseRegistryView(entry.RegistryView);
            bool success = entry.RegistryHive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
                ? SetMachineClassicVerbLegacyDisable(entry.RegistryPath, registryView, disable: !enabled)
                : SetCurrentUserClassicVerbLegacyDisable(entry.RegistryPath, registryView, disable: !enabled);

            if (!success)
            {
                if (!enabled)
                {
                    DeleteManagedExternalEntry(entry.Id);
                }

                return false;
            }

            if (enabled)
            {
                DeleteManagedExternalEntry(entry.Id);
            }

            return true;
        }

        private bool SetCurrentUserClassicVerbLegacyDisable(
            string registryPath,
            RegistryView registryView,
            bool disable)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, registryView);
                using RegistryKey? key = baseKey.OpenSubKey(registryPath, writable: true);
                if (key == null)
                {
                    return false;
                }

                if (disable)
                {
                    if (HasRegistryValue(key, "ProgrammaticAccessOnly"))
                    {
                        return false;
                    }

                    if (!HasRegistryValue(key, "LegacyDisable"))
                    {
                        key.SetValue("LegacyDisable", string.Empty, RegistryValueKind.String);
                    }
                }
                else
                {
                    key.DeleteValue("LegacyDisable", throwOnMissingValue: false);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"SetCurrentUserClassicVerbLegacyDisable ({GetRegistryViewLabel(registryView)}): {ex.Message}");
                return false;
            }
        }

        private bool SetMachineClassicVerbLegacyDisable(
            string registryPath,
            RegistryView registryView,
            bool disable)
        {
            string pathLiteral = EscapePowerShellSingleQuotedString(registryPath);
            string viewName = registryView == RegistryView.Registry32 ? "Registry32" : "Registry64";
            string operation = disable
                ? "if (($key.GetValueNames() -contains 'LegacyDisable') -or ($key.GetValueNames() -contains 'ProgrammaticAccessOnly')) { throw 'A entrada já está desativada.' }; " +
                  "$key.SetValue('LegacyDisable', '', [Microsoft.Win32.RegistryValueKind]::String)"
                : "$key.DeleteValue('LegacyDisable', $false)";

            string script =
                "$baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(" +
                "[Microsoft.Win32.RegistryHive]::LocalMachine, " +
                "[Microsoft.Win32.RegistryView]::" + viewName + "); " +
                "$key = $null; " +
                "try { " +
                "  $key = $baseKey.OpenSubKey('" + pathLiteral + "', $true); " +
                "  if ($null -eq $key) { throw 'A chave do menu não existe ou não permite escrita.' }; " +
                operation + "; " +
                "} finally { if ($null -ne $key) { $key.Dispose() }; $baseKey.Dispose() }";

            PowerShellResult result = RunElevatedPowerShellCommand(script, 30000);
            if (!result.Success)
            {
                LogDebug($"SetMachineClassicVerbLegacyDisable ({GetRegistryViewLabel(registryView)}): exit={result.ExitCode}; stderr='{result.StandardError.Trim()}'.");
            }

            return result.Success;
        }

        private bool SetMachineClassicVerbEntriesLegacyDisable(
            IReadOnlyList<ExplorerContextMenuEntry> entries,
            bool disable)
        {
            if (entries.Count == 0)
            {
                return true;
            }

            var operations = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.RegistryPath))
                .Select(entry => new
                {
                    Path = entry.RegistryPath!,
                    View = ParseRegistryView(entry.RegistryView) == RegistryView.Registry32
                        ? "Registry32"
                        : "Registry64"
                })
                .Distinct()
                .ToArray();
            if (operations.Length == 0)
            {
                return false;
            }

            string operationsJson = EscapePowerShellSingleQuotedString(
                JsonSerializer.Serialize(operations));
            string disableLiteral = disable ? "$true" : "$false";
            string script =
                "$parsedOperations = ConvertFrom-Json -InputObject '" + operationsJson + "'; " +
                "$operations = @($parsedOperations | ForEach-Object { $_ }); " +
                "if ($operations.Count -ne " + operations.Length.ToString() + ") { throw ('Quantidade inválida de alterações de menu: ' + $operations.Count) }; " +
                "$disable = " + disableLiteral + "; " +
                "$changed = New-Object System.Collections.Generic.List[object]; " +
                "try { " +
                "  foreach ($op in $operations) { " +
                "    $path = [string]$op.Path; $viewName = [string]$op.View; " +
                "    if ([string]::IsNullOrWhiteSpace($path)) { throw 'Operação de menu sem caminho de Registro.' }; " +
                "    if ($viewName -ne 'Registry64' -and $viewName -ne 'Registry32') { throw ('Visão de Registro inválida: ' + $viewName) }; " +
                "    $view = [Microsoft.Win32.RegistryView][System.Enum]::Parse([Microsoft.Win32.RegistryView], $viewName); " +
                "    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view); " +
                "    $key = $null; " +
                "    try { " +
                "      $key = $baseKey.OpenSubKey($path, $true); " +
                "      if ($null -eq $key) { throw ('A chave não existe ou não permite escrita: ' + $path) }; " +
                "      $names = @($key.GetValueNames()); " +
                "      if ($disable) { " +
                "        if ($names -contains 'ProgrammaticAccessOnly') { throw ('Entrada protegida por ProgrammaticAccessOnly: ' + $op.Path) }; " +
                "        if ($names -notcontains 'LegacyDisable') { " +
                "          $key.SetValue('LegacyDisable', '', [Microsoft.Win32.RegistryValueKind]::String); " +
                "          $changed.Add($op) | Out-Null " +
                "        } " +
                "      } elseif ($names -contains 'LegacyDisable') { " +
                "        $key.DeleteValue('LegacyDisable', $false); " +
                "        $changed.Add($op) | Out-Null " +
                "      } " +
                "    } finally { if ($null -ne $key) { $key.Dispose() }; $baseKey.Dispose() } " +
                "  } " +
                "} catch { " +
                "  for ($index = $changed.Count - 1; $index -ge 0; $index--) { " +
                "    $op = $changed[$index]; " +
                "    try { " +
                "      $view = [Microsoft.Win32.RegistryView][System.Enum]::Parse([Microsoft.Win32.RegistryView], [string]$op.View); " +
                "      $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view); " +
                "      $key = $null; " +
                "      try { " +
                "        $key = $baseKey.OpenSubKey([string]$op.Path, $true); " +
                "        if ($null -ne $key) { " +
                "          if ($disable) { $key.DeleteValue('LegacyDisable', $false) } " +
                "          else { $key.SetValue('LegacyDisable', '', [Microsoft.Win32.RegistryValueKind]::String) } " +
                "        } " +
                "      } finally { if ($null -ne $key) { $key.Dispose() }; $baseKey.Dispose() } " +
                "    } catch { } " +
                "  }; " +
                "  throw " +
                "}";

            PowerShellResult result = RunElevatedPowerShellCommand(script, 60000);
            if (!result.Success)
            {
                LogDebug($"SetMachineClassicVerbEntriesLegacyDisable: entries={operations.Length}, disable={disable}, exit={result.ExitCode}; stderr='{result.StandardError.Trim()}'.");
            }

            return result.Success;
        }

        private bool SaveManagedExternalEntry(ExplorerContextMenuEntry entry)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
                    ExternalMenuManagedRegistryPath + "\\" + entry.Id,
                    writable: true);
                if (key == null)
                {
                    return false;
                }

                key.SetValue("DisplayName", entry.DisplayName, RegistryValueKind.String);
                key.SetValue("ApplicationName", entry.ApplicationName, RegistryValueKind.String);
                key.SetValue("ControlMode", entry.ControlMode, RegistryValueKind.String);
                key.SetValue("RegistryHive", entry.RegistryHive ?? string.Empty, RegistryValueKind.String);
                key.SetValue("RegistryPath", entry.RegistryPath ?? string.Empty, RegistryValueKind.String);
                key.SetValue("RegistryView", entry.RegistryView ?? string.Empty, RegistryValueKind.String);
                key.SetValue("Clsid", entry.Clsid ?? string.Empty, RegistryValueKind.String);
                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"SaveManagedExternalEntry: falha para '{entry.Id}': {ex.Message}");
                return false;
            }
        }

        private static void DeleteManagedExternalEntry(string id)
        {
            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(ExternalMenuManagedRegistryPath, writable: true);
                root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
            }
            catch
            {
                // The menu entry was already restored; stale bookkeeping is non-critical.
            }
        }

        private static bool IsExternalEntryManaged(string id)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                ExternalMenuManagedRegistryPath + "\\" + id,
                writable: false);
            return key != null;
        }

        private static bool IsClsidBlocked(string clsid)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ExternalMenuBlockedRegistryPath, writable: false);
            return key?.GetValue(clsid) != null;
        }

        private static bool HasRegistryValue(RegistryKey? key, string valueName)
        {
            return key?.GetValueNames().Any(name => name.Equals(valueName, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static bool HasRegistrySubKey(RegistryKey key, string subKeyName)
        {
            try
            {
                using RegistryKey? subKey = key.OpenSubKey(subKeyName, writable: false);
                return subKey != null;
            }
            catch
            {
                return false;
            }
        }

        private static string? ReadRegistryText(RegistryKey? key, string? valueName)
        {
            object? value = key?.GetValue(valueName ?? string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value?.ToString()?.Trim();
        }

        private static bool IsTutzAppRegistration(string verbName, RegistryKey verbKey)
        {
            if (verbName.Contains("TutzApp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string values = string.Join(" ", verbKey.GetValueNames()
                .Select(name => verbKey.GetValue(name)?.ToString() ?? string.Empty));
            return values.Contains("TutzApp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSkipProtectedClassicVerb(string verbName, string command)
        {
            if (!ProtectedWindowsVerbNames.Contains(verbName))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(command) || IsPathUnderWindows(command);
        }

        private static bool ShouldSkipSystemShellHandler(
            string displayName,
            string applicationName,
            string serverPath,
            string handlerName)
        {
            if (!IsPathUnderWindows(serverPath))
            {
                return false;
            }

            string combined = displayName + " " + applicationName + " " + handlerName;
            string[] userFacingExceptions =
            {
                "Terminal",
                "PowerToys",
                "OneDrive",
                "TeraCopy",
                "7-Zip",
                "WinRAR",
                "Git",
                "Dropbox"
            };

            return !userFacingExceptions.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsPathUnderWindows(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            string expanded = Environment.ExpandEnvironmentVariables(value).Trim(' ', '"');
            return expanded.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase) ||
                expanded.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase);
        }

        private static void ResolveComClassMetadata(string clsid, out string displayName, out string serverPath)
        {
            displayName = string.Empty;
            serverPath = string.Empty;

            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Default };

            foreach (RegistryView registryView in views)
            {
                try
                {
                    using RegistryKey classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, registryView);
                    using RegistryKey? classKey = classesRoot.OpenSubKey(@"CLSID\" + clsid, writable: false);
                    if (classKey == null)
                    {
                        continue;
                    }

                    displayName = ReadRegistryText(classKey, null) ?? displayName;
                    using RegistryKey? inproc = classKey.OpenSubKey("InprocServer32", writable: false);
                    using RegistryKey? local = classKey.OpenSubKey("LocalServer32", writable: false);
                    serverPath = ReadRegistryText(inproc, null) ??
                        ReadRegistryText(local, null) ??
                        serverPath;

                    if (!string.IsNullOrWhiteSpace(displayName) || !string.IsNullOrWhiteSpace(serverPath))
                    {
                        return;
                    }
                }
                catch
                {
                    // Missing COM metadata is common for packaged classes.
                }
            }
        }

        private static string ResolveApplicationName(string displayName, string commandOrPath, string fallback)
        {
            string executable = ExtractExecutablePath(commandOrPath);
            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            {
                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(executable);
                    string? product = info.ProductName;
                    if (!string.IsNullOrWhiteSpace(product))
                    {
                        return product.Trim();
                    }

                    string? description = info.FileDescription;
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        return description.Trim();
                    }
                }
                catch
                {
                    // Registry display names remain a useful fallback.
                }
            }

            string normalized = NormalizeDisplayName(displayName, fallback);
            return string.IsNullOrWhiteSpace(normalized) ? HumanizeIdentifier(fallback) : normalized;
        }

        private static string ExtractExecutablePath(string commandOrPath)
        {
            if (string.IsNullOrWhiteSpace(commandOrPath))
            {
                return string.Empty;
            }

            string expanded = Environment.ExpandEnvironmentVariables(commandOrPath.Trim());
            if (expanded.StartsWith('"'))
            {
                int closingQuote = expanded.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    return expanded[1..closingQuote];
                }
            }

            Match match = Regex.Match(expanded, @"^(.+?\.(?:exe|dll))(?=\s|,|$)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim(' ', '"') : expanded.Trim(' ', '"');
        }

        private static string FriendlyPackageName(string packageName, string displayName)
        {
            if (packageName.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows Terminal";
            }

            if (packageName.Contains("TeraCopy", StringComparison.OrdinalIgnoreCase))
            {
                return "TeraCopy";
            }

            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                return displayName.Trim();
            }

            string tail = packageName.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? packageName;
            return HumanizeIdentifier(tail);
        }

        private static string NormalizeDisplayName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('@') || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                return HumanizeIdentifier(fallback);
            }

            string normalized = value.Replace("&", string.Empty, StringComparison.Ordinal).Trim();
            if (Regex.IsMatch(normalized, "(?<=[a-z0-9])(?=[A-Z])"))
            {
                normalized = Regex.Replace(normalized, "(?<=[a-z0-9])(?=[A-Z])", " ");
            }

            return string.IsNullOrWhiteSpace(normalized) ? HumanizeIdentifier(fallback) : normalized;
        }

        private static string HumanizeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Entrada do menu";
            }

            string last = value.Split(new[] { '.', '\\', '/', '_' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
            string spaced = Regex.Replace(last, "(?<=[a-z0-9])(?=[A-Z])", " ");
            return spaced.Replace('-', ' ').Trim();
        }

        private static string HumanizeClassRoot(string classRoot)
        {
            string known = classRoot switch
            {
                "*" => "Arquivos",
                "AllFilesystemObjects" => "Arquivos e pastas",
                "Directory" => "Pastas",
                @"Directory\Background" => "Fundo de pasta",
                "Drive" => "Unidades",
                "Folder" => "Pastas virtuais",
                "LibraryFolder" => "Bibliotecas",
                @"LibraryFolder\Background" => "Fundo de biblioteca",
                "DesktopBackground" => "Área de trabalho",
                _ => string.Empty
            };

            if (!string.IsNullOrWhiteSpace(known))
            {
                return known;
            }

            const string systemAssociationPrefix = @"SystemFileAssociations\";
            if (classRoot.StartsWith(systemAssociationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "Associação de arquivo: " +
                    HumanizeIdentifier(classRoot[systemAssociationPrefix.Length..]);
            }

            const string applicationPrefix = @"Applications\";
            if (classRoot.StartsWith(applicationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "Aplicativo: " +
                    HumanizeIdentifier(classRoot[applicationPrefix.Length..]);
            }

            if (classRoot.StartsWith(".", StringComparison.Ordinal))
            {
                return "Arquivos " + classRoot;
            }

            return HumanizeIdentifier(classRoot);
        }

        private static string HumanizeItemType(string itemType)
        {
            return itemType switch
            {
                "*" => "Arquivos",
                "Directory" => "Pastas",
                "Directory\\Background" => "Fundo de pasta",
                "Drive" => "Unidades",
                "AllFilesystemObjects" => "Arquivos e pastas",
                "DirectoryAndBackground" => "Pastas e fundo de pasta",
                _ => itemType
            };
        }

        private static string NormalizeClsid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim().Trim('"');
            if (!Guid.TryParse(trimmed, out Guid guid))
            {
                return string.Empty;
            }

            return guid.ToString("B").ToUpperInvariant();
        }

        private static bool LooksLikeClsid(string value)
        {
            return Guid.TryParse(value.Trim(), out _);
        }

        private static string GetJsonText(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                ? value.ToString().Trim()
                : string.Empty;
        }

        private static string CreateStableExternalEntryId(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
            return "external-" + Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
        }

        private static string GetExternalControlTargetKey(ExplorerContextMenuEntry entry)
        {
            if (entry.ControlMode.Equals(ExternalMenuControlBlockedClsid, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.Clsid))
            {
                return "clsid|" + entry.Clsid;
            }

            if (entry.ControlMode.Equals(ExternalMenuControlClassicVerb, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.RegistryHive) &&
                !string.IsNullOrWhiteSpace(entry.RegistryPath))
            {
                return $"verb|{entry.RegistryHive}|{entry.RegistryView}|{entry.RegistryPath}";
            }

            return entry.Id;
        }

        private static string GetExternalApplicationGroupKey(ExplorerContextMenuEntry entry)
        {
            string value = string.IsNullOrWhiteSpace(entry.ApplicationName)
                ? entry.DisplayName
                : entry.ApplicationName;
            value = Regex.Replace(value.Trim(), @"\s+", " ");
            value = value.Replace("®", string.Empty, StringComparison.Ordinal)
                .Replace("™", string.Empty, StringComparison.Ordinal)
                .Trim();

            // The same product is often registered under slightly different names:
            // one static verb for Explorer and one COM handler for third-party hosts.
            // Collapse common shell suffixes so one UI action reaches every registration.
            value = Regex.Replace(
                value,
                @"(?i)\s+(?:shell|explorer|context(?:\s+menu)?|shortcut\s+menu)\s+(?:extension|handler)$",
                string.Empty).Trim();
            value = Regex.Replace(
                value,
                @"(?i)\s+(?:shell\s+extension|context\s+menu|contextmenu|menu\s+handler)$",
                string.Empty).Trim();

            string[] canonicalProducts =
            {
                "TeraCopy",
                "Windows Terminal",
                "7-Zip",
                "WinRAR",
                "PowerToys",
                "OneDrive",
                "Dropbox",
                "Git"
            };
            string? canonical = canonicalProducts.FirstOrDefault(product =>
                Regex.IsMatch(
                    value,
                    $@"(?i)(?<![\p{{L}}\p{{N}}]){Regex.Escape(product)}(?![\p{{L}}\p{{N}}])"));
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                value = canonical;
            }

            return value.Length == 0
                ? entry.Id
                : value.ToUpperInvariant();
        }

        private static string AbbreviateCommand(string command)
        {
            string expanded = Environment.ExpandEnvironmentVariables(command).Trim();
            return expanded.Length <= 120 ? expanded : expanded[..117] + "...";
        }

        private static string AbbreviatePath(string path)
        {
            string expanded = Environment.ExpandEnvironmentVariables(path).Trim(' ', '"');
            return expanded.Length <= 110 ? expanded : "..." + expanded[^107..];
        }

        private sealed class HandlerScanRecord
        {
            public HandlerScanRecord(
                string clsid,
                string displayName,
                string handlerName,
                string hiveName,
                string commandHint)
            {
                Clsid = clsid;
                DisplayName = displayName;
                HandlerName = handlerName;
                HiveName = hiveName;
                CommandHint = commandHint;
            }

            public string Clsid { get; }
            public string DisplayName { get; set; }
            public string HandlerName { get; }
            public string HiveName { get; }
            public string CommandHint { get; }
            public HashSet<string> Targets { get; } = new(StringComparer.CurrentCultureIgnoreCase);
        }
    }
}
