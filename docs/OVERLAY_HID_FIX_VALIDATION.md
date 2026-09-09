# Validation report — single-screen overlays and HID hold

- Checks passed: **19/19**
- C# files parsed: **32**
- XML/XAML/project files parsed: **10**

## Checks

- [x] XML/XAML/project files parse
- [x] C# files parse with tree-sitter
- [x] Keyboard overlay has no ScrollViewer
- [x] Gamepad overlay has no ScrollViewer
- [x] Keyboard overlay uses fixed 4x4 grid
- [x] Both overlays scale down as a whole
- [x] Gamepad overlay has adaptive columns
- [x] Overlay cards do not use ellipsis trimming
- [x] Hold reads interpreted HID state continuously
- [x] Hold threshold is 3000 ms
- [x] Hold checks semantic START bit
- [x] Legacy event timer removed
- [x] START OPTIONS aliases supported by parser
- [x] Overlay request is forwarded to GUI process
- [x] B close is forwarded to GUI process
- [x] Force-kill-only monitor cannot toggle overlay
- [x] All 16 keyboard shortcuts preserved
- [x] Regression tests reject scrolling
- [x] Regression tests cover pipe routing

## Environment limitation

The container does not include the Windows .NET/WPF SDK, so `dotnet test` and the rendered WPF windows could not be executed here. The project was validated with C# and XML parsers plus structural regression checks.
