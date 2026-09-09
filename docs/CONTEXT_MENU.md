# Terminal context-menu integration

## Modern Windows 11 menu

The sparse MSIX package registers the native `IExplorerCommand` implementation. It remains a single **Abrir no Terminal** parent with two children:

- Normal
- Elevado

The native implementation and package manifest are unchanged by the classic-menu compatibility pass.

## Classic menu and File Pilot

The classic registry integration uses two top-level static verbs because this was the only shape verified to be both visible and executable in Explorer and File Pilot:

```text
HKLM\Software\Classes\Directory\Background\shell\TutzApp.OpenTerminal
    MUIVerb = Abrir no Terminal
    Icon = <TutzApp.exe>,0
    command\(Default) = "<TutzApp.exe>" --open-terminal "%V"

HKLM\Software\Classes\Directory\Background\shell\TutzApp.OpenTerminalAdmin
    MUIVerb = Abrir no Terminal como administrador
    Icon = <TutzApp.exe>,0
    HasLUAShield = ""
    command\(Default) = "<TutzApp.exe>" --open-terminal-admin "%V"
```

No `SubCommands`, `ExtendedSubCommandsKey`, or new `CommandStore` registration is used for the classic menu. Each leaf starts `TutzApp.exe` in a dedicated one-shot CLI mode. Terminal arguments are processed before the normal WPF startup path and therefore do not use the single-instance mutex or named pipe.

## Safety invariants

The installer must never modify the default value of:

```text
Directory\shell
Directory\Background\shell
Folder\shell
Drive\shell
LibraryFolder\Background\shell
```

It creates only TutzApp-owned leaf subkeys under `Directory\Background\shell`. Previous TutzApp keys in other locations are cleanup targets only.

The installer also never sets `Position=Top`, preventing a TutzApp verb from being promoted during default-verb resolution.

## Command visibility settings

The `ShowNormal` and `ShowElevated` values under `HKCU\Software\TutzApp\ShellIntegration` control both surfaces:

- modern menu: controls which native child commands are enumerated;
- classic menu: controls whether each independent leaf verb exists.
