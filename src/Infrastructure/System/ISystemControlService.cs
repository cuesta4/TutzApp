using System;
using System.Collections.Generic;
using TutzApp.Models;

namespace TutzApp.Services
{
    public interface ISystemControlService
    {
        AppConfig Config { get; }
        AppStatus Status { get; }
        event Action? StatusChanged;
        event Action<string>? LogAdded;
        event Action<string, string>? NotificationRequested;

        void LogDebug(string msg);
        void NotifyStatusChanged();
        bool ChangeResolution(uint width, uint height, uint refreshRate);
        bool GetCurrentResolution(out uint width, out uint height, out uint refreshRate);
        void SetMouseSpeed(int speed, bool updateStatus = true);
        void SetDpiState(string state);
        void SetPollingState(string state);
        bool ApplyMouseSettings(int polling, int dpi, int speed);
        bool ApplyKeyboardSettings(int profile, int brightness);
        void ToggleKeyboardProfile(int? profile = null);
        void ApplyAutomationProfile(bool isGame);
        void PowerOffMonitor();
        bool ToggleDisplayTarget();
        void AdjustBrightness(int offset);
        bool ForceKillForegroundApp();
        bool ForceKillForegroundAppDirect(string? protectedProcessesText = null);
        void SetStatusMessage(string message);
        bool IsAutoStartEnabled();
        void SetAutoStart(bool enable);
        bool SaveAppConfig(AppConfig newConfig);
        void PreloadTouchKeyboard();
        void InvokeTouchKeyboard(bool manageControllerVkMapping = true);
        void SimulateKeyboardShortcut(string shortcutText);
        void RunExecutable(string exePath);
        void OpenTerminal(string? workingDirectory = null);
        void OpenTerminalAdmin(string? workingDirectory = null);
        bool AreTerminalContextMenuEntriesInstalled();
        void InstallTerminalContextMenuEntries();
        void UninstallTerminalContextMenuEntries();
        void EnsureTerminalContextMenuEntries();
        IReadOnlyList<TerminalContextMenuCommand> GetTerminalContextMenuCommands();
        bool SetTerminalContextMenuCommandEnabled(string commandId, bool enabled);
        bool RestoreTerminalContextMenuCommands();
        IReadOnlyList<ExplorerContextMenuEntry> GetExplorerContextMenuEntries();
        bool SetExplorerContextMenuEntryEnabled(ExplorerContextMenuEntry entry, bool enabled);
        void ShowKeyboardShortcutOverlay();
        void HideKeyboardShortcutOverlay();
        bool IsKeyboardShortcutOverlayVisible { get; }
        void ShowGamepadShortcutOverlay();
        void HideGamepadShortcutOverlay();
        bool IsGamepadShortcutOverlayVisible { get; }
        bool RunNativeHidDriverDirect(int polling, int dpi, string? devicePath = null, string? vidHexOverride = null, string? pidHexOverride = null, string? interfaceIdOverride = null);
        bool RunKeyboardHidDriverDirect(int profile, int brightness, string? devicePath = null, string? vidHexOverride = null, string? pidHexOverride = null, string? interfaceIdOverride = null);
        string ScanKeyboardReportIds();
        IReadOnlyList<HidDeviceInfo> EnumerateHidDevices();
        string? GetForegroundProcessName();
        string ApplyBorderlessNow(BorderlessAppRule rule);
        string EnsureBorderlessWindow(string exePath, string monitorDevice, int xOffset, int yOffset, int width, int height);
        LaunchFocusAssist? LaunchFocusAssist { get; }
        PlayniteBridge? PlayniteBridge { get; }
        bool SuppressControllerVkMapping(bool suppress);
        bool RestoreControllerVkMapping();
        int? GetControllerVkMappingState();
    }
}
