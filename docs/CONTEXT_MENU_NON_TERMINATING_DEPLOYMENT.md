# Context-menu deployment without terminating TutzApp

The 2026-07-14 runtime log proves that installation did not crash the CLR. The app itself handled `ApplicationRestartRequested`, then called `ShutdownApp()` 1.2 seconds later. The PowerShell helper was designed to wait for the GUI and all related processes before replacing the sparse package, and then launch another instance.

This revision removes that architecture.

## Separation of responsibilities

The sparse package remains responsible for:

- the Windows 11 `desktop4:FileExplorerContextMenus` declarations;
- the surrogate-hosted `IExplorerCommand` COM class;
- package assets and registration.

The continuously running tray executable is no longer granted package identity by its Win32 application manifest. It can therefore stay alive while the package registration is changed.

## Invariants

- No package operation invokes `ShutdownApp()`.
- No `ApplicationRestartRequested` event exists.
- No deployment helper waits for the parent PID or all `TutzApp.exe` processes.
- No deployment helper relaunches `TutzApp.exe`.
- Only matching TutzApp COM-surrogate processes may be stopped.
- The operation gate remains set until the helper exits.
- Completion is reported back to the existing GUI process.
- The COM warmup proxy is suspended before package replacement and freshly activated after a successful install/repair.
