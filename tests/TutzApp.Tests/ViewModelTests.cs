// ViewModelTests.cs
using System;
using System.Linq;
using System.Threading;
using Xunit;
using TutzApp.Models;
using TutzApp.Services;
using TutzApp.ViewModels;

namespace TutzApp.Tests
{
    public class ViewModelTests
    {
        [Fact]
        public void TestViewModel_ShouldInitializeWithCorrectDefaultStatus()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();

            // Act
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Assert
            Assert.Equal("Desconhecida", vm.CurrentResolution); // Mock default resolution
            Assert.Equal("Desconectado", vm.GamepadState);
            Assert.Equal(3, vm.MouseSpeed);
            Assert.Equal("low", vm.DpiState);
            Assert.Equal("low", vm.PollingState);
            Assert.Equal(1, vm.KeyboardProfile);
            Assert.Equal("Nenhum", vm.ActiveGame);
            Assert.Equal("Pronto", vm.StatusMessage);
        }

        [Fact]
        public void ContextMenuCommands_ShouldBeListedAndIndividuallyRemovable()
        {
            var mockSys = new MockSystemControlService();
            var vm = new MainViewModel(
                mockSys,
                new MockProcessMonitorService(),
                new MockKeyboardHookService());

            TerminalContextMenuCommand normal = Assert.Single(
                vm.TerminalContextMenuCommands,
                command => command.Id == "normal");
            Assert.True(normal.IsEnabled);

            vm.ToggleTerminalContextMenuCommandCommand.Execute(normal);

            Assert.False(mockSys.TerminalNormalEnabled);
            TerminalContextMenuCommand refreshedNormal = Assert.Single(
                vm.TerminalContextMenuCommands,
                command => command.Id == "normal");
            Assert.False(refreshedNormal.IsEnabled);
            Assert.Equal("Removido", refreshedNormal.StatusText);
        }

        [Fact]
        public async System.Threading.Tasks.Task ExternalContextMenuEntries_ShouldBeScannedAndIndividuallyRemovable()
        {
            var mockSys = new MockSystemControlService();
            var vm = new MainViewModel(
                mockSys,
                new MockProcessMonitorService(),
                new MockKeyboardHookService());

            await vm.RefreshExplorerContextMenuEntriesCommand.ExecuteAsync(null);

            ExplorerContextMenuEntry entry = Assert.Single(vm.ExplorerContextMenuEntries);
            Assert.True(entry.IsEnabled);
            Assert.Equal("Aplicativo de teste", entry.ApplicationName);

            await vm.ToggleExplorerContextMenuEntryCommand.ExecuteAsync(entry);

            Assert.False(mockSys.ExternalContextMenuEntryEnabled);
            ExplorerContextMenuEntry refreshed = Assert.Single(vm.ExplorerContextMenuEntries);
            Assert.False(refreshed.IsEnabled);
            Assert.Equal("Removido pelo TutzApp", refreshed.StatusText);
        }

        [Fact]
        public void ExternalContextMenuEntries_Search_ShouldMatchAcrossEntryMetadata()
        {
            var vm = new MainViewModel(
                new MockSystemControlService(),
                new MockProcessMonitorService(),
                new MockKeyboardHookService());

            vm.ExplorerContextMenuEntries.Add(new ExplorerContextMenuEntry(
                "terminal",
                "Abrir no Terminal",
                "Windows Terminal",
                "Comando moderno do terminal",
                "MSIX",
                "wt.exe",
                true,
                false,
                true,
                false,
                "Package"));
            vm.ExplorerContextMenuEntries.Add(new ExplorerContextMenuEntry(
                "teracopy",
                "Copiar com TeraCopy",
                "TeraCopy",
                "Handler clássico de cópia",
                "Registry verb",
                @"HKCU\Software\Classes",
                false,
                true,
                true,
                false,
                "Registry"));

            vm.ExplorerContextMenuSearchText = "teracopy removido";

            ExplorerContextMenuEntry result = Assert.Single(
                vm.ExplorerContextMenuEntriesView.Cast<ExplorerContextMenuEntry>());
            Assert.Equal("TeraCopy", result.ApplicationName);
        }

        [Fact]
        public void ContextMenuCommands_RestoreAll_ShouldEnableBothCommands()
        {
            var mockSys = new MockSystemControlService
            {
                TerminalNormalEnabled = false,
                TerminalElevatedEnabled = false
            };
            var vm = new MainViewModel(
                mockSys,
                new MockProcessMonitorService(),
                new MockKeyboardHookService());

            vm.RestoreTerminalContextMenuCommandsCommand.Execute(null);

            Assert.All(vm.TerminalContextMenuCommands, command => Assert.True(command.IsEnabled));
            Assert.True(mockSys.TerminalNormalEnabled);
            Assert.True(mockSys.TerminalElevatedEnabled);
        }

        [Fact]
        public void TestViewModel_ToggleKeyboardProfileCommand_ShouldCallService()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Act
            vm.ToggleKeyboardProfileCommand.Execute(null);

            // Assert
            Assert.Equal(2, vm.KeyboardProfile);
            Assert.Equal(2, mockSys.Status.KeyboardProfile);
        }

        [Fact]
        public void TestViewModel_ChangeResolutionCommand_ShouldCallServiceAndChangeStatus()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();

            // Adicionar resoluções simuladas para o dicionário
            mockSys.Config.Resolutions.Add("1", new ResolutionInfo { Width = 1920, Height = 1080, RefreshRate = 144 });
            
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Act
            vm.ChangeResolutionCommand.Execute("1");

            // Assert
            Assert.Equal(1920u, mockSys.TargetWidth);
            Assert.Equal(1080u, mockSys.TargetHeight);
            Assert.Equal(144u, mockSys.TargetRefreshRate);
            Assert.Equal("1920x1080@144Hz", vm.CurrentResolution);
        }

        [Fact]
        public void TestViewModel_MouseSpeedCommands_ShouldChangeSpeed()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            mockSys.Config.Mouse.HighSpeed = 14;
            mockSys.Config.Mouse.SlowSpeed = 3;

            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Act - Set Fast
            vm.SetMouseSpeedFastCommand.Execute(null);
            Assert.Equal(14, vm.MouseSpeed);

            // Act - Set Slow
            vm.SetMouseSpeedSlowCommand.Execute(null);
            Assert.Equal(3, vm.MouseSpeed);
        }

        [Fact]
        public void TestViewModel_LogAddedEvent_ShouldUpdateLogsCollection()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Act
            mockSys.LogDebug("Teste log linha 1");

            // Aguarda atualização assíncrona do Dispatcher
            Thread.Sleep(50); 

            // Assert
            Assert.NotEmpty(vm.Logs);
            Assert.Contains("Teste log linha 1", vm.Logs[0]);
        }

        [Fact]
        public void TestViewModel_ToggleAutoStart_ShouldUpdateService()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Act
            vm.IsAutoStartEnabled = true;

            // Assert
            Assert.True(mockSys.AutoStartSetting);
            Assert.True(vm.IsAutoStartEnabled);
        }


        [Fact]
        public void TestHID_BuildPollingPayload_ShouldGenerateCorrectBytes()
        {
            // Act
            byte[] payload = SystemControlService.BuildPollingPayload(1000);

            // Assert
            Assert.Equal(17, payload.Length);
            Assert.Equal(0x08, payload[0]);
            Assert.Equal(0x07, payload[1]);
            Assert.Equal(0x02, payload[5]);
            Assert.Equal(0x01, payload[6]);
            Assert.Equal(0x54, payload[7]);
            Assert.Equal(0xef, payload[16]); // checksum
        }

        [Fact]
        public void TestHID_BuildDpiPayload_ShouldGenerateCorrectBytes()
        {
            // Act
            byte[] payload = SystemControlService.BuildDpiPayload(8000);

            // Assert
            Assert.Equal(17, payload.Length);
            Assert.Equal(0x08, payload[0]);
            Assert.Equal(0x07, payload[1]);
            Assert.Equal(0x0c, payload[4]);
            Assert.Equal(0x04, payload[5]);
            Assert.Equal(0x9f, payload[6]);
            Assert.Equal(0x9f, payload[7]);
            Assert.Equal(0x17, payload[9]);
            Assert.Equal(0xe1, payload[16]); // checksum
        }

        [Fact]
        public void TestViewModel_SaveKeyboardSettingsCommand_ShouldCallService()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            vm.KeyboardVidHex = "3151";
            vm.KeyboardPidHex = "4015";
            vm.KeyboardInterfaceId = "mi_02";
            vm.SelectedKeyboardBrightness = 3;

            // Act
            vm.SaveKeyboardSettingsCommand.Execute(null);

            // Assert
            Assert.True(mockSys.SaveAppConfigCalled);
            Assert.Equal("3151", mockSys.Config.Keyboard.VidHex);
            Assert.Equal("4015", mockSys.Config.Keyboard.PidHex);
            Assert.Equal("mi_02", mockSys.Config.Keyboard.InterfaceId);
            Assert.Equal(3, mockSys.Config.Keyboard.ManualBrightness);
        }

        [Fact]
        public void TestViewModel_SaveMouseSettingsCommand_ShouldCallService()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            vm.MouseVidHex = "ABCD";
            vm.MousePidHex = "1234";
            vm.MouseInterfaceId = "mi_99";

            // Act
            vm.SaveMouseSettingsCommand.Execute(null);

            // Assert
            Assert.True(mockSys.SaveAppConfigCalled);
            Assert.Equal("ABCD", mockSys.Config.Mouse.VidHex);
            Assert.Equal("1234", mockSys.Config.Mouse.PidHex);
            Assert.Equal("mi_99", mockSys.Config.Mouse.InterfaceId);
        }

        [Fact]
        public void TestViewModel_SaveAutomationSettingsCommand_ShouldCallService()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            vm.AutoGameProfileSwitch = false;
            vm.AutoResolutionSwitch = true;
            vm.MonitoredGamesText = "game1.exe, game2.exe";

            // Act
            vm.SaveAutomationSettingsCommand.Execute(null);

            // Assert
            Assert.True(mockSys.SaveAppConfigCalled);
            Assert.False(mockSys.Config.Monitoring.AutoGameProfileSwitch);
            Assert.True(mockSys.Config.Monitoring.AutoResolutionSwitch);
            Assert.Contains("game1.exe", mockSys.Config.Monitoring.Games);
            Assert.Contains("game2.exe", mockSys.Config.Monitoring.Games);
        }

        [Fact]
        public void TestViewModel_ClearLogsCommand_ShouldClearLogs()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);
            vm.Logs.Add("log1");
            vm.Logs.Add("log2");

            // Act
            vm.ClearLogsCommand.Execute(null);

            // Assert
            Assert.Single(vm.Logs);
            Assert.Contains("Logs limpos com sucesso.", vm.Logs[0]);
        }

        [Fact]
        public void TestViewModel_AddGamepadShortcutCommand_ShouldAddAndSave()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            vm.NewShortcutName = "Test Shortcut";
            vm.NewShortcutButton1 = "BACK";
            vm.NewShortcutButton2 = "X";
            vm.NewShortcutButton3 = "Nenhum";
            vm.NewShortcutButton4 = "Nenhum";
            vm.NewShortcutTriggerMode = "DoublePress";
            vm.NewShortcutActionType = "System";
            vm.NewShortcutActionValue = "VirtualKeyboard";

            // Act
            vm.AddGamepadShortcutCommand.Execute(null);

            // Assert
            Assert.True(mockSys.SaveAppConfigCalled);
            var added = vm.GamepadShortcuts.FirstOrDefault(x => x.Name == "Test Shortcut");
            Assert.NotNull(added);
            Assert.Equal("DoublePress", added.TriggerMode);
            Assert.Equal("System", added.ActionType);
            Assert.Equal("VirtualKeyboard", added.ActionValue);
            Assert.Contains("BACK", added.Buttons);
            Assert.Contains("X", added.Buttons);
        }

        [Fact]
        public void TestViewModel_DeleteGamepadShortcutCommand_ShouldRemoveAndSave()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            var shortcut = new GamepadShortcut
            {
                Name = "To Delete",
                Buttons = new() { "BACK" },
                TriggerMode = "Press",
                ActionType = "System",
                ActionValue = "VirtualKeyboard"
            };
            vm.GamepadShortcuts.Add(shortcut);
            vm.Config.GamepadShortcuts.Add(shortcut);
            mockSys.SaveAppConfigCalled = false; // Reset mock flag

            // Act
            vm.DeleteGamepadShortcutCommand.Execute(shortcut);

            // Assert
            Assert.True(mockSys.SaveAppConfigCalled);
            Assert.DoesNotContain(shortcut, vm.GamepadShortcuts);
            Assert.DoesNotContain(shortcut, vm.Config.GamepadShortcuts);
        }

        [Fact]
        public void TestViewModel_SaveAutomationSettings_ShouldUpdateMouseConfig()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Act
            vm.AutoGameDpi = 3200;
            vm.AutoGamePolling = 500;
            vm.AutoGameSpeed = 6;
            vm.AutoNormalDpi = 1600;
            vm.AutoNormalPolling = 250;
            vm.AutoNormalSpeed = 12;

            vm.SaveAutomationSettingsCommand.Execute(null);

            // Assert
            Assert.True(mockSys.SaveAppConfigCalled);
            Assert.Equal(3200, mockSys.Config.Mouse.DpiHigh);
            Assert.Equal(500, mockSys.Config.Mouse.PollingHigh);
            Assert.Equal(6, mockSys.Config.Mouse.SlowSpeed);
            Assert.Equal(1600, mockSys.Config.Mouse.DpiLow);
            Assert.Equal(250, mockSys.Config.Mouse.PollingLow);
            Assert.Equal(12, mockSys.Config.Mouse.HighSpeed);
        }

        [Fact]
        public void TestViewModel_ApplyManualMouseSettings_ShouldCallService()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Act
            vm.SelectedManualDpi = 1600;
            vm.SelectedManualPolling = 500;
            vm.SelectedManualSpeed = 8;

            vm.ApplyManualMouseSettingsCommand.Execute(null);

            // Assert
            Assert.Equal(8, mockSys.Status.MouseSpeed);
            Assert.Equal("low", mockSys.Status.DpiState); // 1600 is not DpiHigh (8000), so it is low
            Assert.Equal("low", mockSys.Status.PollingState); // 500 is not PollingHigh (1000), so it is low
        }

        [Fact]
        public void TestViewModel_SaveAutomationSettings_ShouldUpdateForceKillProtectedProcesses()
        {
            // Arrange
            var mockSys = new MockSystemControlService();
            var mockProc = new MockProcessMonitorService();
            var mockHook = new MockKeyboardHookService();
            var vm = new MainViewModel(mockSys, mockProc, mockHook);

            // Act
            vm.ForceKillProtectedProcessesText = "explorer.exe, steam.exe\r\ncs2.exe";
            vm.SaveAutomationSettingsCommand.Execute(null);

            // Assert
            Assert.True(mockSys.SaveAppConfigCalled);
            Assert.Contains("explorer.exe", mockSys.Config.ForceKill.ProtectedProcesses);
            Assert.Contains("steam.exe", mockSys.Config.ForceKill.ProtectedProcesses);
            Assert.Contains("cs2.exe", mockSys.Config.ForceKill.ProtectedProcesses);
        }

        [Fact]
        public void TestAppConfig_InitializeDefaults_ShouldMigrateLegacySuperF4Shortcut()
        {
            // Arrange
            var config = new AppConfig();
            config.GamepadShortcuts.Add(new GamepadShortcut
            {
                Name = "Fechar Janela (SuperF4)",
                Description = "Fecha a janela ativa com Ctrl+Alt+F4",
                Buttons = new() { "BACK", "Y" },
                TriggerMode = "Press",
                ActionType = "SendKeys",
                ActionValue = "Ctrl+Alt+F4"
            });

            // Act
            config.InitializeDefaults();

            // Assert
            var shortcut = Assert.Single(config.GamepadShortcuts, s => s.Buttons.Contains("BACK") && s.Buttons.Contains("Y"));
            Assert.Equal("System", shortcut.ActionType);
            Assert.Equal("ForceKillForegroundApp", shortcut.ActionValue);
            Assert.Contains("explorer.exe", config.ForceKill.ProtectedProcesses);
        }

        [Fact]
        public void TestAppConfig_InitializeDefaults_ShouldDeduplicateProtectedExplorer()
        {
            // Arrange
            var config = new AppConfig();
            config.ForceKill.ProtectedProcesses = new()
            {
                "explorer.exe",
                " EXPLORER.exe ",
                @"C:\Windows\explorer.exe",
                "steam.exe"
            };

            // Act
            config.InitializeDefaults();

            // Assert
            Assert.Equal(1, config.ForceKill.ProtectedProcesses.Count(p => p.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("steam.exe", config.ForceKill.ProtectedProcesses);
            Assert.Contains("TutzApp.exe", config.ForceKill.ProtectedProcesses);
        }
    }

    #region Manual Mocks for Testing
    public class MockSystemControlService : ISystemControlService
    {
        public AppConfig Config { get; } = new AppConfig();
        public AppStatus Status { get; } = new AppStatus();
        public event Action? StatusChanged;
        public event Action<string>? LogAdded;

        public void NotifyStatusChanged()
        {
            StatusChanged?.Invoke();
        }

        public bool ChangeResolutionResult = true;
        public uint TargetWidth = 0;
        public uint TargetHeight = 0;
        public uint TargetRefreshRate = 0;

        public bool ChangeResolution(uint width, uint height, uint refreshRate)
        {
            TargetWidth = width;
            TargetHeight = height;
            TargetRefreshRate = refreshRate;
            Status.CurrentResolution = $"{width}x{height}@{refreshRate}Hz";
            StatusChanged?.Invoke();
            return ChangeResolutionResult;
        }

        public bool GetCurrentResolution(out uint width, out uint height, out uint refreshRate)
        {
            width = 2560;
            height = 1440;
            refreshRate = 144;
            return true;
        }

        public int LastMouseSpeedSet = 0;
        public void SetMouseSpeed(int speed, bool updateStatus = true)
        {
            LastMouseSpeedSet = speed;
            Status.MouseSpeed = speed;
            if (updateStatus) StatusChanged?.Invoke();
        }

        public string LastDpiStateSet = "";
        public void SetDpiState(string state)
        {
            LastDpiStateSet = state;
            Status.DpiState = state;
            StatusChanged?.Invoke();
        }

        public string LastPollingStateSet = "";
        public void SetPollingState(string state)
        {
            LastPollingStateSet = state;
            Status.PollingState = state;
            StatusChanged?.Invoke();
        }

        public bool ApplyMouseSettings(int polling, int dpi, int speed)
        {
            Status.MouseSpeed = speed;
            Status.DpiState = (dpi == Config.Mouse.DpiHigh) ? "high" : "low";
            Status.PollingState = (polling == Config.Mouse.PollingHigh) ? "high" : "low";
            StatusChanged?.Invoke();
            return true;
        }

        public bool ApplyKeyboardSettings(int profile, int brightness)
        {
            Status.KeyboardProfile = profile;
            Config.Keyboard.ManualBrightness = brightness;
            StatusChanged?.Invoke();
            return true;
        }

        public bool RunNativeHidDriverDirect(int polling, int dpi, string? devicePath = null, string? vidHexOverride = null, string? pidHexOverride = null, string? interfaceIdOverride = null)
        {
            return true;
        }

        public bool RunKeyboardHidDriverDirect(int profile, int brightness, string? devicePath = null, string? vidHexOverride = null, string? pidHexOverride = null, string? interfaceIdOverride = null)
        {
            return true;
        }

        public string ScanKeyboardReportIds()
        {
            return "MOCK_SCAN";
        }

        public int? LastKeyboardProfileToggle = null;
        public void ToggleKeyboardProfile(int? profile = null)
        {
            LastKeyboardProfileToggle = profile;
            Status.KeyboardProfile = profile ?? (Status.KeyboardProfile == 1 ? 2 : 1);
            StatusChanged?.Invoke();
        }

        public bool PowerOffMonitorCalled = false;
        public void PowerOffMonitor()
        {
            PowerOffMonitorCalled = true;
        }

        public bool ToggleDisplayTarget()
        {
            return true;
        }

        public int LastBrightnessAdjustment = 0;
        public void AdjustBrightness(int offset)
        {
            LastBrightnessAdjustment = offset;
        }

        public bool ForceKillForegroundAppCalled = false;
        public bool ForceKillForegroundAppDirectCalled = false;
        public bool ForceKillForegroundApp()
        {
            ForceKillForegroundAppCalled = true;
            return true;
        }

        public bool ForceKillForegroundAppDirect(string? protectedProcessesText = null)
        {
            ForceKillForegroundAppDirectCalled = true;
            return true;
        }

        public void LogDebug(string msg)
        {
            LogAdded?.Invoke(msg);
        }

        public void SetStatusMessage(string message)
        {
            Status.StatusMessage = message;
            StatusChanged?.Invoke();
        }

        public bool AutoStartSetting = false;
        public bool IsAutoStartEnabled()
        {
            return AutoStartSetting;
        }

        public void SetAutoStart(bool enable)
        {
            AutoStartSetting = enable;
        }

        public bool SaveAppConfigCalled = false;
        public bool SaveAppConfigResult = true;
        public bool SaveAppConfig(AppConfig newConfig)
        {
            SaveAppConfigCalled = true;
            Config.Keyboard.VidHex = newConfig.Keyboard.VidHex;
            Config.Keyboard.PidHex = newConfig.Keyboard.PidHex;
            Config.Keyboard.InterfaceId = newConfig.Keyboard.InterfaceId;
            Config.Keyboard.DevicePath = newConfig.Keyboard.DevicePath;
            Config.Keyboard.Profile1Brightness = newConfig.Keyboard.Profile1Brightness;
            Config.Keyboard.Profile2Brightness = newConfig.Keyboard.Profile2Brightness;
            Config.Keyboard.ManualBrightness = newConfig.Keyboard.ManualBrightness;
            Config.Mouse.VidHex = newConfig.Mouse.VidHex;
            Config.Mouse.PidHex = newConfig.Mouse.PidHex;
            Config.Mouse.InterfaceId = newConfig.Mouse.InterfaceId;
            Config.Monitoring.AutoGameProfileSwitch = newConfig.Monitoring.AutoGameProfileSwitch;
            Config.Monitoring.AutoResolutionSwitch = newConfig.Monitoring.AutoResolutionSwitch;
            Config.Monitoring.DoublePressIntervalMs = newConfig.Monitoring.DoublePressIntervalMs;
            Config.Monitoring.Games = newConfig.Monitoring.Games;
            Config.ForceKill.ProtectedProcesses = newConfig.ForceKill.ProtectedProcesses;
            return SaveAppConfigResult;
        }

        public bool ApplyAutomationProfileCalled = false;
        public bool ApplyAutomationProfileIsGame = false;
        public void ApplyAutomationProfile(bool isGame)
        {
            ApplyAutomationProfileCalled = true;
            ApplyAutomationProfileIsGame = isGame;
            Status.PollingState = isGame ? "high" : "low";
            Status.DpiState = isGame ? "high" : "low";
            StatusChanged?.Invoke();
        }

        public System.Collections.Generic.IReadOnlyList<HidDeviceInfo> EnumerateHidDevices()
        {
            return Array.Empty<HidDeviceInfo>();
        }

        #pragma warning disable CS0067
        public event Action<string, string>? NotificationRequested;
        #pragma warning restore CS0067

        public string? GetForegroundProcessName()
        {
            return null;
        }

        public string ApplyBorderlessNow(BorderlessAppRule rule)
        {
            return "WINDOW_NOT_FOUND";
        }

        public string EnsureBorderlessWindow(string exePath, string monitorDevice, int xOffset, int yOffset, int width, int height)
        {
            return "WINDOW_NOT_FOUND";
        }

        public bool IsKeyboardShortcutOverlayVisible { get; set; } = false;
        public bool IsGamepadShortcutOverlayVisible { get; set; } = false;
        public void PreloadTouchKeyboard() { }
        public void InvokeTouchKeyboard(bool manageControllerVkMapping = true) { }
        public void SimulateKeyboardShortcut(string shortcutText) { }
        public void RunExecutable(string exePath) { }
        public void OpenTerminal(string? workingDirectory = null) { }
        public void OpenTerminalAdmin(string? workingDirectory = null) { }
        public bool TerminalContextMenuInstalled { get; set; } = false;
        public bool TerminalNormalEnabled { get; set; } = true;
        public bool TerminalElevatedEnabled { get; set; } = true;
        public bool AreTerminalContextMenuEntriesInstalled() => TerminalContextMenuInstalled;
        public void InstallTerminalContextMenuEntries() => TerminalContextMenuInstalled = true;
        public void UninstallTerminalContextMenuEntries() => TerminalContextMenuInstalled = false;
        public void EnsureTerminalContextMenuEntries() => InstallTerminalContextMenuEntries();
        public IReadOnlyList<TerminalContextMenuCommand> GetTerminalContextMenuCommands() => new[]
        {
            new TerminalContextMenuCommand(
                "normal",
                "Normal",
                "Abre o Windows Terminal sem elevação.",
                TerminalNormalEnabled),
            new TerminalContextMenuCommand(
                "elevated",
                "Elevado",
                "Abre o Windows Terminal como administrador.",
                TerminalElevatedEnabled)
        };
        public bool SetTerminalContextMenuCommandEnabled(string commandId, bool enabled)
        {
            if (commandId.Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                TerminalNormalEnabled = enabled;
                return true;
            }
            if (commandId.Equals("elevated", StringComparison.OrdinalIgnoreCase))
            {
                TerminalElevatedEnabled = enabled;
                return true;
            }
            return false;
        }
        public bool RestoreTerminalContextMenuCommands()
        {
            TerminalNormalEnabled = true;
            TerminalElevatedEnabled = true;
            return true;
        }
        public bool ExternalContextMenuEntryEnabled { get; set; } = true;
        public IReadOnlyList<ExplorerContextMenuEntry> GetExplorerContextMenuEntries() => new[]
        {
            new ExplorerContextMenuEntry(
                "external-test",
                "Abrir com aplicativo de teste",
                "Aplicativo de teste",
                "Entrada simulada do menu de contexto.",
                "Extensão do Explorer",
                "Arquivos e pastas",
                ExternalContextMenuEntryEnabled,
                isManagedByTutzApp: !ExternalContextMenuEntryEnabled,
                canToggle: true,
                requiresElevation: false,
                controlMode: "blocked-clsid",
                clsid: "{11111111-1111-1111-1111-111111111111}")
        };
        public bool SetExplorerContextMenuEntryEnabled(ExplorerContextMenuEntry entry, bool enabled)
        {
            ExternalContextMenuEntryEnabled = enabled;
            return true;
        }
        public void ShowKeyboardShortcutOverlay() => IsKeyboardShortcutOverlayVisible = true;
        public void HideKeyboardShortcutOverlay() => IsKeyboardShortcutOverlayVisible = false;
        public void ShowGamepadShortcutOverlay() => IsGamepadShortcutOverlayVisible = true;
        public void HideGamepadShortcutOverlay() => IsGamepadShortcutOverlayVisible = false;
    }

    public class MockProcessMonitorService : IProcessMonitorService
    {
        public bool IsCs2Running { get; set; } = false;
        public bool IsAnyGameRunning { get; set; } = false;
        public void StartMonitoring(CancellationToken cancellationToken) { }
    }

    public class MockKeyboardHookService : IKeyboardHookService
    {
        public event Action? HelpRequested;
        public void StartHook() { }
        public void StopHook() { }
        public void TriggerHelp() => HelpRequested?.Invoke();
    }
    #endregion
}
