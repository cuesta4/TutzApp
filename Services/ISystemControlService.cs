// Services/ISystemControlService.cs
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
        void AdjustBrightness(int offset);
        void SetStatusMessage(string message);
        bool IsAutoStartEnabled();
        void SetAutoStart(bool enable);
        bool SaveAppConfig(AppConfig newConfig);
        void PreloadTouchKeyboard();
        void InvokeTouchKeyboard();
        void SimulateKeyboardShortcut(string shortcutText);
        void RunExecutable(string exePath);
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
    }
}
