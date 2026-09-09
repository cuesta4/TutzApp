param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$hadErrors = $false
foreach ($item in $Path) {
    $resolved = [System.IO.Path]::GetFullPath($item)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        Write-Error "PowerShell script not found: $resolved"
        $hadErrors = $true
        continue
    }

    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $resolved,
        [ref]$tokens,
        [ref]$errors) | Out-Null

    if ($errors.Count -eq 0) {
        Write-Host "PowerShell syntax OK: $resolved"
        continue
    }

    $hadErrors = $true
    Write-Host "PowerShell syntax FAILED: $resolved" -ForegroundColor Red
    foreach ($parseError in $errors) {
        $extent = $parseError.Extent
        $message = "  Line {0}, column {1}: {2}" -f (
            $extent.StartLineNumber,
            $extent.StartColumnNumber,
            $parseError.Message)
        Write-Host $message -ForegroundColor Red
        if (-not [string]::IsNullOrWhiteSpace($extent.Text)) {
            Write-Host ("    {0}" -f $extent.Text.Trim()) -ForegroundColor DarkRed
        }
    }
}

if ($hadErrors) {
    exit 1
}

exit 0
