> **Historical note:** This document describes a superseded implementation. The active design is documented in `CONTEXT_MENU.md` and `CLASSIC_OPTION1_IMPLEMENTATION.md`.

# Classic context-menu registry fix

This pass removes the previous per-user `Folder\shell` and `Drive\shell`
registrations and replaces the classic menu with a machine-wide static cascade.

## Root cause

`HKEY_CLASSES_ROOT` is a merged view of `HKCU\Software\Classes` and
`HKLM\Software\Classes`. `Folder` and `Drive` have special merge semantics:
a duplicate immediate subkey under HKCU can hide the corresponding HKLM
subkey. The previous implementation created `HKCU\...\Folder\shell` and
`Drive\shell`; that could hide Windows' built-in machine verbs and break
folder activation.

## New classic registration

Each supported class is registered in both machine registry views at:

```text
HKLM\Software\Classes\<class>\shell\TutzApp.Terminal
  MUIVerb = Abrir no Terminal
  Icon = <TutzApp.exe>,0
  Position = Top
  ExtendedSubCommandsKey\Shell\01.Normal
    MUIVerb = Normal
    command\(Default) = "<TutzApp.exe>" --open-terminal <token>
  ExtendedSubCommandsKey\Shell\02.Elevated
    MUIVerb = Elevado
    HasLUAShield
    command\(Default) = "<TutzApp.exe>" --open-terminal-admin <token>
```

The parent has no default value, direct `command`, `DelegateExecute`, or
`SubCommands` value. The child verbs are embedded directly as documented for
`ExtendedSubCommandsKey`, so hosts do not need to resolve private CommandStore
names.

## Migration

Before registration, the app removes only TutzApp-owned per-user verbs,
TutzApp's old CommandStore entries, and stale default values created by prior
builds. Empty `Folder`, `Drive`, `Directory`, and `LibraryFolder` paths created
by TutzApp are removed bottom-up so the HKLM view becomes visible again.

`Repair-TutzApp-Classic-Menu.ps1` performs the same cleanup without touching
the modern MSIX menu. It is intended for machines already affected by the
broken folder activation.
