# Modern context-menu activation pass — package 1.0.0.6

## Observed behavior

On the first Windows 11 context-menu request, Explorer could show `Loading...`, remove the command, and only display it on a later request. The managed crash log did not contain a corresponding new CLR exception, so this pass treats the failure as a packaged COM activation/surrogate problem and adds native evidence rather than attributing it to the WPF process.

## Package and COM registration

- Package architecture is `x64`, matching `TutzExplorerCommand.dll` and the win-x64 app.
- Package version is `1.0.0.6`.
- The COM class/AppId uses a new GUID so an existing surrogate cannot retain the previous DLL registration.
- The server follows the documented `SurrogateServer` + `ThreadingModel="STA"` model.
- Install/Repair stops only `dllhost.exe` instances whose command line contains the old or current TutzApp AppId before replacing the package.

## Cold-start removal

`ModernContextMenuWarmup` activates the packaged `IExplorerCommand` from a dedicated STA thread shortly after normal app startup. It keeps the COM proxy alive until application shutdown; releasing it immediately would allow the surrogate and DLL to unload and would merely defer the cold activation to the first right-click.

## Native implementation

- COM reference counts use `InterlockedIncrement`/`InterlockedDecrement`.
- The enumerator remains STA and contains no hand-written agility declarations.
- The parent icon is restored by deriving `<app-root>\TutzApp.exe,0` from the loaded DLL path, without existence checks during menu construction.
- Key native calls are recorded in:

```text
%LOCALAPPDATA%\TutzApp\ShellIntegration\modern-context-menu-native.log
```

The trace distinguishes activation, class-factory creation, submenu enumeration, child enumeration, icon lookup and invocation.

## Apparent app termination

The managed app now polls the actual Explorer shell PID. If Explorer is replaced, it retries notification-icon registration three times. This covers the case where an Explorer restart makes the tray app appear to have terminated even though no managed shutdown or CLR crash was recorded.
