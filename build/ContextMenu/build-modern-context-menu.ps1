param(
    [string]$RootPath = '',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ZigPath = '',

    [switch]$ManifestPreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-ProjectRoot {
    param([AllowEmptyString()][string]$RequestedRoot)

    $candidate = if ([string]::IsNullOrWhiteSpace($RequestedRoot)) {
        Join-Path $PSScriptRoot '..\..'
    }
    else {
        $RequestedRoot.Trim().Trim('"').Trim("'")
    }

    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw 'Project root is empty.'
    }

    $resolved = [System.IO.Path]::GetFullPath($candidate)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Project root does not exist: $resolved"
    }

    return $resolved.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Get-FirstExistingFile {
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $expanded = [Environment]::ExpandEnvironmentVariables($candidate.Trim().Trim('"'))
        if (Test-Path -LiteralPath $expanded -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($expanded)
        }
    }

    return $null
}

function Resolve-ZigExecutable {
    param([AllowEmptyString()][string]$RequestedPath)

    $candidates = New-Object 'System.Collections.Generic.List[string]'

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add($RequestedPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:TUTZ_ZIG)) {
        $candidates.Add($env:TUTZ_ZIG)
    }

    foreach ($commandName in @('zig.exe', 'zig')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
            $candidates.Add($command.Source)
        }
    }

    $candidates.Add((Join-Path $env:USERPROFILE 'scoop\apps\zig\current\zig.exe'))
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\zig\zig.exe'))
    $candidates.Add((Join-Path $env:ProgramData 'chocolatey\bin\zig.exe'))

    $resolved = Get-FirstExistingFile -Candidates ($candidates.ToArray())
    if ($null -eq $resolved) {
        throw @"
zig.exe was not found.

Add Zig to PATH or define an explicit path before running build.bat:
  set TUTZ_ZIG=D:\path\to\zig.exe
"@
    }

    return $resolved
}

function Add-UniqueDirectory {
    param(
        [Parameter(Mandatory = $true)][System.Collections.Generic.List[string]]$List,
        [AllowEmptyString()][string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"')).TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($expanded)) {
        return
    }

    foreach ($existing in $List) {
        if ([string]::Equals($existing, $expanded, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    $List.Add($expanded)
}

function Find-WindowsSdkTool {
    param([Parameter(Mandatory = $true)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return [System.IO.Path]::GetFullPath($command.Source)
    }

    $roots = New-Object 'System.Collections.Generic.List[string]'
    Add-UniqueDirectory -List $roots -Path $env:WindowsSdkVerBinPath
    Add-UniqueDirectory -List $roots -Path $env:WindowsSdkBinPath
    Add-UniqueDirectory -List $roots -Path $env:WindowsSdkDir
    Add-UniqueDirectory -List $roots -Path $env:KitsRoot10

    if (-not [string]::IsNullOrWhiteSpace($env:WindowsSdkDir)) {
        Add-UniqueDirectory -List $roots -Path (Join-Path $env:WindowsSdkDir 'bin')
        if (-not [string]::IsNullOrWhiteSpace($env:WindowsSDKVersion)) {
            $sdkVersion = $env:WindowsSDKVersion.TrimEnd('\', '/')
            Add-UniqueDirectory -List $roots -Path (Join-Path (Join-Path $env:WindowsSdkDir 'bin') $sdkVersion)
        }
    }

    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        Add-UniqueDirectory -List $roots -Path (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:TUTZ_EWDK_ROOT)) {
        Add-UniqueDirectory -List $roots -Path (Join-Path $env:TUTZ_EWDK_ROOT 'Program Files (x86)\Windows Kits\10\bin')
    }

    foreach ($root in $roots) {
        foreach ($relative in @("x64\$Name", "x86\$Name", $Name)) {
            $candidate = Join-Path $root $relative
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return [System.IO.Path]::GetFullPath($candidate)
            }
        }

        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }

        $versionDirectories = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending

        foreach ($directory in $versionDirectories) {
            foreach ($architecture in @('x64', 'x86')) {
                $candidate = Join-Path (Join-Path $directory.FullName $architecture) $Name
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    return [System.IO.Path]::GetFullPath($candidate)
                }
            }
        }
    }

    throw "$Name was not found in PATH or in the installed Windows SDK/EWDK."
}

function Invoke-NativeLogged {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$FailureMessage,
        [switch]$ShowOutputOnSuccess
    )

    $logDirectory = Split-Path -Parent $LogPath
    if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }
    Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue

    $previousPreference = $ErrorActionPreference
    $exitCode = -1
    try {
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments *> $LogPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if ($exitCode -ne 0) {
        Write-Host ''
        Write-Host "Command failed. Full output: $LogPath"
        if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
            Get-Content -LiteralPath $LogPath | ForEach-Object { Write-Host $_ }
        }
        throw "$FailureMessage Exit code: $exitCode"
    }

    if ($ShowOutputOnSuccess -and (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        Get-Content -LiteralPath $LogPath | ForEach-Object { Write-Host $_ }
    }
}

function Assert-WindowsX64Dll {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$RequiredExports
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 512) {
        throw "Generated file is too small to be a valid PE DLL: $Path"
    }

    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "Generated file does not have an MZ header: $Path"
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or $peOffset + 24 -ge $bytes.Length) {
        throw "Generated file has an invalid PE header offset: $Path"
    }

    if ($bytes[$peOffset] -ne 0x50 -or
        $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0x00 -or
        $bytes[$peOffset + 3] -ne 0x00) {
        throw "Generated file does not have a PE signature: $Path"
    }

    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x8664) {
        throw ("Generated DLL is not x86-64. PE machine: 0x{0:X4}" -f $machine)
    }

    $characteristics = [BitConverter]::ToUInt16($bytes, $peOffset + 22)
    if (($characteristics -band 0x2000) -eq 0) {
        throw "Generated PE file is not marked as a DLL: $Path"
    }

    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    foreach ($exportName in $RequiredExports) {
        if ($ascii.IndexOf($exportName, [StringComparison]::Ordinal) -lt 0) {
            throw "Required export name was not found in the generated DLL: $exportName"
        }
    }
}

function Assert-ManifestContract {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$AssetsDirectory
    )

    [xml]$document = Get-Content -LiteralPath $ManifestPath -Raw
    $namespaces = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
    $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $namespaces.AddNamespace('uap10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
    $namespaces.AddNamespace('desktop4', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/4')
    $namespaces.AddNamespace('desktop5', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/5')
    $namespaces.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
    $namespaces.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

    $package = $document.SelectSingleNode('/f:Package', $namespaces)
    $identity = $document.SelectSingleNode('/f:Package/f:Identity', $namespaces)
    $application = $document.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespaces)
    $allowExternal = $document.SelectSingleNode('/f:Package/f:Properties/uap10:AllowExternalContent', $namespaces)
    $runFullTrust = $document.SelectSingleNode('/f:Package/f:Capabilities/rescap:Capability[@Name="runFullTrust"]', $namespaces)
    $unvirtualized = $document.SelectSingleNode('/f:Package/f:Capabilities/rescap:Capability[@Name="unvirtualizedResources"]', $namespaces)
    $comClass = $document.SelectSingleNode('/f:Package/f:Applications/f:Application/f:Extensions/com:Extension/com:ComServer/com:SurrogateServer/com:Class', $namespaces)
    $verbs = $document.SelectNodes('/f:Package/f:Applications/f:Application/f:Extensions/desktop4:Extension/desktop4:FileExplorerContextMenus/desktop5:ItemType/desktop5:Verb', $namespaces)

    if ($null -eq $package) {
        throw 'The package manifest has no Package root element.'
    }

    $ignorable = @($package.GetAttribute('IgnorableNamespaces') -split '\s+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($requiredAlias in @('com', 'desktop4', 'desktop5', 'rescap', 'uap', 'uap10')) {
        if ($ignorable -notcontains $requiredAlias) {
            throw "IgnorableNamespaces is missing the required alias: $requiredAlias"
        }
    }

    if ($null -eq $identity -or $identity.GetAttribute('Name') -ne 'ArthurCuesta.TutzApp.ContextMenu') {
        throw 'The package Identity Name does not match the application manifest.'
    }
    if ($identity.GetAttribute('Publisher') -ne 'CN=TutzApp Local Development') {
        throw 'The package Publisher does not match the signing certificate/application manifest.'
    }
    if ($identity.GetAttribute('ProcessorArchitecture') -ne 'neutral') {
        throw 'The sparse identity package must use ProcessorArchitecture=neutral.'
    }
    if ($null -eq $application -or $application.GetAttribute('Id') -ne 'TutzApp') {
        throw 'The package Application Id does not match app.manifest.'
    }

    $uap10Namespace = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
    $trustLevel = $application.GetAttribute('TrustLevel', $uap10Namespace)
    $runtimeBehavior = $application.GetAttribute('RuntimeBehavior', $uap10Namespace)
    if ($trustLevel -ne 'mediumIL') {
        throw 'The sparse Win32 application must use uap10:TrustLevel=mediumIL.'
    }
    if ($runtimeBehavior -ne 'win32App') {
        throw 'The sparse Win32 application must use uap10:RuntimeBehavior=win32App.'
    }
    if (-not [string]::IsNullOrWhiteSpace($application.GetAttribute('EntryPoint'))) {
        throw 'The external-location Win32 application must not use the legacy EntryPoint attribute.'
    }
    if ($null -eq $allowExternal -or $allowExternal.InnerText.Trim() -ne 'true') {
        throw 'uap10:AllowExternalContent must be true for the sparse package.'
    }
    if ($null -eq $runFullTrust -or $null -eq $unvirtualized) {
        throw 'The sparse package requires runFullTrust and unvirtualizedResources.'
    }
    if ($null -eq $comClass -or $comClass.GetAttribute('Id') -ne 'F13BF4B5-9064-4971-B73E-1D5EC8F2EAA5') {
        throw 'The COM class registration is missing or has the wrong CLSID.'
    }
    if ($verbs.Count -ne 2) {
        throw "Expected exactly two Explorer context-menu verbs; found $($verbs.Count)."
    }

    foreach ($verb in $verbs) {
        $verbId = $verb.GetAttribute('Id')
        $verbClsid = $verb.GetAttribute('Clsid')
        if ($verbId -notmatch '^[A-Za-z0-9. -]{1,64}$') {
            throw "Invalid desktop5:Verb Id: $verbId"
        }
        if ($verbClsid -ne $comClass.GetAttribute('Id')) {
            throw "Verb CLSID does not match COM class CLSID: $verbId"
        }
    }

    foreach ($assetName in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
        $assetPath = Join-Path $AssetsDirectory $assetName
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            throw "Required MSIX asset is missing: $assetPath"
        }
    }
}

function New-SparsePackageLayout {
    param(
        [Parameter(Mandatory = $true)][string]$LayoutDirectory,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$AssetsDirectory
    )

    if (Test-Path -LiteralPath $LayoutDirectory) {
        Remove-Item -LiteralPath $LayoutDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $LayoutDirectory -Force | Out-Null
    Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $LayoutDirectory 'AppxManifest.xml') -Force

    $layoutAssets = Join-Path $LayoutDirectory 'Assets'
    New-Item -ItemType Directory -Path $layoutAssets -Force | Out-Null
    foreach ($assetName in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
        Copy-Item -LiteralPath (Join-Path $AssetsDirectory $assetName) -Destination (Join-Path $layoutAssets $assetName) -Force
    }
}

$root = Resolve-ProjectRoot -RequestedRoot $RootPath
$manifestSource = Join-Path $root 'build\ContextMenu\AppxManifest.xml'
$assetsDirectory = Join-Path $root 'Assets'
$layoutDir = Join-Path $root 'build\ContextMenu\package-layout'
$logsDir = Join-Path $root 'build\ContextMenu\logs'
$makeAppxLog = Join-Path $logsDir 'makeappx.log'
$preflightLog = Join-Path $logsDir 'makeappx-preflight.log'
$preflightPackage = Join-Path $root 'build\ContextMenu\manifest-preflight.msix'

Assert-ManifestContract -ManifestPath $manifestSource -AssetsDirectory $assetsDirectory

if ($ManifestPreflightOnly) {
    $makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
    Write-Host "Project root: $root"
    Write-Host "MakeAppx: $makeAppx"
    Write-Host 'Validating sparse MSIX manifest with MakeAppx...'

    New-SparsePackageLayout -LayoutDirectory $layoutDir -ManifestPath $manifestSource -AssetsDirectory $assetsDirectory
    Remove-Item -LiteralPath $preflightPackage -Force -ErrorAction SilentlyContinue

    try {
        Invoke-NativeLogged `
            -Executable $makeAppx `
            -Arguments @('pack', '/o', '/nv', '/v', '/d', $layoutDir, '/p', $preflightPackage) `
            -LogPath $preflightLog `
            -FailureMessage 'MakeAppx manifest preflight failed.'

        Write-Host 'Sparse MSIX manifest preflight passed.'
        Remove-Item -LiteralPath $preflightPackage -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $layoutDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    catch {
        Write-Host "Failed package layout retained at: $layoutDir"
        throw
    }

    return
}

$zig = Resolve-ZigExecutable -RequestedPath $ZigPath
Write-Host "Project root: $root"
Write-Host "Zig: $zig"
Write-Host 'Native target: x86_64-windows-gnu'

$nativeSource = Join-Path $root 'src\Features\ContextMenu\Native\ExplorerCommand.cpp'
$moduleDefinition = Join-Path $root 'src\Features\ContextMenu\Native\TutzExplorerCommand.def'
$nativeOutputDirectory = Join-Path $root "src\Features\ContextMenu\Native\bin\x64\$Configuration"
$nativeOutput = Join-Path $nativeOutputDirectory 'TutzExplorerCommand.dll'
$integrationDir = Join-Path $root 'ShellIntegration'
$integrationDll = Join-Path $integrationDir 'TutzExplorerCommand.dll'
$packagePath = Join-Path $integrationDir 'TutzApp.ContextMenu.msix'
$certificatePath = Join-Path $integrationDir 'TutzApp.ContextMenu.cer'
$zigLog = Join-Path $logsDir 'zig-shell-extension.log'
$packageUnpackLog = Join-Path $logsDir 'makeappx-unpack-verify.log'
$packageVerifyDirectory = Join-Path $root 'build\ContextMenu\package-open-verification'
$publisher = 'CN=TutzApp Local Development'

foreach ($requiredFile in @($nativeSource, $moduleDefinition, $manifestSource)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file was not found: $requiredFile"
    }
}

New-Item -ItemType Directory -Path $nativeOutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $integrationDir -Force | Out-Null
Remove-Item -LiteralPath $nativeOutput -Force -ErrorAction SilentlyContinue

$zigArguments = @(
    'c++',
    '-target', 'x86_64-windows-gnu',
    '-std=c++17',
    '-shared',
    '-fno-exceptions',
    '-fno-rtti',
    '-DUNICODE',
    '-D_UNICODE'
)

if ([string]::Equals($Configuration, 'Debug', [StringComparison]::OrdinalIgnoreCase)) {
    $zigArguments += @('-O0', '-gcodeview', '-D_DEBUG')
}
else {
    $zigArguments += @('-O2', '-DNDEBUG')
}

$zigArguments += @(
    $nativeSource,
    $moduleDefinition,
    '-lole32',
    '-lshell32',
    '-lshlwapi',
    '-ladvapi32',
    '-o', $nativeOutput
)

Write-Host 'Building native IExplorerCommand extension with Zig...'
Invoke-NativeLogged `
    -Executable $zig `
    -Arguments $zigArguments `
    -LogPath $zigLog `
    -FailureMessage "Zig failed to build $nativeSource."

if (-not (Test-Path -LiteralPath $nativeOutput -PathType Leaf)) {
    throw "Zig completed but the DLL was not created: $nativeOutput"
}

$nativeLength = (Get-Item -LiteralPath $nativeOutput).Length
if ($nativeLength -lt 4096) {
    throw "The generated DLL is unexpectedly small ($nativeLength bytes): $nativeOutput"
}

Assert-WindowsX64Dll `
    -Path $nativeOutput `
    -RequiredExports @('DllGetClassObject', 'DllCanUnloadNow')
Write-Host 'PE validation: x86-64 DLL with required COM exports.'
Write-Host "Zig output log: $zigLog"

Copy-Item -LiteralPath $nativeOutput -Destination $integrationDll -Force
Write-Host "Native DLL: $integrationDll ($nativeLength bytes)"

$makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
$signTool = Find-WindowsSdkTool -Name 'signtool.exe'
Write-Host "MakeAppx: $makeAppx"
Write-Host "SignTool: $signTool"

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $publisher -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddDays(30)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $certificate) {
    Write-Host 'Creating local code-signing certificate for the sparse package...'
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $publisher `
        -FriendlyName 'TutzApp modern context menu package' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(5)
}

Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT -Force | Out-Null
New-SparsePackageLayout -LayoutDirectory $layoutDir -ManifestPath $manifestSource -AssetsDirectory $assetsDirectory
Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue

try {
    Write-Host 'Packing sparse MSIX identity package...'
    Invoke-NativeLogged `
        -Executable $makeAppx `
        -Arguments @('pack', '/o', '/nv', '/v', '/d', $layoutDir, '/p', $packagePath) `
        -LogPath $makeAppxLog `
        -FailureMessage 'MakeAppx failed to create the sparse package.'

    Write-Host 'Signing sparse MSIX identity package...'
    $signLog = Join-Path $logsDir 'signtool.log'
    Invoke-NativeLogged `
        -Executable $signTool `
        -Arguments @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, '/s', 'My', $packagePath) `
        -LogPath $signLog `
        -FailureMessage 'SignTool failed to sign the sparse package.'

    if (Test-Path -LiteralPath $packageVerifyDirectory) {
        Remove-Item -LiteralPath $packageVerifyDirectory -Recurse -Force
    }

    Write-Host 'Opening the signed sparse package with MakeAppx (/nv)...'
    Invoke-NativeLogged `
        -Executable $makeAppx `
        -Arguments @('unpack', '/o', '/nv', '/p', $packagePath, '/d', $packageVerifyDirectory) `
        -LogPath $packageUnpackLog `
        -FailureMessage 'MakeAppx could not reopen the signed sparse package with semantic validation disabled.'

    foreach ($requiredPackageFile in @('AppxManifest.xml', 'AppxBlockMap.xml')) {
        $verifiedPath = Join-Path $packageVerifyDirectory $requiredPackageFile
        if (-not (Test-Path -LiteralPath $verifiedPath -PathType Leaf)) {
            throw "The signed package reopened, but a required package file is missing: $verifiedPath"
        }
    }

    $unpackedManifest = Join-Path $packageVerifyDirectory 'AppxManifest.xml'
    Assert-ManifestContract -ManifestPath $unpackedManifest -AssetsDirectory (Join-Path $packageVerifyDirectory 'Assets')

    $packageItem = Get-Item -LiteralPath $packagePath
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    Write-Host "Signed package reopened successfully: $($packageItem.Length) bytes"
    Write-Host "Package SHA-256: $packageHash"

    Remove-Item -LiteralPath $packageVerifyDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $layoutDir -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    Write-Host "Failed package layout retained at: $layoutDir"
    throw
}

Write-Host ''
Write-Host 'Modern context-menu integration built:'
Write-Host "  DLL:  $integrationDll"
Write-Host "  MSIX: $packagePath"
Write-Host "  CER:  $certificatePath"
Write-Host "  Logs: $logsDir"
