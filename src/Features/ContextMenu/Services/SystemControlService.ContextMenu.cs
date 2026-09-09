using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TutzApp.Common;
using TutzApp.Models;

namespace TutzApp.Services
{
    public partial class SystemControlService
    {
        private const string TerminalContextMenuPackageName = "ArthurCuesta.TutzApp.ContextMenu";
        private const string TerminalContextMenuInstallRegistryPath = @"Software\TutzApp\ShellIntegration";
        private const string TerminalContextMenuExternalLocationValue = "ExternalLocation";
        private const string TerminalContextMenuShowNormalValue = "ShowNormal";
        private const string TerminalContextMenuShowElevatedValue = "ShowElevated";
        private const string TerminalContextMenuNormalCommandId = "normal";
        private const string TerminalContextMenuElevatedCommandId = "elevated";
        private const string TerminalContextMenuPackageFileName = "TutzApp.ContextMenu.msix";
        private const string TerminalContextMenuCertificateFileName = "TutzApp.ContextMenu.cer";
        private const string TerminalContextMenuDllFileName = "TutzExplorerCommand.dll";
        private const string TerminalContextMenuComAppId = "F13BF4B5-9064-4971-B73E-1D5EC8F2EAA5";
        private const string LegacyTerminalContextMenuComAppId = "8E0BA745-6D15-4C5B-BEF8-0F73A4C82677";
        private const string TerminalContextMenuCertificateSubject = "CN=TutzApp Local Development";
        private const string TerminalContextMenuOperationDirectoryName = "ShellIntegrationOperations";
        private static int TerminalContextMenuPackageOperationScheduled;

        // The modern Windows 11 menu remains a single IExplorerCommand parent with
        // Normal/Elevated children. The classic menu deliberately uses two independent
        // static verbs: this is the only layout verified to execute correctly in both
        // Explorer's "Show more options" menu and File Pilot.
        private const string ClassicTerminalContextMenuParentKeyName = "TutzApp.Terminal"; // migration cleanup only
        private const string ClassicTerminalNormalKeyName = "TutzApp.OpenTerminal";
        private const string ClassicTerminalElevatedKeyName = "TutzApp.OpenTerminalAdmin";
        // Cleanup-only location used by the immediately previous build. New installs
        // never create or execute a separate launcher script.
        private const string LegacyClassicTerminalLauncherDirectoryName = @"TutzApp\ShellIntegration";
        private const string LegacyClassicTerminalLauncherFileName = "Invoke-ClassicTerminal.ps1";

        // CommandStore is no longer used for new registrations. The path and generated
        // names are retained strictly to remove residues created by previous builds.
        private const string ClassicTerminalCommandStoreRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";
        private const string ClassicShellDefaultVerbStatePrefix = "ClassicShellDefaultVerbManaged."; // migration cleanup only

        private readonly record struct TerminalContextMenuTarget(
            string ClassShellPath,
            string ArgumentToken,
            string CommandStoreSuffix);

        private readonly record struct MachineClassicMenuOperation(
            string ClassShellPath,
            string NormalCommand,
            string ElevatedCommand);

        // Exact registry surface validated by the interactive probe. It affects the
        // background menu of a filesystem directory and cannot become Directory's
        // default double-click verb because no key is created under Directory\shell.
        private static readonly TerminalContextMenuTarget[] TerminalContextMenuTargets =
        {
            new(@"Directory\Background\shell", "%V", "DirectoryBackground")
        };

        // Previous builds wrote TutzApp keys to these locations. They are cleanup-only;
        // new installs never add classic entries there.
        private static readonly TerminalContextMenuTarget[] ClassicTerminalContextMenuCleanupTargets =
        {
            new(@"Directory\Background\shell", "%V", "DirectoryBackground"),
            new(@"Directory\shell", "%1", "Directory"),
            new(@"Drive\shell", "%1", "Drive"),
            new(@"Folder\shell", "%1", "Folder"),
            new(@"LibraryFolder\Background\shell", "%V", "LibraryBackground")
        };

        public bool AreTerminalContextMenuEntriesInstalled()
        {
            try
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    return false;
                }

                string externalLocation = GetTerminalContextMenuExternalLocation();
                if (!TryGetTerminalContextMenuArtifacts(externalLocation, out _, out _, out _))
                {
                    return false;
                }

                using RegistryKey? stateKey = Registry.CurrentUser.OpenSubKey(TerminalContextMenuInstallRegistryPath, false);
                string? registeredLocation = stateKey?.GetValue(TerminalContextMenuExternalLocationValue)?.ToString();
                if (string.IsNullOrWhiteSpace(registeredLocation) ||
                    !PathsEqual(registeredLocation, externalLocation))
                {
                    return false;
                }

                return IsTerminalContextMenuPackageRegistered() &&
                    AreClassicTerminalContextMenuEntriesInstalled();
            }
            catch (Exception ex)
            {
                LogDebug($"AreTerminalContextMenuEntriesInstalled: falha ao consultar pacote moderno: {ex.Message}");
                return false;
            }
        }

        public void InstallTerminalContextMenuEntries()
        {
            try
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    SetStatusMessage("O menu de contexto moderno requer Windows 11.");
                    return;
                }

                string externalLocation = GetTerminalContextMenuExternalLocation();
                if (!TryGetTerminalContextMenuArtifacts(
                    externalLocation,
                    out string packagePath,
                    out string certificatePath,
                    out string extensionDllPath))
                {
                    string expectedDirectory = Path.Combine(externalLocation, "ShellIntegration");
                    LogDebug($"InstallTerminalContextMenuEntries: artefatos ausentes em '{expectedDirectory}'. Execute build.bat para gerar DLL, MSIX e certificado.");
                    SetStatusMessage("Integração moderna ausente. Execute build.bat novamente.");
                    return;
                }

                bool wasAlreadyInstalled = AreTerminalContextMenuEntriesInstalled();
                bool showNormal;
                bool showElevated;
                using (RegistryKey? stateKey = Registry.CurrentUser.CreateSubKey(TerminalContextMenuInstallRegistryPath, true))
                {
                    if (stateKey == null)
                    {
                        SetStatusMessage("Falha ao salvar a configuração do menu de contexto.");
                        return;
                    }

                    stateKey.SetValue(TerminalContextMenuExternalLocationValue, externalLocation, RegistryValueKind.String);
                    stateKey.SetValue("PackagePath", packagePath, RegistryValueKind.String);
                    stateKey.SetValue("ExtensionDllPath", extensionDllPath, RegistryValueKind.String);
                    showNormal = ReadTerminalContextMenuCommandState(stateKey, TerminalContextMenuShowNormalValue);
                    showElevated = ReadTerminalContextMenuCommandState(stateKey, TerminalContextMenuShowElevatedValue);
                }

                // Remove only TutzApp-owned HKCU residues from previous builds. This code
                // never reads, writes or deletes the default value of any shell container.
                int perUserRemoved = RemoveClassicTerminalContextMenuEntries();
                if (perUserRemoved > 0)
                {
                    LogDebug($"InstallTerminalContextMenuEntries: {perUserRemoved} registro(s) clássico(s) antigo(s) removido(s) de HKCU.");
                }

                SetStatusMessage("Autorize o UAC para registrar o certificado e os dois comandos clássicos.");
                string executablePath = Path.Combine(externalLocation, "TutzApp.exe");
                if (!EnsureTerminalContextMenuCertificateTrusted(
                    certificatePath,
                    executablePath,
                    showNormal,
                    showElevated,
                    out string certificateTrustError))
                {
                    LogDebug($"InstallTerminalContextMenuEntries: certificado/comandos clássicos não registrados: {certificateTrustError}");
                    SetStatusMessage("Falha ao registrar os comandos clássicos e o certificado do menu moderno.");
                    return;
                }

                NotifyShellAssociationChanged();
                if (!ScheduleTerminalContextMenuPackageOperation(
                    install: true,
                    externalLocation,
                    packagePath))
                {
                    SetStatusMessage("Falha ao preparar a instalação segura do menu moderno.");
                    return;
                }

                LogDebug($"InstallTerminalContextMenuEntries: operação MSIX iniciada em segundo plano; repair={wasAlreadyInstalled}; classicTargets={TerminalContextMenuTargets.Length}; externalLocation='{externalLocation}'.");
                SetStatusMessage(wasAlreadyInstalled
                    ? "Reparando o menu moderno em segundo plano. O TutzApp permanecerá aberto."
                    : "Instalando o menu moderno em segundo plano. O TutzApp permanecerá aberto.");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref TerminalContextMenuPackageOperationScheduled, 0);
                LogDebug($"InstallTerminalContextMenuEntries: exceção protegida: {ex}");
                SetStatusMessage("Falha ao instalar o menu moderno do Terminal.");
            }
        }

        public void UninstallTerminalContextMenuEntries()
        {
            try
            {
                string externalLocation = GetTerminalContextMenuExternalLocation();
                string integrationDirectory = Path.Combine(externalLocation, "ShellIntegration");
                string certificatePath = Path.Combine(integrationDirectory, TerminalContextMenuCertificateFileName);
                bool packageRegistered = IsTerminalContextMenuPackageRegistered();

                if (!RemoveTerminalContextMenuCertificateTrust(certificatePath, out string certificateRemovalError))
                {
                    LogDebug($"UninstallTerminalContextMenuEntries: falha ao remover certificado confiável: {certificateRemovalError}");
                    SetStatusMessage("Falha ao remover o certificado confiável do menu moderno.");
                    return;
                }

                int classicRemoved = RemoveClassicTerminalContextMenuEntries();
                Registry.CurrentUser.DeleteSubKeyTree(TerminalContextMenuInstallRegistryPath, throwOnMissingSubKey: false);
                NotifyShellAssociationChanged();

                if (!packageRegistered)
                {
                    LogDebug($"UninstallTerminalContextMenuEntries: pacote já ausente; classicRemoved={classicRemoved}.");
                    SetStatusMessage("Menus moderno e clássico do Terminal removidos.");
                    return;
                }

                if (!ScheduleTerminalContextMenuPackageOperation(
                    install: false,
                    externalLocation,
                    packagePath: string.Empty))
                {
                    SetStatusMessage("Falha ao preparar a remoção segura do menu moderno.");
                    return;
                }

                LogDebug($"UninstallTerminalContextMenuEntries: remoção MSIX iniciada em segundo plano; classicRemoved={classicRemoved}.");
                SetStatusMessage("Removendo o menu moderno em segundo plano. O TutzApp permanecerá aberto.");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref TerminalContextMenuPackageOperationScheduled, 0);
                LogDebug($"UninstallTerminalContextMenuEntries: exceção protegida: {ex}");
                SetStatusMessage("Falha ao remover o menu moderno do Terminal.");
            }
        }

        public void EnsureTerminalContextMenuEntries()
        {
            InstallTerminalContextMenuEntries();
        }

        public IReadOnlyList<TerminalContextMenuCommand> GetTerminalContextMenuCommands()
        {
            try
            {
                using RegistryKey? stateKey = Registry.CurrentUser.OpenSubKey(
                    TerminalContextMenuInstallRegistryPath,
                    writable: false);

                bool showNormal = ReadTerminalContextMenuCommandState(
                    stateKey,
                    TerminalContextMenuShowNormalValue);
                bool showElevated = ReadTerminalContextMenuCommandState(
                    stateKey,
                    TerminalContextMenuShowElevatedValue);

                return new[]
                {
                    new TerminalContextMenuCommand(
                        TerminalContextMenuNormalCommandId,
                        "Normal",
                        "Abre o Windows Terminal no diretório selecionado sem elevação.",
                        showNormal),
                    new TerminalContextMenuCommand(
                        TerminalContextMenuElevatedCommandId,
                        "Elevado",
                        "Abre o Windows Terminal como administrador no diretório selecionado.",
                        showElevated)
                };
            }
            catch (Exception ex)
            {
                LogDebug($"GetTerminalContextMenuCommands: falha ao ler configuração: {ex.Message}");
                return new[]
                {
                    new TerminalContextMenuCommand(
                        TerminalContextMenuNormalCommandId,
                        "Normal",
                        "Abre o Windows Terminal no diretório selecionado sem elevação.",
                        isEnabled: true),
                    new TerminalContextMenuCommand(
                        TerminalContextMenuElevatedCommandId,
                        "Elevado",
                        "Abre o Windows Terminal como administrador no diretório selecionado.",
                        isEnabled: true)
                };
            }
        }

        public bool SetTerminalContextMenuCommandEnabled(string commandId, bool enabled)
        {
            string? valueName = commandId?.Trim().ToLowerInvariant() switch
            {
                TerminalContextMenuNormalCommandId => TerminalContextMenuShowNormalValue,
                TerminalContextMenuElevatedCommandId => TerminalContextMenuShowElevatedValue,
                _ => null
            };

            if (valueName == null)
            {
                LogDebug($"SetTerminalContextMenuCommandEnabled: identificador desconhecido '{commandId}'.");
                return false;
            }

            try
            {
                using RegistryKey? stateKey = Registry.CurrentUser.CreateSubKey(
                    TerminalContextMenuInstallRegistryPath,
                    writable: true);
                if (stateKey == null)
                {
                    LogDebug("SetTerminalContextMenuCommandEnabled: não foi possível abrir a chave de configuração.");
                    return false;
                }

                bool hadPreviousValue = stateKey.GetValueNames()
                    .Any(name => name.Equals(valueName, StringComparison.OrdinalIgnoreCase));
                object? previousValue = stateKey.GetValue(
                    valueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);

                stateKey.SetValue(valueName, enabled ? 1 : 0, RegistryValueKind.DWord);

                bool shouldSynchronizeClassic = IsTerminalContextMenuPackageRegistered() ||
                    HasAnyClassicTerminalContextMenuEntry();
                if (shouldSynchronizeClassic && !SynchronizeClassicTerminalContextMenuEntries(stateKey))
                {
                    if (hadPreviousValue && previousValue != null)
                    {
                        stateKey.SetValue(valueName, previousValue);
                    }
                    else
                    {
                        stateKey.DeleteValue(valueName, throwOnMissingValue: false);
                    }

                    _ = SynchronizeClassicTerminalContextMenuEntries(stateKey);
                    LogDebug($"SetTerminalContextMenuCommandEnabled: falha ao sincronizar menu clássico; estado revertido para commandId='{commandId}'.");
                    return false;
                }

                NotifyShellAssociationChanged();
                LogDebug($"SetTerminalContextMenuCommandEnabled: commandId='{commandId}', enabled={enabled}; menu moderno e clássico sincronizados.");
                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"SetTerminalContextMenuCommandEnabled: falha: {ex.Message}");
                return false;
            }
        }

        public bool RestoreTerminalContextMenuCommands()
        {
            try
            {
                using RegistryKey? stateKey = Registry.CurrentUser.CreateSubKey(
                    TerminalContextMenuInstallRegistryPath,
                    writable: true);
                if (stateKey == null)
                {
                    LogDebug("RestoreTerminalContextMenuCommands: não foi possível abrir a chave de configuração.");
                    return false;
                }

                bool hadNormalValue = stateKey.GetValueNames()
                    .Any(name => name.Equals(TerminalContextMenuShowNormalValue, StringComparison.OrdinalIgnoreCase));
                bool hadElevatedValue = stateKey.GetValueNames()
                    .Any(name => name.Equals(TerminalContextMenuShowElevatedValue, StringComparison.OrdinalIgnoreCase));
                object? previousNormal = stateKey.GetValue(TerminalContextMenuShowNormalValue);
                object? previousElevated = stateKey.GetValue(TerminalContextMenuShowElevatedValue);

                stateKey.DeleteValue(TerminalContextMenuShowNormalValue, throwOnMissingValue: false);
                stateKey.DeleteValue(TerminalContextMenuShowElevatedValue, throwOnMissingValue: false);

                bool shouldSynchronizeClassic = IsTerminalContextMenuPackageRegistered() ||
                    HasAnyClassicTerminalContextMenuEntry();
                if (shouldSynchronizeClassic && !SynchronizeClassicTerminalContextMenuEntries(stateKey))
                {
                    RestoreRegistryValue(stateKey, TerminalContextMenuShowNormalValue, hadNormalValue, previousNormal);
                    RestoreRegistryValue(stateKey, TerminalContextMenuShowElevatedValue, hadElevatedValue, previousElevated);
                    _ = SynchronizeClassicTerminalContextMenuEntries(stateKey);
                    LogDebug("RestoreTerminalContextMenuCommands: falha ao sincronizar menu clássico; estado anterior restaurado.");
                    return false;
                }

                NotifyShellAssociationChanged();
                LogDebug("RestoreTerminalContextMenuCommands: atalhos Normal e Elevado restaurados nos menus moderno e clássico.");
                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"RestoreTerminalContextMenuCommands: falha: {ex.Message}");
                return false;
            }
        }

        private static void RestoreRegistryValue(
            RegistryKey key,
            string valueName,
            bool hadValue,
            object? value)
        {
            if (hadValue && value != null)
            {
                key.SetValue(valueName, value);
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }

        private static bool ReadTerminalContextMenuCommandState(RegistryKey? stateKey, string valueName)
        {
            object? rawValue = stateKey?.GetValue(valueName);
            if (rawValue is null)
            {
                return true;
            }

            return rawValue switch
            {
                int intValue => intValue != 0,
                long longValue => longValue != 0,
                string text when int.TryParse(text, out int parsed) => parsed != 0,
                _ => true
            };
        }

        private readonly record struct PowerShellResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
        {
            public bool Success => !TimedOut && ExitCode == 0;
        }

        private string GetTerminalContextMenuExternalLocation()
        {
            string? processPath = Environment.ProcessPath;
            string? directory = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = AppContext.BaseDirectory;
            }

            return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool TryGetTerminalContextMenuArtifacts(
            string externalLocation,
            out string packagePath,
            out string certificatePath,
            out string extensionDllPath)
        {
            string integrationDirectory = Path.Combine(externalLocation, "ShellIntegration");
            packagePath = Path.Combine(integrationDirectory, TerminalContextMenuPackageFileName);
            certificatePath = Path.Combine(integrationDirectory, TerminalContextMenuCertificateFileName);
            extensionDllPath = Path.Combine(integrationDirectory, TerminalContextMenuDllFileName);

            return File.Exists(packagePath) && File.Exists(certificatePath) && File.Exists(extensionDllPath);
        }

        private static string GetLegacyClassicTerminalLauncherPath()
        {
            string commonApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonApplicationData))
            {
                commonApplicationData = Path.Combine(
                    Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
                    "ProgramData");
            }

            return Path.Combine(
                commonApplicationData,
                LegacyClassicTerminalLauncherDirectoryName,
                LegacyClassicTerminalLauncherFileName);
        }

        private bool EnsureTerminalContextMenuCertificateTrusted(
            string certificatePath,
            string executablePath,
            bool showNormal,
            bool showElevated,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            if (!TryReadTerminalContextMenuCertificate(certificatePath, out string thumbprint, out errorMessage))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                errorMessage = $"O executável exigido pelo menu clássico não existe: '{executablePath}'.";
                return false;
            }

            bool certificateTrusted = IsCertificateInLocalMachineTrustedPeople(thumbprint);
            bool machineMenuInstalled = AreMachineClassicTerminalContextMenuEntriesInstalled(
                executablePath,
                showNormal,
                showElevated);
            if (certificateTrusted && machineMenuInstalled)
            {
                RemoveCertificateFromCurrentUserTrustedPeople(thumbprint);
                return true;
            }

            MachineClassicMenuOperation[] operations = CreateMachineClassicMenuOperations(executablePath);
            string operationsJson = EscapePowerShellSingleQuotedString(JsonSerializer.Serialize(operations));
            string legacyLauncherPathLiteral = EscapePowerShellSingleQuotedString(
                GetLegacyClassicTerminalLauncherPath());
            string cleanupPathsJson = EscapePowerShellSingleQuotedString(JsonSerializer.Serialize(
                ClassicTerminalContextMenuCleanupTargets.Select(target => target.ClassShellPath).ToArray()));
            string certificateLiteral = EscapePowerShellSingleQuotedString(certificatePath);
            string thumbprintLiteral = EscapePowerShellSingleQuotedString(thumbprint);
            string subjectLiteral = EscapePowerShellSingleQuotedString(TerminalContextMenuCertificateSubject);
            string executableLiteral = EscapePowerShellSingleQuotedString(executablePath + ",0");
            string commandStoreLiteral = EscapePowerShellSingleQuotedString(ClassicTerminalCommandStoreRoot);
            string parentKeyLiteral = EscapePowerShellSingleQuotedString(ClassicTerminalContextMenuParentKeyName);
            string normalKeyLiteral = EscapePowerShellSingleQuotedString(ClassicTerminalNormalKeyName);
            string elevatedKeyLiteral = EscapePowerShellSingleQuotedString(ClassicTerminalElevatedKeyName);
            string legacyCommandNamesJson = EscapePowerShellSingleQuotedString(JsonSerializer.Serialize(
                GetLegacyClassicCommandStoreNames()));
            string showNormalLiteral = showNormal ? "$true" : "$false";
            string showElevatedLiteral = showElevated ? "$true" : "$false";

            string script = $$"""
                $ErrorActionPreference = 'Stop'
                $certificatePath = '{{certificateLiteral}}'
                $expectedThumbprint = '{{thumbprintLiteral}}'
                $expectedSubject = '{{subjectLiteral}}'
                $menuIcon = '{{executableLiteral}}'
                $legacyLauncherPath = '{{legacyLauncherPathLiteral}}'
                $parentName = '{{parentKeyLiteral}}'
                $normalName = '{{normalKeyLiteral}}'
                $elevatedName = '{{elevatedKeyLiteral}}'
                $showNormal = {{showNormalLiteral}}
                $showElevated = {{showElevatedLiteral}}
                $commandStorePath = '{{commandStoreLiteral}}'
                $parsedOperations = ConvertFrom-Json -InputObject '{{operationsJson}}'
                $operations = @($parsedOperations | ForEach-Object { $_ })
                $parsedCleanupPaths = ConvertFrom-Json -InputObject '{{cleanupPathsJson}}'
                $cleanupPaths = @($parsedCleanupPaths | ForEach-Object { [string]$_ })
                $parsedLegacyNames = ConvertFrom-Json -InputObject '{{legacyCommandNamesJson}}'
                $legacyNames = @($parsedLegacyNames | ForEach-Object { [string]$_ })

                if ($operations.Count -ne {{operations.Length}}) {
                    throw ('Quantidade inválida de alvos do menu clássico: ' + $operations.Count)
                }

                $certificate = New-Object -TypeName System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (,$certificatePath)
                if ($certificate.Thumbprint -ne $expectedThumbprint) {
                    throw 'O thumbprint do certificado mudou antes da elevação.'
                }
                if ($certificate.Subject -ne $expectedSubject) {
                    throw ('Publisher inesperado: ' + $certificate.Subject)
                }

                $storePath = 'Cert:\LocalMachine\TrustedPeople'
                $trustedPath = $storePath + '\' + $expectedThumbprint
                if (-not (Test-Path -LiteralPath $trustedPath)) {
                    Import-Certificate -FilePath $certificatePath -CertStoreLocation $storePath | Out-Null
                }
                if (-not (Test-Path -LiteralPath $trustedPath)) {
                    throw 'O certificado não apareceu em LocalMachine\TrustedPeople.'
                }
                $stale = Get-ChildItem -LiteralPath $storePath | Where-Object {
                    $_.Subject -eq $expectedSubject -and $_.Thumbprint -ne $expectedThumbprint
                }
                foreach ($item in $stale) {
                    Remove-Item -LiteralPath ($storePath + '\' + $item.Thumbprint) -Force
                }

                # Remove the obsolete standalone PowerShell launcher from the previous
                # build. The two classic verbs now invoke TutzApp.exe directly.
                if (Test-Path -LiteralPath $legacyLauncherPath -PathType Leaf) {
                    Remove-Item -LiteralPath $legacyLauncherPath -Force
                }
                $legacyLauncherDirectory = Split-Path -Parent $legacyLauncherPath
                $legacyLauncherLog = Join-Path $legacyLauncherDirectory 'classic-terminal-launch.log'
                if (Test-Path -LiteralPath $legacyLauncherLog -PathType Leaf) {
                    Remove-Item -LiteralPath $legacyLauncherLog -Force
                }
                if (Test-Path -LiteralPath $legacyLauncherDirectory -PathType Container) {
                    $remainingLegacyLauncherFiles = @(
                        Get-ChildItem -LiteralPath $legacyLauncherDirectory -Force -ErrorAction SilentlyContinue)
                    if ($remainingLegacyLauncherFiles.Count -eq 0) {
                        Remove-Item -LiteralPath $legacyLauncherDirectory -Force
                    }
                }

                # Software\Classes is shared between registry views. Use the native
                # machine view, matching the verified Python probe.
                $classView = if ([Environment]::Is64BitOperatingSystem) {
                    [Microsoft.Win32.RegistryView]::Registry64
                } else {
                    [Microsoft.Win32.RegistryView]::Default
                }
                $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                    [Microsoft.Win32.RegistryHive]::LocalMachine,
                    $classView)
                $classesRoot = $null
                try {
                    $classesRoot = $baseKey.CreateSubKey('Software\Classes', $true)
                    if ($null -eq $classesRoot) {
                        throw 'Não foi possível abrir Software\Classes.'
                    }

                    # Remove only TutzApp-owned keys left by older cascade builds.
                    # Do not query or modify the default value of any shell container.
                    foreach ($path in $cleanupPaths) {
                        if ([string]::IsNullOrWhiteSpace($path) -or
                            -not $path.EndsWith('\shell', [System.StringComparison]::OrdinalIgnoreCase) -or
                            $path.Contains('..')) {
                            throw ('Caminho de limpeza inválido: ' + $path)
                        }

                        $shellKey = $null
                        try {
                            $shellKey = $classesRoot.OpenSubKey($path, $true)
                            if ($null -ne $shellKey) {
                                $shellKey.DeleteSubKeyTree($parentName, $false)
                                $shellKey.DeleteSubKeyTree($normalName, $false)
                                $shellKey.DeleteSubKeyTree($elevatedName, $false)
                            }
                        } finally {
                            if ($null -ne $shellKey) { $shellKey.Dispose() }
                        }
                    }

                    foreach ($op in $operations) {
                        $path = [string]$op.ClassShellPath
                        if ([string]::IsNullOrWhiteSpace($path) -or
                            -not $path.EndsWith('\shell', [System.StringComparison]::OrdinalIgnoreCase) -or
                            $path.Contains('..')) {
                            throw ('Caminho de classe inválido: ' + $path)
                        }

                        $shellKey = $null
                        try {
                            $shellKey = $classesRoot.CreateSubKey($path, $true)
                            if ($null -eq $shellKey) {
                                throw ('Falha ao abrir classe ' + $path)
                            }

                            if ($showNormal) {
                                $verb = $null
                                $command = $null
                                try {
                                    $verb = $shellKey.CreateSubKey($normalName, $true)
                                    if ($null -eq $verb) {
                                        throw ('Falha ao criar comando normal em ' + $path)
                                    }
                                    $verb.DeleteValue('', $false)
                                    $verb.SetValue('MUIVerb', 'Abrir no Terminal', [Microsoft.Win32.RegistryValueKind]::String)
                                    $verb.SetValue('Icon', $menuIcon, [Microsoft.Win32.RegistryValueKind]::String)
                                    $verb.DeleteValue('Position', $false)
                                    $verb.DeleteValue('SubCommands', $false)
                                    $verb.DeleteValue('DelegateExecute', $false)
                                    $verb.DeleteValue('LegacyDisable', $false)
                                    $verb.DeleteValue('HasLUAShield', $false)
                                    $verb.DeleteSubKeyTree('shell', $false)
                                    $verb.DeleteSubKeyTree('ExtendedSubCommandsKey', $false)
                                    $verb.DeleteSubKeyTree('command', $false)
                                    $command = $verb.CreateSubKey('command', $true)
                                    if ($null -eq $command) {
                                        throw ('Falha ao criar command normal em ' + $path)
                                    }
                                    $command.SetValue('', [string]$op.NormalCommand, [Microsoft.Win32.RegistryValueKind]::String)
                                } finally {
                                    if ($null -ne $command) { $command.Dispose() }
                                    if ($null -ne $verb) { $verb.Dispose() }
                                }
                            }

                            if ($showElevated) {
                                $verb = $null
                                $command = $null
                                try {
                                    $verb = $shellKey.CreateSubKey($elevatedName, $true)
                                    if ($null -eq $verb) {
                                        throw ('Falha ao criar comando elevado em ' + $path)
                                    }
                                    $verb.DeleteValue('', $false)
                                    $verb.SetValue('MUIVerb', 'Abrir no Terminal como administrador', [Microsoft.Win32.RegistryValueKind]::String)
                                    $verb.SetValue('Icon', $menuIcon, [Microsoft.Win32.RegistryValueKind]::String)
                                    $verb.SetValue('HasLUAShield', '', [Microsoft.Win32.RegistryValueKind]::String)
                                    $verb.DeleteValue('Position', $false)
                                    $verb.DeleteValue('SubCommands', $false)
                                    $verb.DeleteValue('DelegateExecute', $false)
                                    $verb.DeleteValue('LegacyDisable', $false)
                                    $verb.DeleteSubKeyTree('shell', $false)
                                    $verb.DeleteSubKeyTree('ExtendedSubCommandsKey', $false)
                                    $verb.DeleteSubKeyTree('command', $false)
                                    $command = $verb.CreateSubKey('command', $true)
                                    if ($null -eq $command) {
                                        throw ('Falha ao criar command elevado em ' + $path)
                                    }
                                    $command.SetValue('', [string]$op.ElevatedCommand, [Microsoft.Win32.RegistryValueKind]::String)
                                } finally {
                                    if ($null -ne $command) { $command.Dispose() }
                                    if ($null -ne $verb) { $verb.Dispose() }
                                }
                            }
                        } finally {
                            if ($null -ne $shellKey) { $shellKey.Dispose() }
                        }
                    }
                } finally {
                    if ($null -ne $classesRoot) { $classesRoot.Dispose() }
                    $baseKey.Dispose()
                }

                # CommandStore is cleanup-only. It is split by registry view.
                $commandViews = if ([Environment]::Is64BitOperatingSystem) {
                    @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)
                } else {
                    @([Microsoft.Win32.RegistryView]::Default)
                }
                foreach ($view in $commandViews) {
                    $commandBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                        [Microsoft.Win32.RegistryHive]::LocalMachine,
                        $view)
                    $commandStore = $null
                    try {
                        $commandStore = $commandBase.OpenSubKey($commandStorePath, $true)
                        if ($null -ne $commandStore) {
                            foreach ($legacyName in $legacyNames) {
                                $commandStore.DeleteSubKeyTree($legacyName, $false)
                            }
                        }
                    } finally {
                        if ($null -ne $commandStore) { $commandStore.Dispose() }
                        $commandBase.Dispose()
                    }
                }
                """;

            PowerShellResult result = RunElevatedPowerShellCommand(script, 60000);
            if (!result.Success)
            {
                errorMessage = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"PowerShell elevado terminou com código {result.ExitCode}."
                    : result.StandardError.Trim();
                return false;
            }

            if (!IsCertificateInLocalMachineTrustedPeople(thumbprint))
            {
                errorMessage = "O PowerShell elevado terminou com sucesso, mas o certificado não foi encontrado em LocalMachine\\TrustedPeople.";
                return false;
            }

            if (!AreMachineClassicTerminalContextMenuEntriesInstalled(
                    executablePath,
                    showNormal,
                    showElevated))
            {
                errorMessage = "O certificado foi confiado, mas os dois verbs clássicos não foram registrados corretamente.";
                return false;
            }

            RemoveCertificateFromCurrentUserTrustedPeople(thumbprint);
            return true;
        }

        private static MachineClassicMenuOperation[] CreateMachineClassicMenuOperations(
            string executablePath)
        {
            string quotedExecutable = QuoteWindowsArgument(executablePath);
            return TerminalContextMenuTargets
                .Select(target => new MachineClassicMenuOperation(
                    target.ClassShellPath,
                    $"{quotedExecutable} --open-terminal {QuoteWindowsArgument(target.ArgumentToken)}",
                    $"{quotedExecutable} --open-terminal-admin {QuoteWindowsArgument(target.ArgumentToken)}"))
                .ToArray();
        }

        private static string[] GetLegacyClassicCommandStoreNames()
        {
            return ClassicTerminalContextMenuCleanupTargets
                .SelectMany(target => new[]
                {
                    GetClassicCommandStoreCommandName(target, elevated: false),
                    GetClassicCommandStoreCommandName(target, elevated: true)
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool AreMachineClassicTerminalContextMenuEntriesInstalled(
            string executablePath,
            bool showNormal,
            bool showElevated)
        {
            // A previous build installed a standalone PowerShell launcher. Treat its
            // presence as an incomplete migration so Install/Repair removes it.
            if (File.Exists(GetLegacyClassicTerminalLauncherPath()))
            {
                return false;
            }

            MachineClassicMenuOperation operation = CreateMachineClassicMenuOperations(executablePath).Single();
            RegistryView classView = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;

            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, classView);
                using RegistryKey? classesRoot = baseKey.OpenSubKey(@"Software\Classes", writable: false);
                if (classesRoot == null)
                {
                    return false;
                }

                foreach (TerminalContextMenuTarget cleanupTarget in ClassicTerminalContextMenuCleanupTargets)
                {
                    using RegistryKey? shellKey = classesRoot.OpenSubKey(cleanupTarget.ClassShellPath, writable: false);
                    if (shellKey == null)
                    {
                        if (cleanupTarget.ClassShellPath.Equals(operation.ClassShellPath, StringComparison.OrdinalIgnoreCase) &&
                            (showNormal || showElevated))
                        {
                            return false;
                        }

                        continue;
                    }

                    using RegistryKey? oldParent = shellKey.OpenSubKey(
                        ClassicTerminalContextMenuParentKeyName,
                        writable: false);
                    if (oldParent != null)
                    {
                        return false;
                    }

                    bool isActiveTarget = cleanupTarget.ClassShellPath.Equals(
                        operation.ClassShellPath,
                        StringComparison.OrdinalIgnoreCase);
                    if (!IsMachineClassicTerminalCommandInstalled(
                            shellKey,
                            ClassicTerminalNormalKeyName,
                            operation.NormalCommand,
                            isActiveTarget && showNormal,
                            "Abrir no Terminal",
                            requireLuaShield: false) ||
                        !IsMachineClassicTerminalCommandInstalled(
                            shellKey,
                            ClassicTerminalElevatedKeyName,
                            operation.ElevatedCommand,
                            isActiveTarget && showElevated,
                            "Abrir no Terminal como administrador",
                            requireLuaShield: true))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsMachineClassicTerminalCommandInstalled(
            RegistryKey? shellKey,
            string keyName,
            string expectedCommand,
            bool shouldExist,
            string expectedMuiVerb,
            bool requireLuaShield)
        {
            using RegistryKey? verbKey = shellKey?.OpenSubKey(keyName, writable: false);
            if (!shouldExist)
            {
                return verbKey == null;
            }

            using RegistryKey? commandKey = verbKey?.OpenSubKey("command", writable: false);
            using RegistryKey? nestedShellKey = verbKey?.OpenSubKey("shell", writable: false);
            using RegistryKey? extendedSubCommandsKey = verbKey?.OpenSubKey(
                "ExtendedSubCommandsKey",
                writable: false);
            bool hasLuaShield = verbKey?.GetValueNames().Any(name =>
                name.Equals("HasLUAShield", StringComparison.OrdinalIgnoreCase)) == true;
            bool hasForbiddenParentShape = verbKey?.GetValueNames().Any(name =>
                string.IsNullOrEmpty(name) ||
                name.Equals("SubCommands", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DelegateExecute", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Position", StringComparison.OrdinalIgnoreCase)) == true ||
                nestedShellKey != null ||
                extendedSubCommandsKey != null;

            return verbKey != null &&
                commandKey != null &&
                !hasForbiddenParentShape &&
                string.Equals(verbKey.GetValue("MUIVerb")?.ToString(), expectedMuiVerb, StringComparison.Ordinal) &&
                string.Equals(commandKey.GetValue(null)?.ToString(), expectedCommand, StringComparison.OrdinalIgnoreCase) &&
                hasLuaShield == requireLuaShield;
        }

        private static bool HasAnyMachineClassicTerminalContextMenuEntry()
        {
            RegistryView[] commandViews = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Default };
            RegistryView classView = Environment.Is64BitOperatingSystem
                ? RegistryView.Registry64
                : RegistryView.Default;

            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, classView);
                using RegistryKey? classesRoot = baseKey.OpenSubKey(@"Software\Classes", writable: false);
                foreach (TerminalContextMenuTarget target in ClassicTerminalContextMenuCleanupTargets)
                {
                    using RegistryKey? shellKey = classesRoot?.OpenSubKey(target.ClassShellPath, writable: false);
                    if (shellKey == null)
                    {
                        continue;
                    }

                    foreach (string keyName in new[]
                    {
                        ClassicTerminalContextMenuParentKeyName,
                        ClassicTerminalNormalKeyName,
                        ClassicTerminalElevatedKeyName
                    })
                    {
                        using RegistryKey? ownedKey = shellKey.OpenSubKey(keyName, writable: false);
                        if (ownedKey != null)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Continue with CommandStore residue detection.
            }

            foreach (RegistryView view in commandViews)
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using RegistryKey? commandStore = baseKey.OpenSubKey(
                        ClassicTerminalCommandStoreRoot,
                        writable: false);
                    foreach (string legacyName in GetLegacyClassicCommandStoreNames())
                    {
                        using RegistryKey? legacy = commandStore?.OpenSubKey(legacyName, writable: false);
                        if (legacy != null)
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // Continue with the other view.
                }
            }

            return false;
        }

        private bool RemoveTerminalContextMenuCertificateTrust(string certificatePath, out string errorMessage)
        {
            errorMessage = string.Empty;
            string thumbprint = string.Empty;
            if (File.Exists(certificatePath) &&
                !TryReadTerminalContextMenuCertificate(certificatePath, out thumbprint, out errorMessage))
            {
                return false;
            }

            bool certificatePresent = HasMatchingCertificateInLocalMachineTrustedPeople(thumbprint);
            bool machineMenusPresent = HasAnyMachineClassicTerminalContextMenuEntry();
            bool launcherPresent = File.Exists(GetLegacyClassicTerminalLauncherPath());
            if (!certificatePresent && !machineMenusPresent && !launcherPresent)
            {
                if (!string.IsNullOrWhiteSpace(thumbprint))
                {
                    RemoveCertificateFromCurrentUserTrustedPeople(thumbprint);
                }

                return true;
            }

            string targetPathsJson = EscapePowerShellSingleQuotedString(JsonSerializer.Serialize(
                ClassicTerminalContextMenuCleanupTargets.Select(target => target.ClassShellPath).ToArray()));
            string legacyCommandNamesJson = EscapePowerShellSingleQuotedString(JsonSerializer.Serialize(
                GetLegacyClassicCommandStoreNames()));
            string commandStoreLiteral = EscapePowerShellSingleQuotedString(ClassicTerminalCommandStoreRoot);
            string parentKeyLiteral = EscapePowerShellSingleQuotedString(ClassicTerminalContextMenuParentKeyName);
            string normalKeyLiteral = EscapePowerShellSingleQuotedString(ClassicTerminalNormalKeyName);
            string elevatedKeyLiteral = EscapePowerShellSingleQuotedString(ClassicTerminalElevatedKeyName);
            string thumbprintLiteral = EscapePowerShellSingleQuotedString(thumbprint);
            string subjectLiteral = EscapePowerShellSingleQuotedString(TerminalContextMenuCertificateSubject);
            string legacyLauncherPathLiteral = EscapePowerShellSingleQuotedString(GetLegacyClassicTerminalLauncherPath());

            string script = $$"""
                $ErrorActionPreference = 'Stop'
                $expectedThumbprint = '{{thumbprintLiteral}}'
                $expectedSubject = '{{subjectLiteral}}'
                $legacyLauncherPath = '{{legacyLauncherPathLiteral}}'
                $parentName = '{{parentKeyLiteral}}'
                $normalName = '{{normalKeyLiteral}}'
                $elevatedName = '{{elevatedKeyLiteral}}'
                $commandStorePath = '{{commandStoreLiteral}}'
                $parsedPaths = ConvertFrom-Json -InputObject '{{targetPathsJson}}'
                $paths = @($parsedPaths | ForEach-Object { [string]$_ })
                $parsedLegacyNames = ConvertFrom-Json -InputObject '{{legacyCommandNamesJson}}'
                $legacyNames = @($parsedLegacyNames | ForEach-Object { [string]$_ })

                $storePath = 'Cert:\LocalMachine\TrustedPeople'
                $matches = Get-ChildItem -LiteralPath $storePath | Where-Object {
                    $_.Subject -eq $expectedSubject -and
                    ([string]::IsNullOrWhiteSpace($expectedThumbprint) -or $_.Thumbprint -eq $expectedThumbprint)
                }
                foreach ($item in $matches) {
                    Remove-Item -LiteralPath ($storePath + '\' + $item.Thumbprint) -Force
                }

                $classView = if ([Environment]::Is64BitOperatingSystem) {
                    [Microsoft.Win32.RegistryView]::Registry64
                } else {
                    [Microsoft.Win32.RegistryView]::Default
                }
                $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                    [Microsoft.Win32.RegistryHive]::LocalMachine,
                    $classView)
                $classesRoot = $null
                try {
                    $classesRoot = $baseKey.OpenSubKey('Software\Classes', $true)
                    if ($null -ne $classesRoot) {
                        foreach ($path in $paths) {
                            $shellKey = $null
                            try {
                                $shellKey = $classesRoot.OpenSubKey($path, $true)
                                if ($null -ne $shellKey) {
                                    $shellKey.DeleteSubKeyTree($parentName, $false)
                                    $shellKey.DeleteSubKeyTree($normalName, $false)
                                    $shellKey.DeleteSubKeyTree($elevatedName, $false)
                                }
                            } finally {
                                if ($null -ne $shellKey) { $shellKey.Dispose() }
                            }
                        }
                    }
                } finally {
                    if ($null -ne $classesRoot) { $classesRoot.Dispose() }
                    $baseKey.Dispose()
                }

                $commandViews = if ([Environment]::Is64BitOperatingSystem) {
                    @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)
                } else {
                    @([Microsoft.Win32.RegistryView]::Default)
                }
                foreach ($view in $commandViews) {
                    $commandBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                        [Microsoft.Win32.RegistryHive]::LocalMachine,
                        $view)
                    $commandStore = $null
                    try {
                        $commandStore = $commandBase.OpenSubKey($commandStorePath, $true)
                        if ($null -ne $commandStore) {
                            foreach ($legacyName in $legacyNames) {
                                $commandStore.DeleteSubKeyTree($legacyName, $false)
                            }
                        }
                    } finally {
                        if ($null -ne $commandStore) { $commandStore.Dispose() }
                        $commandBase.Dispose()
                    }
                }

                if (Test-Path -LiteralPath $legacyLauncherPath -PathType Leaf) {
                    Remove-Item -LiteralPath $legacyLauncherPath -Force
                }
                $legacyLauncherDirectory = Split-Path -Parent $legacyLauncherPath
                $legacyLauncherLog = Join-Path $legacyLauncherDirectory 'classic-terminal-launch.log'
                if (Test-Path -LiteralPath $legacyLauncherLog -PathType Leaf) {
                    Remove-Item -LiteralPath $legacyLauncherLog -Force
                }
                if (Test-Path -LiteralPath $legacyLauncherDirectory -PathType Container) {
                    $remaining = @(Get-ChildItem -LiteralPath $legacyLauncherDirectory -Force -ErrorAction SilentlyContinue)
                    if ($remaining.Count -eq 0) {
                        Remove-Item -LiteralPath $legacyLauncherDirectory -Force
                    }
                }
                """;

            PowerShellResult result = RunElevatedPowerShellCommand(script, 60000);
            if (!result.Success)
            {
                errorMessage = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"PowerShell elevado terminou com código {result.ExitCode}."
                    : result.StandardError.Trim();
                return false;
            }

            if (HasMatchingCertificateInLocalMachineTrustedPeople(thumbprint))
            {
                errorMessage = "O certificado ainda está presente em LocalMachine\\TrustedPeople após a remoção.";
                return false;
            }

            if (HasAnyMachineClassicTerminalContextMenuEntry())
            {
                errorMessage = "Os verbs clássicos do TutzApp ou resíduos antigos do CommandStore ainda estão presentes após a remoção.";
                return false;
            }

            if (File.Exists(GetLegacyClassicTerminalLauncherPath()))
            {
                errorMessage = "O launcher PowerShell do menu clássico ainda está presente após a remoção.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(thumbprint))
            {
                RemoveCertificateFromCurrentUserTrustedPeople(thumbprint);
            }

            return true;
        }

        private static bool TryReadTerminalContextMenuCertificate(
            string certificatePath,
            out string thumbprint,
            out string errorMessage)
        {
            thumbprint = string.Empty;
            errorMessage = string.Empty;
            try
            {
                using X509Certificate2 certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
                if (!certificate.Subject.Equals(TerminalContextMenuCertificateSubject, StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = $"Publisher inesperado no certificado: '{certificate.Subject}'.";
                    return false;
                }

                DateTime now = DateTime.Now;
                if (now < certificate.NotBefore || now > certificate.NotAfter)
                {
                    errorMessage = $"Certificado fora da validade: {certificate.NotBefore:O} a {certificate.NotAfter:O}.";
                    return false;
                }

                thumbprint = NormalizeCertificateThumbprint(certificate.Thumbprint);
                if (string.IsNullOrWhiteSpace(thumbprint))
                {
                    errorMessage = "O certificado não possui thumbprint.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Falha ao ler o certificado '{certificatePath}': {ex.Message}";
                return false;
            }
        }

        private static bool IsCertificateInLocalMachineTrustedPeople(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                return false;
            }

            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                X509Certificate2Collection matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    NormalizeCertificateThumbprint(thumbprint),
                    validOnly: false);
                return matches.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasMatchingCertificateInLocalMachineTrustedPeople(string thumbprint)
        {
            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (X509Certificate2 certificate in store.Certificates)
                {
                    if (!certificate.Subject.Equals(TerminalContextMenuCertificateSubject, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(thumbprint) ||
                        NormalizeCertificateThumbprint(certificate.Thumbprint)
                            .Equals(NormalizeCertificateThumbprint(thumbprint), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static void RemoveCertificateFromCurrentUserTrustedPeople(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                return;
            }

            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
                X509Certificate2Collection matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    NormalizeCertificateThumbprint(thumbprint),
                    validOnly: false);
                foreach (X509Certificate2 certificate in matches)
                {
                    store.Remove(certificate);
                }
            }
            catch
            {
                // Cleanup of the obsolete per-user trust entry is best effort only.
            }
        }

        private static string NormalizeCertificateThumbprint(string? thumbprint)
        {
            return string.Concat((thumbprint ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();
        }

        private bool IsTerminalContextMenuPackageRegistered()
        {
            string packageNameLiteral = EscapePowerShellSingleQuotedString(TerminalContextMenuPackageName);
            string script =
                "$package = Get-AppxPackage -Name '" + packageNameLiteral + "' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                "if ($null -ne $package) { Write-Output 'TUTZAPP_CONTEXT_MENU_PRESENT' }";

            PowerShellResult result = RunPowerShellCommand(script, 15000);
            if (!result.Success)
            {
                LogDebug($"IsTerminalContextMenuPackageRegistered: exit={result.ExitCode}; stderr='{result.StandardError.Trim()}'.");
                return false;
            }

            return result.StandardOutput.Contains("TUTZAPP_CONTEXT_MENU_PRESENT", StringComparison.Ordinal);
        }

        private bool ScheduleTerminalContextMenuPackageOperation(
            bool install,
            string externalLocation,
            string packagePath)
        {
            if (Interlocked.CompareExchange(
                ref TerminalContextMenuPackageOperationScheduled,
                1,
                0) != 0)
            {
                LogDebug("ScheduleTerminalContextMenuPackageOperation: outra operação de pacote já está pendente.");
                return false;
            }

            try
            {
                string publishedExecutablePath = Path.Combine(AppContext.BaseDirectory, "TutzApp.exe");
                string executablePath = File.Exists(publishedExecutablePath)
                    ? publishedExecutablePath
                    : Environment.ProcessPath ?? Path.Combine(externalLocation, "TutzApp.exe");
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    throw new FileNotFoundException(
                        "O executável do TutzApp não foi encontrado na localização externa do pacote.",
                        executablePath);
                }

                string operationDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TutzApp",
                    TerminalContextMenuOperationDirectoryName);
                Directory.CreateDirectory(operationDirectory);
                CleanupOldTerminalContextMenuOperations(operationDirectory);

                string operationId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
                string operationName = install ? "install" : "uninstall";
                string scriptPath = Path.Combine(
                    operationDirectory,
                    $"context-menu-{operationName}-{operationId}.ps1");
                string logPath = Path.Combine(
                    operationDirectory,
                    $"context-menu-{operationName}-{operationId}.log");

                string packageNameLiteral = EscapePowerShellSingleQuotedString(
                    TerminalContextMenuPackageName);
                string externalLocationLiteral = EscapePowerShellSingleQuotedString(
                    externalLocation);
                string packagePathLiteral = EscapePowerShellSingleQuotedString(
                    packagePath);
                string logPathLiteral = EscapePowerShellSingleQuotedString(logPath);
                string operationLiteral = install ? "install" : "uninstall";

                var script = new StringBuilder();
                script.AppendLine("$ErrorActionPreference = 'Stop'");
                script.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
                script.AppendLine($"$operation = '{operationLiteral}'");
                script.AppendLine($"$packageName = '{packageNameLiteral}'");
                script.AppendLine($"$externalLocation = '{externalLocationLiteral}'");
                script.AppendLine($"$sourcePackage = '{packagePathLiteral}'");
                script.AppendLine($"$operationLog = '{logPathLiteral}'");
                script.AppendLine("function Write-TutzOperationLog([string]$message) {");
                script.AppendLine("  $line = ('[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}' -f (Get-Date), $message)");
                script.AppendLine("  Add-Content -LiteralPath $operationLog -Value $line -Encoding UTF8");
                script.AppendLine("}");
                script.AppendLine($"$contextMenuAppIds = @('{TerminalContextMenuComAppId}', '{LegacyTerminalContextMenuComAppId}')");
                script.AppendLine("function Stop-TutzContextMenuSurrogates {");
                script.AppendLine("  $stopped = 0");
                script.AppendLine("  $surrogates = @(Get-CimInstance Win32_Process -Filter \"Name = 'dllhost.exe'\" -ErrorAction SilentlyContinue)");
                script.AppendLine("  foreach ($surrogate in $surrogates) {");
                script.AppendLine("    $commandLine = [string]$surrogate.CommandLine");
                script.AppendLine("    foreach ($appId in $contextMenuAppIds) {");
                script.AppendLine("      if ((-not [string]::IsNullOrWhiteSpace($commandLine)) -and $commandLine.IndexOf($appId, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {");
                script.AppendLine("        Stop-Process -Id ([int]$surrogate.ProcessId) -Force -ErrorAction SilentlyContinue");
                script.AppendLine("        $stopped++");
                script.AppendLine("        break");
                script.AppendLine("      }");
                script.AppendLine("    }");
                script.AppendLine("  }");
                script.AppendLine("  Write-TutzOperationLog ('Stopped matching COM surrogate processes: ' + $stopped)");
                script.AppendLine("}");
                script.AppendLine("$succeeded = $false");
                script.AppendLine("try {");
                script.AppendLine("  Write-TutzOperationLog ('Starting ' + $operation + ' operation without stopping the GUI.')");
                script.AppendLine("  Write-TutzOperationLog 'The GUI remains running; only the dedicated COM surrogate will be stopped.'");
                script.AppendLine("  Stop-TutzContextMenuSurrogates");
                script.AppendLine("  Start-Sleep -Milliseconds 300");
                script.AppendLine("  $existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Select-Object -First 1");
                script.AppendLine("  if ($operation -eq 'install') {");
                script.AppendLine("    $sourcePackage = (Resolve-Path -LiteralPath $sourcePackage -ErrorAction Stop).ProviderPath");
                script.AppendLine("    $externalLocation = (Resolve-Path -LiteralPath $externalLocation -ErrorAction Stop).ProviderPath");
                script.AppendLine("    $requiredExternalFiles = @(");
                script.AppendLine("      (Join-Path $externalLocation 'TutzApp.exe'),");
                script.AppendLine("      (Join-Path $externalLocation 'ShellIntegration\\TutzExplorerCommand.dll'),");
                script.AppendLine("      (Join-Path $externalLocation 'Assets\\StoreLogo.png'),");
                script.AppendLine("      (Join-Path $externalLocation 'Assets\\Square150x150Logo.png'),");
                script.AppendLine("      (Join-Path $externalLocation 'Assets\\Square44x44Logo.png')");
                script.AppendLine("    )");
                script.AppendLine("    foreach ($requiredFile in $requiredExternalFiles) {");
                script.AppendLine("      if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw ('Required external file is missing: ' + $requiredFile) }");
                script.AppendLine("    }");
                script.AppendLine("    $certificatePath = Join-Path $externalLocation 'ShellIntegration\\TutzApp.ContextMenu.cer'");
                script.AppendLine("    $certificate = New-Object -TypeName System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (,$certificatePath)");
                script.AppendLine("    $machineTrustedPath = 'Cert:\\LocalMachine\\TrustedPeople\\' + $certificate.Thumbprint");
                script.AppendLine("    if (-not (Test-Path -LiteralPath $machineTrustedPath)) { throw ('Certificate is not trusted in LocalMachine\\TrustedPeople: ' + $certificate.Thumbprint) }");
                script.AppendLine("    $cacheDirectory = Join-Path $env:LOCALAPPDATA 'TutzApp\\PackageInstallCache'");
                script.AppendLine("    New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null");
                script.AppendLine("    $cachedPackage = Join-Path $cacheDirectory 'TutzApp.ContextMenu.msix'");
                script.AppendLine("    Copy-Item -LiteralPath $sourcePackage -Destination $cachedPackage -Force");
                script.AppendLine("    $sourceHash = (Get-FileHash -LiteralPath $sourcePackage -Algorithm SHA256).Hash");
                script.AppendLine("    $cachedHash = (Get-FileHash -LiteralPath $cachedPackage -Algorithm SHA256).Hash");
                script.AppendLine("    if ($sourceHash -ne $cachedHash) { throw 'The cached MSIX failed SHA-256 verification.' }");
                script.AppendLine("    if ((Get-Item -LiteralPath $cachedPackage).Length -lt 4096) { throw 'The cached MSIX is empty or truncated.' }");
                script.AppendLine("    Add-Type -AssemblyName System.IO.Compression.FileSystem");
                script.AppendLine("    $archive = [System.IO.Compression.ZipFile]::OpenRead($cachedPackage)");
                script.AppendLine("    try {");
                script.AppendLine("      foreach ($entryName in @('AppxManifest.xml','AppxBlockMap.xml','AppxSignature.p7x')) {");
                script.AppendLine("        if ($null -eq $archive.GetEntry($entryName)) { throw ('Required MSIX entry is missing: ' + $entryName) }");
                script.AppendLine("      }");
                script.AppendLine("    } finally { if ($null -ne $archive) { $archive.Dispose() } }");
                script.AppendLine("    if ($null -ne $existing) {");
                script.AppendLine("      Write-TutzOperationLog ('Removing existing package ' + $existing.PackageFullName)");
                script.AppendLine("      Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction Stop");
                script.AppendLine("      $removeDeadline = [DateTime]::UtcNow.AddSeconds(30)");
                script.AppendLine("      while ($null -ne (Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Select-Object -First 1)) {");
                script.AppendLine("        if ([DateTime]::UtcNow -ge $removeDeadline) { throw 'The existing sparse package did not finish unregistering.' }");
                script.AppendLine("        Start-Sleep -Milliseconds 250");
                script.AppendLine("      }");
                script.AppendLine("    }");
                script.AppendLine("    Write-TutzOperationLog ('Registering package from ' + $cachedPackage)");
                script.AppendLine("    Add-AppxPackage -Path $cachedPackage -ExternalLocation $externalLocation -ErrorAction Stop");
                script.AppendLine("    $installDeadline = [DateTime]::UtcNow.AddSeconds(30)");
                script.AppendLine("    while ($null -eq (Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Select-Object -First 1)) {");
                script.AppendLine("      if ([DateTime]::UtcNow -ge $installDeadline) { throw 'The sparse package was not visible after registration.' }");
                script.AppendLine("      Start-Sleep -Milliseconds 250");
                script.AppendLine("    }");
                script.AppendLine("  } else {");
                script.AppendLine("    if ($null -ne $existing) {");
                script.AppendLine("      Write-TutzOperationLog ('Removing package ' + $existing.PackageFullName)");
                script.AppendLine("      Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction Stop");
                script.AppendLine("      $removeDeadline = [DateTime]::UtcNow.AddSeconds(30)");
                script.AppendLine("      while ($null -ne (Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Select-Object -First 1)) {");
                script.AppendLine("        if ([DateTime]::UtcNow -ge $removeDeadline) { throw 'The sparse package did not finish unregistering.' }");
                script.AppendLine("        Start-Sleep -Milliseconds 250");
                script.AppendLine("      }");
                script.AppendLine("    }");
                script.AppendLine("    $cacheDirectory = Join-Path $env:LOCALAPPDATA 'TutzApp\\PackageInstallCache'");
                script.AppendLine("    if (Test-Path -LiteralPath $cacheDirectory) { Remove-Item -LiteralPath $cacheDirectory -Recurse -Force }");
                script.AppendLine("  }");
                script.AppendLine("  # AppX can keep a short deployment lock after the cmdlet returns. Wait for a stable deployment state before reporting completion to the running GUI.");
                script.AppendLine("  $stableChecks = 0");
                script.AppendLine("  $settleDeadline = [DateTime]::UtcNow.AddSeconds(30)");
                script.AppendLine("  while ($stableChecks -lt 8) {");
                script.AppendLine("    $finalPackage = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Select-Object -First 1");
                script.AppendLine("    $expectedPresent = $operation -eq 'install'");
                script.AppendLine("    $present = $null -ne $finalPackage");
                script.AppendLine("    $statusIsHealthy = (-not $expectedPresent) -or ($present -and (([string]$finalPackage.Status) -eq 'Ok'))");
                script.AppendLine("    if (($present -eq $expectedPresent) -and $statusIsHealthy) { $stableChecks++ } else { $stableChecks = 0 }");
                script.AppendLine("    if ([DateTime]::UtcNow -ge $settleDeadline) { throw 'The sparse package did not reach a stable, healthy final state.' }");
                script.AppendLine("    Start-Sleep -Milliseconds 500");
                script.AppendLine("  }");
                script.AppendLine("  # AppXSVC can retain the deployment lock briefly after Get-AppxPackage reports Ok.");
                script.AppendLine("  Start-Sleep -Seconds 5");
                script.AppendLine("  $succeeded = $true");
                script.AppendLine("  Write-TutzOperationLog ('Operation completed successfully and deployment state is stable: ' + $operation)");
                script.AppendLine("} catch {");
                script.AppendLine("  Write-TutzOperationLog ('Operation failed: ' + ($_ | Out-String).Trim())");
                script.AppendLine("}");
                script.AppendLine("if ($succeeded) { exit 0 } else { exit 1 }");

                File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string powershellPath = Path.Combine(
                    systemDirectory,
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe");
                if (!File.Exists(powershellPath))
                {
                    powershellPath = "powershell.exe";
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = powershellPath,
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                        QuoteWindowsArgument(scriptPath),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = operationDirectory
                };

                ModernContextMenuWarmup.SuspendForPackageDeployment();

                Process? helper = Process.Start(startInfo);
                if (helper == null)
                {
                    ModernContextMenuWarmup.ResumeAfterPackageDeployment();
                    throw new InvalidOperationException("Process.Start retornou null para o helper de implantação.");
                }

                LogDebug($"ScheduleTerminalContextMenuPackageOperation: helper iniciado sem encerrar a GUI; pid={helper.Id}; operation='{operationName}'; script='{scriptPath}'; log='{logPath}'.");
                _ = MonitorTerminalContextMenuPackageOperationAsync(helper, install, logPath);
                return true;
            }
            catch (Exception ex)
            {
                ModernContextMenuWarmup.ResumeAfterPackageDeployment();
                Interlocked.Exchange(ref TerminalContextMenuPackageOperationScheduled, 0);
                LogDebug($"ScheduleTerminalContextMenuPackageOperation: falha ao iniciar helper: {ex}");
                return false;
            }
        }

        private async Task MonitorTerminalContextMenuPackageOperationAsync(
            Process helper,
            bool install,
            string logPath)
        {
            try
            {
                await helper.WaitForExitAsync().ConfigureAwait(false);
                int exitCode = helper.ExitCode;
                bool succeeded = exitCode == 0;

                LogDebug(
                    $"MonitorTerminalContextMenuPackageOperationAsync: operation='{(install ? "install" : "uninstall")}'; " +
                    $"exit={exitCode}; log='{logPath}'.");

                if (install || !succeeded)
                {
                    ModernContextMenuWarmup.ResumeAfterPackageDeployment();
                }

                NotifyShellAssociationChanged();
                if (succeeded)
                {
                    string message = install
                        ? "Menus moderno e clássico do Terminal instalados. O TutzApp continuou em execução."
                        : "Menus moderno e clássico do Terminal removidos. O TutzApp continuou em execução.";
                    SetStatusMessage(message);
                    NotificationRequested?.Invoke("TutzApp", message);
                }
                else
                {
                    string message = install
                        ? "Falha ao instalar o menu moderno. Consulte o log da integração."
                        : "Falha ao remover o menu moderno. Consulte o log da integração.";
                    SetStatusMessage(message);
                    NotificationRequested?.Invoke("TutzApp", $"{message}\n{logPath}");
                }
            }
            catch (Exception ex)
            {
                ModernContextMenuWarmup.ResumeAfterPackageDeployment();
                LogDebug($"MonitorTerminalContextMenuPackageOperationAsync: falha ao monitorar helper: {ex}");
                SetStatusMessage("Falha ao acompanhar a operação do menu moderno.");
            }
            finally
            {
                helper.Dispose();
                Interlocked.Exchange(ref TerminalContextMenuPackageOperationScheduled, 0);
            }
        }

        private static void CleanupOldTerminalContextMenuOperations(string operationDirectory)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (string path in Directory.EnumerateFiles(operationDirectory))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                        {
                            File.Delete(path);
                        }
                    }
                    catch
                    {
                        // A running or locked diagnostic belongs to another operation.
                    }
                }
            }
            catch
            {
                // Diagnostic cleanup is best effort and must not block deployment.
            }
        }

        private PowerShellResult RunElevatedPowerShellCommand(string script, int timeoutMilliseconds)
        {
            string logPath = Path.Combine(
                Path.GetTempPath(),
                $"TutzApp-elevated-{Environment.ProcessId}-{Guid.NewGuid():N}.log");

            try
            {
                string logLiteral = EscapePowerShellSingleQuotedString(logPath);
                string wrappedScript =
                    "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                    "$OutputEncoding = [System.Text.Encoding]::UTF8; " +
                    "$ErrorActionPreference = 'Stop'; " +
                    "try { " + script + "; " +
                    "  'SUCCESS' | Set-Content -LiteralPath '" + logLiteral + "' -Encoding UTF8; exit 0 " +
                    "} catch { " +
                    "  ($_ | Out-String) | Set-Content -LiteralPath '" + logLiteral + "' -Encoding UTF8; exit 1 " +
                    "}";

                string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript));
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedScript,
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    return new PowerShellResult(-1, string.Empty, "Process.Start retornou null.", false);
                }

                bool exited = process.WaitForExit(timeoutMilliseconds);
                if (!exited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best effort cleanup only.
                    }

                    return new PowerShellResult(-1, string.Empty, "PowerShell elevado excedeu o tempo limite.", true);
                }

                string log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
                return process.ExitCode == 0
                    ? new PowerShellResult(0, log, string.Empty, false)
                    : new PowerShellResult(process.ExitCode, string.Empty, log, false);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new PowerShellResult(-1, string.Empty, "A solicitação do UAC foi cancelada.", false);
            }
            catch (Exception ex)
            {
                return new PowerShellResult(-1, string.Empty, ex.ToString(), false);
            }
            finally
            {
                try
                {
                    if (File.Exists(logPath))
                    {
                        File.Delete(logPath);
                    }
                }
                catch
                {
                    // Temporary diagnostic cleanup is best effort only.
                }
            }
        }

        private PowerShellResult RunPowerShellCommand(string script, int timeoutMilliseconds)
        {
            try
            {
                string encodedScript = Convert.ToBase64String(
                    Encoding.Unicode.GetBytes(
                        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                        "$OutputEncoding = [System.Text.Encoding]::UTF8; " +
                        script));

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedScript,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    return new PowerShellResult(-1, string.Empty, "Process.Start retornou null.", false);
                }

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                bool exited = process.WaitForExit(timeoutMilliseconds);
                if (!exited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best effort cleanup only.
                    }

                    return new PowerShellResult(-1, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult(), true);
                }

                return new PowerShellResult(
                    process.ExitCode,
                    stdoutTask.GetAwaiter().GetResult(),
                    stderrTask.GetAwaiter().GetResult(),
                    false);
            }
            catch (Exception ex)
            {
                return new PowerShellResult(-1, string.Empty, ex.ToString(), false);
            }
        }

        private bool AreClassicTerminalContextMenuEntriesInstalled()
        {
            string externalLocation = GetTerminalContextMenuExternalLocation();
            string executablePath = Path.Combine(externalLocation, "TutzApp.exe");
            if (!File.Exists(executablePath) || HasAnyPerUserClassicTerminalContextMenuResidue())
            {
                return false;
            }

            using RegistryKey? stateKey = Registry.CurrentUser.OpenSubKey(
                TerminalContextMenuInstallRegistryPath,
                writable: false);
            bool showNormal = ReadTerminalContextMenuCommandState(
                stateKey,
                TerminalContextMenuShowNormalValue);
            bool showElevated = ReadTerminalContextMenuCommandState(
                stateKey,
                TerminalContextMenuShowElevatedValue);

            return AreMachineClassicTerminalContextMenuEntriesInstalled(
                executablePath,
                showNormal,
                showElevated);
        }

        private bool HasAnyClassicTerminalContextMenuEntry()
        {
            return HasAnyPerUserClassicTerminalContextMenuResidue() ||
                HasAnyMachineClassicTerminalContextMenuEntry();
        }

        private static bool HasAnyPerUserClassicTerminalContextMenuResidue()
        {
            foreach (TerminalContextMenuTarget target in ClassicTerminalContextMenuCleanupTargets)
            {
                string shellPath = @"Software\Classes\" + target.ClassShellPath;
                using RegistryKey? shellKey = Registry.CurrentUser.OpenSubKey(shellPath, writable: false);
                if (shellKey == null)
                {
                    continue;
                }

                if (shellKey.GetSubKeyNames().Any(name =>
                    name.Equals(ClassicTerminalContextMenuParentKeyName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(ClassicTerminalNormalKeyName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(ClassicTerminalElevatedKeyName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            using RegistryKey? commandStore = Registry.CurrentUser.OpenSubKey(
                ClassicTerminalCommandStoreRoot,
                writable: false);
            foreach (string legacyName in GetLegacyClassicCommandStoreNames())
            {
                using RegistryKey? legacy = commandStore?.OpenSubKey(legacyName, writable: false);
                if (legacy != null)
                {
                    return true;
                }
            }

            return false;
        }

        private bool SynchronizeClassicTerminalContextMenuEntries(RegistryKey? stateKey = null)
        {
            string externalLocation = GetTerminalContextMenuExternalLocation();
            string executablePath = Path.Combine(externalLocation, "TutzApp.exe");
            string certificatePath = Path.Combine(
                externalLocation,
                "ShellIntegration",
                TerminalContextMenuCertificateFileName);
            if (!File.Exists(executablePath) || !File.Exists(certificatePath))
            {
                return false;
            }

            bool showNormal = ReadTerminalContextMenuCommandState(
                stateKey,
                TerminalContextMenuShowNormalValue);
            bool showElevated = ReadTerminalContextMenuCommandState(
                stateKey,
                TerminalContextMenuShowElevatedValue);

            _ = RemoveClassicTerminalContextMenuEntries();
            if (!EnsureTerminalContextMenuCertificateTrusted(
                    certificatePath,
                    executablePath,
                    showNormal,
                    showElevated,
                    out string errorMessage))
            {
                LogDebug($"SynchronizeClassicTerminalContextMenuEntries: {errorMessage}");
                return false;
            }

            return true;
        }

        private static string GetClassicCommandStoreCommandName(
            TerminalContextMenuTarget target,
            bool elevated)
        {
            return $"TutzApp.Terminal.{target.CommandStoreSuffix}.{(elevated ? "Elevated" : "Normal")}";
        }

        private int RemoveClassicTerminalContextMenuEntries()
        {
            int removed = 0;
            using RegistryKey? stateKey = Registry.CurrentUser.OpenSubKey(
                TerminalContextMenuInstallRegistryPath,
                writable: true);
            using RegistryKey? commandStoreRoot = Registry.CurrentUser.OpenSubKey(
                ClassicTerminalCommandStoreRoot,
                writable: true);

            foreach (TerminalContextMenuTarget target in ClassicTerminalContextMenuCleanupTargets)
            {
                string shellPath = @"Software\Classes\" + target.ClassShellPath;
                try
                {
                    using (RegistryKey? shellKey = Registry.CurrentUser.OpenSubKey(shellPath, writable: true))
                    {
                        if (shellKey != null)
                        {
                            foreach (string keyName in new[]
                            {
                                ClassicTerminalContextMenuParentKeyName,
                                ClassicTerminalNormalKeyName,
                                ClassicTerminalElevatedKeyName
                            })
                            {
                                if (shellKey.GetSubKeyNames().Any(name =>
                                    name.Equals(keyName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    shellKey.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                                    removed++;
                                }
                            }
                        }
                    }

                    commandStoreRoot?.DeleteSubKeyTree(
                        GetClassicCommandStoreCommandName(target, elevated: false),
                        throwOnMissingSubKey: false);
                    commandStoreRoot?.DeleteSubKeyTree(
                        GetClassicCommandStoreCommandName(target, elevated: true),
                        throwOnMissingSubKey: false);
                    stateKey?.DeleteValue(
                        ClassicShellDefaultVerbStatePrefix + target.CommandStoreSuffix,
                        throwOnMissingValue: false);

                    DeleteEmptyCurrentUserRegistryPath(shellPath);
                }
                catch (Exception ex)
                {
                    LogDebug($"RemoveClassicTerminalContextMenuEntries: falha ao limpar HKCU\\{shellPath}: {ex.Message}");
                }
            }

            return removed;
        }

        private static void DeleteEmptyCurrentUserRegistryPath(string path)
        {
            const string classesRoot = @"Software\Classes";
            string currentPath = path;
            while (currentPath.StartsWith(classesRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                bool isEmpty;
                using (RegistryKey? currentKey = Registry.CurrentUser.OpenSubKey(currentPath, writable: false))
                {
                    if (currentKey == null)
                    {
                        int missingSeparator = currentPath.LastIndexOf('\\');
                        if (missingSeparator <= classesRoot.Length)
                        {
                            return;
                        }

                        currentPath = currentPath[..missingSeparator];
                        continue;
                    }

                    isEmpty = currentKey.GetSubKeyNames().Length == 0 &&
                        currentKey.GetValueNames().Length == 0;
                }

                if (!isEmpty)
                {
                    return;
                }

                int separator = currentPath.LastIndexOf('\\');
                if (separator <= classesRoot.Length)
                {
                    return;
                }

                string parentPath = currentPath[..separator];
                string keyName = currentPath[(separator + 1)..];
                using RegistryKey? parentKey = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
                parentKey?.DeleteSubKey(keyName, throwOnMissingSubKey: false);
                currentPath = parentPath;
            }
        }

        private static bool PathsEqual(string first, string second)
        {
            try
            {
                string normalizedFirst = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedSecond = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string EscapePowerShellSingleQuotedString(string value)
        {
            return value.Replace("'", "''", StringComparison.Ordinal);
        }

        private static void NotifyShellAssociationChanged()
        {
            NativeMethods.SHChangeNotify(
                NativeMethods.SHCNE_ASSOCCHANGED,
                NativeMethods.SHCNF_IDLIST | NativeMethods.SHCNF_FLUSH,
                IntPtr.Zero,
                IntPtr.Zero);

            // Explorer and alternative file managers cache different registry
            // surfaces. Broadcast both the class-association and shell-extension
            // areas after the synchronous SHChangeNotify flush.
            BroadcastShellSettingChange(@"Software\Classes");
            BroadcastShellSettingChange(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions");
        }

        private static void BroadcastShellSettingChange(string areaName)
        {
            IntPtr area = IntPtr.Zero;
            try
            {
                area = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(areaName);
                _ = NativeMethods.SendMessageTimeout(
                    NativeMethods.HWND_BROADCAST_HANDLE,
                    NativeMethods.WM_SETTINGCHANGE,
                    IntPtr.Zero,
                    area,
                    NativeMethods.SMTO_ABORTIFHUNG,
                    2000,
                    out _);
            }
            finally
            {
                if (area != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(area);
                }
            }
        }

    }
}
