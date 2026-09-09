# Classic option 1 implementation

The Python compatibility probe established the following matrix:

| Layout | Explorer classic | File Pilot | Execution |
|---|---:|---:|---:|
| Two independent static verbs | Yes | Yes | Yes |
| Static cascade variants | Partial/varied | No | Not reliable |

The app therefore uses the independent-verb layout for the classic registry surface and keeps the existing nested `IExplorerCommand` only for the modern Windows 11 menu.

The active classic target is intentionally restricted to `Directory\Background\shell` with `%V`, exactly matching the verified probe. Other historical class paths are retained only in cleanup arrays so reinstalling removes obsolete TutzApp registrations without creating new ones there.

Each classic leaf invokes `TutzApp.exe` directly with either `--open-terminal` or `--open-terminal-admin`. These arguments are handled as one-shot commands at the beginning of `App.OnStartup`, before `base.OnStartup`, the single-instance mutex, tray initialization, hooks, the admin helper, or the named pipe. If the caller launched the one-shot TutzApp process elevated (as File Pilot may do), the normal command uses Microsoft's **Execute In Explorer** pattern: it obtains the desktop Explorer shell automation object and asks Explorer to execute `wt.exe`, so the terminal inherits Explorer's normal token. The administrator command continues to use the explicit `runas` verb.

No standalone launcher script is created. Install/Repair removes the obsolete `Invoke-ClassicTerminal.ps1` left by the preceding experimental build.
