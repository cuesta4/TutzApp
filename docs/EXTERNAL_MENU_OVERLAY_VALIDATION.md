# Validation report — external context-menu manager and shortcut overlays

## Static validation completed

- 32 C# files parsed with Tree-sitter C#: 0 syntax errors.
- 10 XAML/XML/project files parsed as XML: 0 errors.
- Embedded PowerShell used to enumerate AppX manifests parsed with Tree-sitter PowerShell: 0 syntax errors.
- All pre-existing MainWindow binding heads remain present; 9 new binding heads were added.
- All 8 pre-existing MainWindow event handlers remain present.
- All 16 keyboard shortcuts from the previous F1 overlay remain present.
- No unsupported `CharacterSpacing` property remains in WPF XAML.
- The problematic global ComboBox override was not reintroduced.
- `ISystemControlService`, `SystemControlService`, and the test mock expose the same two external-menu methods.

## Functional design checks

- Scans generic file, folder, background, drive, library and desktop menu roots in HKCU and HKLM.
- Detects classic `shell` verbs and `shellex\\ContextMenuHandlers`.
- Enumerates packaged `windows.fileExplorerContextMenus` entries through `Get-AppxPackageManifest`.
- Excludes the TutzApp package from the external-app list.
- Classic verbs are disabled with `LegacyDisable`; machine registrations request UAC.
- COM/MSIX handlers are disabled per-user through `Shell Extensions\\Blocked` by CLSID.
- Restore is only enabled for changes recorded by TutzApp, preventing the app from undoing a pre-existing external block.
- Bookkeeping is written before the registry mutation and rolled back if the mutation fails.

## Environment limitation

The complete .NET/WPF build was not executed in this Linux container because the Windows Desktop SDK and WPF build targets are unavailable. The project should still be validated with the existing `build.bat` on Windows before replacing the working executable.
