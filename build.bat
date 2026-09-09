@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "PROJECT=TutzApp.csproj"
set "TEST_PROJECT=tests\TutzApp.Tests\TutzApp.Tests.csproj"
set "CONFIGURATION=Release"
set "RUNTIME=win-x64"
set "PUBLISH_DIR=publish-temp"
set "OUTPUT_EXE=TutzApp.exe"

rem Optional override:
rem   set TUTZ_ZIG=D:\path\to\zig.exe

cd /d "%ROOT%" || exit /b 1

where dotnet.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet SDK was not found in PATH.
    set "ERR=1"
    goto :fail
)

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Windows PowerShell was not found.
    set "ERR=1"
    goto :fail
)

echo Validating PowerShell build scripts...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build\ContextMenu\validate-powershell-scripts.ps1" -Path "%ROOT%build\ContextMenu\build-modern-context-menu.ps1"
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)

echo Validating sparse MSIX manifest with MakeAppx...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build\ContextMenu\build-modern-context-menu.ps1" -ManifestPreflightOnly
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)

echo Cleaning stale build artifacts...
taskkill /im "%OUTPUT_EXE%" /f >nul 2>nul
for %%D in ("bin" "obj" "tests\TutzApp.Tests\bin" "tests\TutzApp.Tests\obj" "publish-temp") do (
    if exist "%%~D" rmdir /s /q "%%~D"
)

echo Restoring test project...
dotnet restore "%TEST_PROJECT%"
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)

echo Running tests...
dotnet test "%TEST_PROJECT%" -c "%CONFIGURATION%" --no-restore
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)

echo Publishing %PROJECT%...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%OUTPUT_EXE%" (
    del /f /q "%OUTPUT_EXE%"
    if errorlevel 1 (
        set "ERR=!ERRORLEVEL!"
        goto :fail
    )
)

dotnet publish "%PROJECT%" ^
    -c "%CONFIGURATION%" ^
    -r "%RUNTIME%" ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=false ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o "%PUBLISH_DIR%"
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)

if not exist "%PUBLISH_DIR%\%OUTPUT_EXE%" (
    echo ERROR: Published executable was not found:
    echo   %PUBLISH_DIR%\%OUTPUT_EXE%
    set "ERR=1"
    goto :fail
)

copy /y "%PUBLISH_DIR%\%OUTPUT_EXE%" "%OUTPUT_EXE%" >nul
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)

rmdir /s /q "%PUBLISH_DIR%"
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)

echo Checking Zig toolchain...
set "ZIG_EXE="
if defined TUTZ_ZIG set "ZIG_EXE=%TUTZ_ZIG%"
if not defined ZIG_EXE (
    for /f "delims=" %%I in ('where zig.exe 2^>nul') do if not defined ZIG_EXE set "ZIG_EXE=%%I"
)
if not defined ZIG_EXE if exist "D:\CODING\SDKs\zig\zig.exe" set "ZIG_EXE=D:\CODING\SDKs\zig\zig.exe"
if not defined ZIG_EXE if exist "D:\CODING\SDKs\Zig\zig.exe" set "ZIG_EXE=D:\CODING\SDKs\Zig\zig.exe"
if not defined ZIG_EXE (
    echo ERROR: zig.exe was not found in PATH.
    echo Define it explicitly with:
    echo   set TUTZ_ZIG=D:\path\to\zig.exe
    set "ERR=1"
    goto :fail
)
if not exist "%ZIG_EXE%" (
    echo ERROR: Zig executable does not exist:
    echo   %ZIG_EXE%
    set "ERR=1"
    goto :fail
)
echo Zig executable:
echo   %ZIG_EXE%
"%ZIG_EXE%" version
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)
set "TUTZ_ZIG=%ZIG_EXE%"

echo Building Windows 11 modern context-menu integration with Zig...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build\ContextMenu\build-modern-context-menu.ps1" -Configuration "%CONFIGURATION%" -ZigPath "%ZIG_EXE%"
if errorlevel 1 (
    set "ERR=!ERRORLEVEL!"
    goto :fail
)

echo.
echo Build completed successfully.
echo Output: %ROOT%%OUTPUT_EXE%
echo Shell integration: %ROOT%ShellIntegration\
pause
exit /b 0

:fail
if not defined ERR set "ERR=1"
echo.
echo ============================================================
echo BUILD SCRIPT FAILED
echo Exit code: %ERR%
echo ============================================================
echo.
pause
exit /b %ERR%
