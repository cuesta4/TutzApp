# TutzApp — non-terminating context-menu deployment

This revision removes the forced GUI shutdown/relaunch cycle from modern context-menu installation, repair, and removal.

## Root cause

The previous implementation explicitly raised `ApplicationRestartRequested`, waited 1.2 seconds, and called `ShutdownApp()`. The deployment helper then waited for every `TutzApp.exe` process to disappear and attempted to relaunch the app. This was a controlled shutdown path, not a native crash.

## New deployment model

- `TutzApp.exe` remains unpackaged at runtime: the external-location package continues to own the Explorer extension, but the executable manifest no longer grants package identity to the tray process.
- Package registration/removal runs asynchronously while the GUI, tray icon, hooks, helper, and monitoring services remain active.
- Only the dedicated TutzApp `dllhost.exe` COM surrogate is stopped before package replacement.
- The helper no longer waits for the GUI, kills related TutzApp processes, or starts a replacement instance.
- The running GUI monitors the helper and reports success/failure when deployment completes.
- The warmup worker releases its COM proxy before deployment and activates the newly registered surrogate afterward, so keeping the GUI alive does not reintroduce the first-right-click cold start.
- Package version is `1.0.0.7`.

The modern menu implementation, classic two-command registration, File Pilot compatibility, and Execute In Explorer terminal launch path are otherwise unchanged.
