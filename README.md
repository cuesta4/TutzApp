# TutzApp

TutzApp is a Windows desktop application built with WPF and .NET 10.

## Requirements

- Windows
- .NET SDK 10

## Build

Run `build.bat` from the project root.

The script restores dependencies, runs the test suite, and publishes a self-contained `win-x64` build to the `publish` folder.

## Tests

```bat
dotnet test TutzApp.Tests\TutzApp.Tests.csproj -c Release
```
