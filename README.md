# TutzApp

TutzApp is a Windows desktop utility for people who switch between games, gamepads, keyboard, mouse, and TV. It brings hardware monitoring, global shortcuts, game automation, display control, and Explorer integration into one tray application.

## Features

- **Gamepad:** XInput, Raw HID, and Windows Gaming Input, with calibration persisted per device.
- **Mouse mode:** double-press L3 to toggle; the left stick moves the cursor; A/Y click; the right stick scrolls; L2/R2 go back/forward; R3 opens `Win+V`; L1/R1 act as `Ctrl`/`Shift` while clicking.
- **Mouse curve:** granular center response and cubic easing after 500 ms above 95% stick travel, reaching 1.5x speed.
- **Controller shortcuts:** up to four buttons, triggered by press, double press, or hold; actions can be system commands, executables, or keyboard combinations.
- **Xbox/Windows:** Game Bar, Task View, Game Bar navigation, and an option to suppress `ControllerToVKMapping` to prevent double input between Windows menus and the mouse.
- **HID and global shortcuts:** keyboard profiles/brightness, DPI, polling rate, mouse speed, SuperF4, virtual keyboard, Terminal, QMK, monitor, text editing, and virtual desktops.
- **Automatic profiles:** when monitored games are detected, switches DPI, polling rate, mouse speed, and keyboard profile/brightness; for CS2, it can apply `1920x1080@144Hz` and restore the previous mode afterward.
- **Display:** per-application Digital Vibrance through NVIDIA NVAPI, RTSS OSD, and monitor/TV switching.
- **NoMoreBorder:** removes borders and positions configured applications on a selected monitor with custom position and size.
- **Explorer:** modern MSIX/`IExplorerCommand` menu, classic Explorer/File Pilot compatibility, and normal/elevated Terminal commands, with install, repair, and removal without shutting down the main application.
- **Tools:** [`tools/analog_curve_lab.py`](tools/analog_curve_lab.py) tests real mouse curves; `tools/run_analog_curve_lab.bat` launches it on Windows.

## Usage and build

Run as administrator when required. `F1` opens global help, holding `START` for three seconds opens gamepad help, and double-pressing L3 toggles mouse mode. `appsettings.json` is local and not versioned.

Requirements: Windows 10 build 19041+, .NET 10 SDK, PowerShell, Zig, and Windows SDK/EWDK with `MakeAppx`. NVIDIA NVAPI, RTSS, and Twinkle Tray are optional. The full build validates scripts/MSIX, runs tests, publishes the executable, and compiles the Explorer extension:

```bat
build.bat
```

During development, use `build-fast.ps1` with `TUTZ_EWDK_ENV` pointing to the local EWDK environment. To run tests:

```powershell
dotnet test tests\TutzApp.Tests\TutzApp.Tests.csproj -c Release
```

Architecture, technical decisions, and validation notes are in [`docs/`](docs/).
