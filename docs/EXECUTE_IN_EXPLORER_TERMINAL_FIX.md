# Execute In Explorer terminal fix

## Problem

Some classic-menu hosts, notably File Pilot, may execute a static Shell verb through an elevated broker. The classic **normal** verb correctly invoked:

```text
TutzApp.exe --open-terminal "%V"
```

but the short-lived TutzApp process inherited the broker's elevated token. A normal `Process.Start("wt.exe")` then propagated that token, so Windows Terminal opened as administrator. Explorer's legacy menu did not reproduce the issue because Explorer invoked the same verb at medium integrity.

## Implementation

The registry layout remains unchanged. The fix is entirely in the one-shot terminal handler:

1. Resolve the target working directory.
2. For the administrator command, keep `ProcessStartInfo.Verb = "runas"`.
3. For the normal command from a non-elevated TutzApp process, keep the ordinary `Process.Start` path.
4. For the normal command from an elevated TutzApp process, use Microsoft's **Execute In Explorer** pattern.

`ExplorerProcessLauncher` mirrors the Microsoft Windows classic sample:

- instantiate `ShellWindows`;
- locate the desktop with `IShellWindows.FindWindowSW`;
- query `SID_STopLevelBrowser`;
- obtain the active `IShellView`;
- request `SVGIO_BACKGROUND` as `IShellFolderViewDual`;
- obtain `IShellDispatch2` from the view's `Application` property;
- call `IShellDispatch2.ShellExecute("wt.exe", ...)`.

The actual process creation therefore occurs in the desktop Explorer process and inherits Explorer's normal user token. No PowerShell launcher, helper executable, token duplication, or registry change is introduced.

The COM path runs on an STA thread. Calls made from a non-STA thread are marshalled to a short-lived dedicated STA worker with a ten-second timeout.
