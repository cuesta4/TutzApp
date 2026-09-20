param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'TutzApp.csproj'
$runtime = 'win-x64'
$publishDirectory = Join-Path $root 'publish-fast'
$publishedExecutable = Join-Path $publishDirectory 'TutzApp.exe'
$outputExecutable = Join-Path $root 'TutzApp.exe'
$ewdkEnvironment = $env:TUTZ_EWDK_ENV
if ([string]::IsNullOrWhiteSpace($ewdkEnvironment)) {
    $ewdkEnvironment = Join-Path $root 'EWDK-LLVM.env'
}

if (-not (Test-Path -LiteralPath $ewdkEnvironment -PathType Leaf)) {
    throw "EWDK environment file not found: $ewdkEnvironment. Set TUTZ_EWDK_ENV to the local EWDK-LLVM.env path."
}

foreach ($line in [System.IO.File]::ReadLines($ewdkEnvironment)) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
        continue
    }

    $separator = $line.IndexOf('=')
    if ($separator -le 0) {
        continue
    }

    $name = $line.Substring(0, $separator)
    if ($name -in @('HOME', 'CODEX_HOME')) {
        continue
    }

    Set-Item -LiteralPath "Env:$name" -Value $line.Substring($separator + 1)
}

if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK was not found in PATH.'
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

try {
    Write-Host "Publishing $project incrementally ($Configuration, $runtime)..."
    & dotnet.exe publish $project `
        -c $Configuration `
        -r $runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDirectory `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw "Published executable was not found: $publishedExecutable"
    }

    $runningInstances = @(Get-Process -Name 'TutzApp' -ErrorAction SilentlyContinue)
    if ($runningInstances.Count -gt 0) {
        $runningInstances | Stop-Process -Force -ErrorAction Stop
        foreach ($process in $runningInstances) {
            if (-not $process.WaitForExit(10000)) {
                throw "TutzApp process did not exit in time: PID $($process.Id)"
            }
        }
    }

    Copy-Item -LiteralPath $publishedExecutable -Destination $outputExecutable -Force
    Write-Host "Fast build completed successfully: $outputExecutable"
}
finally {
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}
