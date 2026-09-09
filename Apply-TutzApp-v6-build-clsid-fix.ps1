#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter()]
    [string]$ProjectRoot = (Get-Location).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$oldClsid = '8E0BA745-6D15-4C5B-BEF8-0F73A4C82677'
$newClsid = 'F13BF4B5-9064-4971-B73E-1D5EC8F2EAA5'

$projectRootPath = [System.IO.Path]::GetFullPath($ProjectRoot)
$scriptPath = Join-Path $projectRootPath 'build\ContextMenu\build-modern-context-menu.ps1'
$manifestPath = Join-Path $projectRootPath 'build\ContextMenu\AppxManifest.xml'

if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Build script not found: $scriptPath"
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Manifest not found: $manifestPath"
}

[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaceManager.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespaceManager.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')

$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaceManager)
$comClass = $manifest.SelectSingleNode('//com:Class', $namespaceManager)
if ($null -eq $identity -or $null -eq $comClass) {
    throw 'Could not locate the package Identity or COM Class in AppxManifest.xml.'
}

$manifestClsid = [string]$comClass.Id
if (-not $manifestClsid.Equals($newClsid, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected manifest CLSID '$manifestClsid'. Expected '$newClsid'."
}
if (-not ([string]$identity.ProcessorArchitecture).Equals('neutral', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Sparse package ProcessorArchitecture must remain neutral. Current value: '$($identity.ProcessorArchitecture)'."
}

$source = Get-Content -LiteralPath $scriptPath -Raw
$oldCount = ([regex]::Matches($source, [regex]::Escape($oldClsid), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
$newCountBefore = ([regex]::Matches($source, [regex]::Escape($newClsid), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count

if ($oldCount -eq 0 -and $newCountBefore -gt 0) {
    Write-Host "Build script already validates CLSID $newClsid." -ForegroundColor Green
    exit 0
}
if ($oldCount -eq 0) {
    throw "The build script contains neither the old nor the new CLSID. Refusing an ambiguous edit."
}

$backupPath = "$scriptPath.before-v6-clsid-fix.bak"
Copy-Item -LiteralPath $scriptPath -Destination $backupPath -Force

$updated = [regex]::Replace(
    $source,
    [regex]::Escape($oldClsid),
    $newClsid,
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

[System.IO.File]::WriteAllText(
    $scriptPath,
    $updated,
    [System.Text.UTF8Encoding]::new($false))

$verified = Get-Content -LiteralPath $scriptPath -Raw
$remainingOld = ([regex]::Matches($verified, [regex]::Escape($oldClsid), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
$newCountAfter = ([regex]::Matches($verified, [regex]::Escape($newClsid), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count

if ($remainingOld -ne 0 -or $newCountAfter -lt $oldCount) {
    Copy-Item -LiteralPath $backupPath -Destination $scriptPath -Force
    throw 'Post-edit verification failed. The original build script was restored.'
}

Write-Host "Build preflight CLSID synchronized successfully." -ForegroundColor Green
Write-Host "Replaced occurrences: $oldCount"
Write-Host "Current CLSID: $newClsid"
Write-Host "Backup: $backupPath"
