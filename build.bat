@echo off
setlocal

set "ROOT=%~dp0"
set "PROJECT=TutzApp.csproj"
set "TEST_PROJECT=TutzApp.Tests\TutzApp.Tests.csproj"
set "CONFIGURATION=Release"
set "RUNTIME=win-x64"
set "PUBLISH_DIR=publish-temp"
set "OUTPUT_EXE=TutzApp.exe"

cd /d "%ROOT%" || exit /b 1

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet SDK was not found in PATH.
    pause
    exit /b 1
)

echo Restoring test project...
dotnet restore "%TEST_PROJECT%"
if errorlevel 1 goto :fail

echo Running tests...
dotnet test "%TEST_PROJECT%" -c "%CONFIGURATION%" --no-restore
if errorlevel 1 goto :fail

echo Publishing %PROJECT%...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
taskkill /im "%OUTPUT_EXE%" /f >nul 2>nul
if exist "%OUTPUT_EXE%" (
    del /f /q "%OUTPUT_EXE%"
    if errorlevel 1 goto :fail
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
if errorlevel 1 goto :fail

if not exist "%PUBLISH_DIR%\%OUTPUT_EXE%" (
    echo ERROR: Published executable was not found:
    echo   %PUBLISH_DIR%\%OUTPUT_EXE%
    goto :fail
)

copy /y "%PUBLISH_DIR%\%OUTPUT_EXE%" "%OUTPUT_EXE%" >nul
if errorlevel 1 goto :fail

rmdir /s /q "%PUBLISH_DIR%"
if errorlevel 1 goto :fail

echo.
echo Build completed successfully.
echo Output: %ROOT%%OUTPUT_EXE%
pause
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
