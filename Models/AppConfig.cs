// Models/AppConfig.cs
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TutzApp.Models
{
    public class GamepadShortcut
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Buttons { get; set; } = new();
        public string TriggerMode { get; set; } = "Press"; // "Press", "DoublePress", "Hold3s"
        public string ActionType { get; set; } = "System"; // "System", "RunExe", "SendKeys"
        public string ActionValue { get; set; } = string.Empty;

        public string ComboText
        {
            get
            {
                string combo = string.Join(" + ", Buttons);
                if (TriggerMode == "DoublePress")
                    return $"{combo} (Duplo)";
                if (TriggerMode == "Hold3s")
                    return $"{combo} (3s)";
                return combo;
            }
        }

        public string DisplayDescription
        {
            get
            {
                // If a custom description was provided, use it
                if (!string.IsNullOrWhiteSpace(Description))
                    return Description;

                // Otherwise, generate from action
                if (ActionType == "System")
                {
                    switch (ActionValue.ToLowerInvariant())
                    {
                        case "virtualkeyboard": return "Abre o teclado virtual";
                        case "togglekeyboardprofile": return "Alterna o perfil do teclado (QMK)";
                        case "poweroffmonitor": return "Desliga o monitor principal";
                        case "togglegamepadhelp": return "Exibe/Oculta este painel de ajuda";
                        case "volumedown": return "Diminui o volume do sistema";
                        case "volumeup": return "Aumenta o volume do sistema";
                        case "showgamebar": return "Abre a Xbox Game Bar (Win+G)";
                        case "screenshot": return "Captura de tela (Win+PrtSc)";
                        default: return ActionValue;
                    }
                }
                if (ActionType == "SendKeys")
                    return $"Simula teclado: {ActionValue}";
                if (ActionType == "RunExe")
                    try { return $"Executa: {System.IO.Path.GetFileName(ActionValue)}"; } catch { return $"Executa: {ActionValue}"; }
                return ActionValue;
            }
        }
    }

    public class AppConfig
    {
        public bool DebugEnabled { get; set; } = true;
        public string DebugLogPath { get; set; } = "DebugLog.txt";
        public string BatDir { get; set; } = string.Empty;
        public string TwinkleTrayPath { get; set; } = string.Empty;
        public MouseSettings Mouse { get; set; } = new();
        public KeyboardSettings Keyboard { get; set; } = new();
        public PythonSettings Python { get; set; } = new();
        public MonitoringSettings Monitoring { get; set; } = new();
        public VibranceSettings Vibrance { get; set; } = new();
        public Dictionary<string, ResolutionInfo> Resolutions { get; set; } = new();
        public List<GamepadShortcut> GamepadShortcuts { get; set; } = new();

        public AppConfig()
        {
        }

        public void InitializeDefaults()
        {
            Mouse ??= new MouseSettings();
            Keyboard ??= new KeyboardSettings();
            Python ??= new PythonSettings();
            Monitoring ??= new MonitoringSettings();
            Vibrance ??= new VibranceSettings();
            Resolutions ??= new Dictionary<string, ResolutionInfo>();
            Monitoring.Games ??= new List<string>();
            Vibrance.AppRules ??= new List<VibranceAppRule>();

            if (GamepadShortcuts == null)
            {
                GamepadShortcuts = new List<GamepadShortcut>();
            }

            if (GamepadShortcuts.Count == 0)
            {
                GamepadShortcuts.AddRange(new List<GamepadShortcut>
                {
                    new() { Name = "Xbox Game Bar", Description = "Abre a barra de jogos do Xbox", Buttons = new() { "BACK" }, TriggerMode = "DoublePress", ActionType = "System", ActionValue = "ShowGameBar" },
                    new() { Name = "Captura de Tela", Description = "Tira screenshot e salva em Imagens", Buttons = new() { "R-THUMB" }, TriggerMode = "DoublePress", ActionType = "System", ActionValue = "Screenshot" },
                    new() { Name = "Painel de Atalhos (Controle)", Description = "Exibe ou oculta a lista de atalhos do gamepad", Buttons = new() { "START" }, TriggerMode = "Hold3s", ActionType = "System", ActionValue = "ToggleGamepadHelp" },
                    new() { Name = "Teclado Virtual", Description = "Abre o teclado virtual na tela", Buttons = new() { "BACK", "X" }, TriggerMode = "Press", ActionType = "System", ActionValue = "VirtualKeyboard" },
                    new() { Name = "Fechar Janela (SuperF4)", Description = "Fecha a janela ativa com Ctrl+Alt+F4", Buttons = new() { "BACK", "Y" }, TriggerMode = "Press", ActionType = "SendKeys", ActionValue = "Ctrl+Alt+F4" },
                    new() { Name = "Diminuir Volume", Description = "Reduz o volume do sistema em um nível", Buttons = new() { "BACK", "LB" }, TriggerMode = "Press", ActionType = "System", ActionValue = "VolumeDown" },
                    new() { Name = "Aumentar Volume", Description = "Aumenta o volume do sistema em um nível", Buttons = new() { "BACK", "RB" }, TriggerMode = "Press", ActionType = "System", ActionValue = "VolumeUp" },
                    new() { Name = "Alt + R", Description = "Simula Alt + R", Buttons = new() { "BACK", "A" }, TriggerMode = "Press", ActionType = "SendKeys", ActionValue = "Alt+R" }
                });
            }
            else
            {
                bool hasAltR = GamepadShortcuts.Any(s => 
                    s.ActionType == "SendKeys" && 
                    s.ActionValue == "Alt+R" && 
                    s.Buttons.Count == 2 && 
                    s.Buttons.Contains("BACK") && 
                    s.Buttons.Contains("A"));

                if (!hasAltR)
                {
                    GamepadShortcuts.Add(new GamepadShortcut
                    {
                        Name = "Alt + R",
                        Description = "Simula Alt + R",
                        Buttons = new() { "BACK", "A" },
                        TriggerMode = "Press",
                        ActionType = "SendKeys",
                        ActionValue = "Alt+R"
                    });
                }
            }
        }
    }

    public class MouseSettings
    {
        public int HighSpeed { get; set; } = 14;
        public int SlowSpeed { get; set; } = 3;
        public int DpiHigh { get; set; } = 8000;
        public int DpiLow { get; set; } = 800;
        public int PollingHigh { get; set; } = 1000;
        public int PollingLow { get; set; } = 125;
        public string VidHex { get; set; } = "25A7";
        public string PidHex { get; set; } = "FA7C";
        public string InterfaceId { get; set; } = "mi_01";
        public string DevicePath { get; set; } = string.Empty;

        public ushort Vid => ushort.TryParse(VidHex, System.Globalization.NumberStyles.HexNumber, null, out var val) ? val : (ushort)0x25A7;
        public ushort Pid => ushort.TryParse(PidHex, System.Globalization.NumberStyles.HexNumber, null, out var val) ? val : (ushort)0xFA7C;
    }

    public class KeyboardSettings
    {
        public string VidHex { get; set; } = "3151";
        public string PidHex { get; set; } = "4015";
        public string InterfaceId { get; set; } = "mi_02";
        public string DevicePath { get; set; } = string.Empty;
        public int Profile1Brightness { get; set; } = 4;
        public int Profile2Brightness { get; set; } = 4;
        public int ManualBrightness { get; set; } = 4;
        public int AutoGameProfile { get; set; } = 1;
        public int AutoNormalProfile { get; set; } = 2;
    }

    public class HidDeviceInfo
    {
        public string DevicePath { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public string DeviceDescription { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string VidHex { get; set; } = string.Empty;
        public string PidHex { get; set; } = string.Empty;
        public string InterfaceId { get; set; } = string.Empty;
        public ushort UsagePage { get; set; }
        public ushort Usage { get; set; }
        public ushort FeatureReportByteLength { get; set; }

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                string name = !string.IsNullOrWhiteSpace(FriendlyName)
                    ? FriendlyName
                    : !string.IsNullOrWhiteSpace(DeviceDescription)
                        ? DeviceDescription
                        : "Dispositivo HID";
                string ids = string.IsNullOrWhiteSpace(VidHex) || string.IsNullOrWhiteSpace(PidHex)
                    ? "VID/PID desconhecido"
                    : $"VID_{VidHex} PID_{PidHex}";
                string iface = string.IsNullOrWhiteSpace(InterfaceId) ? "sem interface" : InterfaceId.ToUpperInvariant();
                string maker = string.IsNullOrWhiteSpace(Manufacturer) ? string.Empty : $" | {Manufacturer}";
                return $"{name}{maker} | {ids} | {iface}";
            }
        }
    }

    public class PythonSettings
    {
        public string DriverDir { get; set; } = string.Empty;
        public string DriverPath { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
    }

    public class MonitoringSettings
    {
        public int GamesCheckIntervalMs { get; set; } = 5000;
        public int GameProfileEnterDelayMs { get; set; } = 750;
        public int GameProfileExitDelayMs { get; set; } = 1000;
        public int GamepadPollIntervalMs { get; set; } = 75;
        public int DoublePressIntervalMs { get; set; } = 300;
        public bool AutoGameProfileSwitch { get; set; } = true;
        public bool AutoResolutionSwitch { get; set; } = true;
        public List<string> Games { get; set; } = new();
    }

    public class VibranceSettings
    {
        public bool Enabled { get; set; } = true;
        public int FocusPollIntervalMs { get; set; } = 2000;
        public int DefaultDriverLevel { get; set; } = 50;
        public List<VibranceAppRule> AppRules { get; set; } = new();
    }

    public class VibranceAppRule : INotifyPropertyChanged
    {
        public string DisplayName { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        private int _appVibrancePercent;

        public int AppVibrancePercent
        {
            get => _appVibrancePercent;
            set
            {
                int clamped = value < 0 ? 0 : value > 100 ? 100 : value;
                if (_appVibrancePercent == clamped) return;

                _appVibrancePercent = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DriverLevel));
            }
        }

        [JsonIgnore]
        public int DriverLevel => 50 + AppVibrancePercent / 2;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ResolutionInfo
    {
        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint RefreshRate { get; set; }
    }
}
