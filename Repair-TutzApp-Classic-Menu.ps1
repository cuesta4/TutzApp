#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$NoRestartExplorer
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath)
    )
    if ($NoRestartExplorer) {
        $arguments += '-NoRestartExplorer'
    }

    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments | Out-Null
    exit 0
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logRoot = Join-Path ([Environment]::GetFolderPath('Desktop')) "TutzApp-classic-menu-repair-$timestamp"
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$logPath = Join-Path $logRoot 'repair.log'

function Write-RepairLog([string]$Message) {
    $line = '[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}' -f (Get-Date), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Export-RegistryKey([string]$NativePath, [string]$FileName) {
    try {
        & reg.exe query $NativePath *> $null
        if ($LASTEXITCODE -eq 0) {
            & reg.exe export $NativePath (Join-Path $logRoot $FileName) /y *> $null
            Write-RepairLog "Backup: $NativePath"
        }
    }
    catch {
        Write-RepairLog "Backup ignorado para ${NativePath}: $($_.Exception.Message)"
    }
}

function Remove-EmptyCurrentUserClassPath([string]$Path) {
    $classesRoot = 'Software\Classes'
    $currentPath = $Path

    while ($currentPath.StartsWith($classesRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($currentPath, $false)
        if ($null -eq $key) {
            $separator = $currentPath.LastIndexOf('\')
            if ($separator -le $classesRoot.Length) { return }
            $currentPath = $currentPath.Substring(0, $separator)
            continue
        }

        try {
            $isEmpty = ($key.GetSubKeyNames().Count -eq 0 -and $key.GetValueNames().Count -eq 0)
        }
        finally {
            $key.Dispose()
        }

        if (-not $isEmpty) { return }
        $separator = $currentPath.LastIndexOf('\')
        if ($separator -le $classesRoot.Length) { return }

        $parentPath = $currentPath.Substring(0, $separator)
        $keyName = $currentPath.Substring($separator + 1)
        $parent = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($parentPath, $true)
        if ($null -eq $parent) { return }
        try {
            $parent.DeleteSubKey($keyName, $false)
            Write-RepairLog "Chave HKCU vazia removida: $currentPath"
        }
        finally {
            $parent.Dispose()
        }
        $currentPath = $parentPath
    }
}

$targets = @(
    @{ Path = 'Directory\Background\shell'; Suffix = 'DirectoryBackground' },
    @{ Path = 'Directory\shell'; Suffix = 'Directory' },
    @{ Path = 'Drive\shell'; Suffix = 'Drive' },
    @{ Path = 'Folder\shell'; Suffix = 'Folder' },
    @{ Path = 'LibraryFolder\Background\shell'; Suffix = 'LibraryBackground' }
)
$ownedKeys = @('TutzApp.Terminal', 'TutzApp.OpenTerminal', 'TutzApp.OpenTerminalAdmin')
$statePath = 'Software\TutzApp\ShellIntegration'
$commandStorePath = 'Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell'

Export-RegistryKey 'HKCU\Software\Classes\Folder' 'HKCU-Classes-Folder.reg'
Export-RegistryKey 'HKCU\Software\Classes\Drive' 'HKCU-Classes-Drive.reg'
Export-RegistryKey 'HKCU\Software\Classes\Directory' 'HKCU-Classes-Directory.reg'
Export-RegistryKey 'HKCU\Software\Classes\LibraryFolder' 'HKCU-Classes-LibraryFolder.reg'
Export-RegistryKey 'HKLM\Software\Classes\Directory' 'HKLM-Classes-Directory.reg'

# Per-user cleanup: delete only TutzApp-owned subkeys and bookkeeping values.
# The default value of every shell container is intentionally untouched.
$stateKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($statePath, $true)
$commandStore = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($commandStorePath, $true)
try {
    foreach ($target in $targets) {
        $shellPath = 'Software\Classes\' + [string]$target.Path
        $shellKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($shellPath, $true)
        try {
            if ($null -ne $shellKey) {
                foreach ($ownedKey in $ownedKeys) {
                    if ($shellKey.GetSubKeyNames() -contains $ownedKey) {
                        $shellKey.DeleteSubKeyTree($ownedKey, $false)
                        Write-RepairLog "Registro por usuário removido: HKCU\$shellPath\$ownedKey"
                    }
                }
            }
        }
        finally {
            if ($null -ne $shellKey) { $shellKey.Dispose() }
        }

        foreach ($elevated in @($false, $true)) {
            $name = 'TutzApp.Terminal.{0}.{1}' -f [string]$target.Suffix, $(if ($elevated) { 'Elevated' } else { 'Normal' })
            if ($null -ne $commandStore) {
                $commandStore.DeleteSubKeyTree($name, $false)
            }
        }
        if ($null -ne $stateKey) {
            $stateKey.DeleteValue(('ClassicShellDefaultVerbManaged.' + [string]$target.Suffix), $false)
        }

        Remove-EmptyCurrentUserClassPath $shellPath
    }
}
finally {
    if ($null -ne $commandStore) { $commandStore.Dispose() }
    if ($null -ne $stateKey) { $stateKey.Dispose() }
}

# Software\Classes is shared between registry views. Clean the native machine view once.
$classView = if ([Environment]::Is64BitOperatingSystem) {
    [Microsoft.Win32.RegistryView]::Registry64
}
else {
    [Microsoft.Win32.RegistryView]::Default
}
$baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $classView)
$classes = $baseKey.OpenSubKey('Software\Classes', $true)
try {
    foreach ($target in $targets) {
        if ($null -eq $classes) { break }
        $shellKey = $classes.OpenSubKey([string]$target.Path, $true)
        if ($null -eq $shellKey) { continue }
        try {
            foreach ($ownedKey in $ownedKeys) {
                $shellKey.DeleteSubKeyTree($ownedKey, $false)
            }
        }
        finally {
            $shellKey.Dispose()
        }
    }
    Write-RepairLog "Registros clássicos de máquina removidos na visão $classView."
}
finally {
    if ($null -ne $classes) { $classes.Dispose() }
    $baseKey.Dispose()
}

# CommandStore is not shared; remove residues from both views.
$commandViews = if ([Environment]::Is64BitOperatingSystem) {
    @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)
}
else {
    @([Microsoft.Win32.RegistryView]::Default)
}
foreach ($view in $commandViews) {
    $commandBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
    $machineStore = $commandBase.OpenSubKey($commandStorePath, $true)
    try {
        if ($null -ne $machineStore) {
            foreach ($target in $targets) {
                foreach ($elevated in @($false, $true)) {
                    $name = 'TutzApp.Terminal.{0}.{1}' -f [string]$target.Suffix, $(if ($elevated) { 'Elevated' } else { 'Normal' })
                    $machineStore.DeleteSubKeyTree($name, $false)
                }
            }
        }
    }
    finally {
        if ($null -ne $machineStore) { $machineStore.Dispose() }
        $commandBase.Dispose()
    }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TutzShellRefresh {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@
[TutzShellRefresh]::SHChangeNotify(0x08000000, 0x1000, [IntPtr]::Zero, [IntPtr]::Zero)
Write-RepairLog 'SHCNE_ASSOCCHANGED enviado ao Shell.'

if (-not $NoRestartExplorer) {
    Get-Process explorer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Process explorer.exe
    Write-RepairLog 'Explorer reiniciado.'
}

Write-RepairLog 'Reparo concluído. Somente registros pertencentes ao TutzApp foram removidos.'
Write-Host "`nBackup e log: $logRoot"
