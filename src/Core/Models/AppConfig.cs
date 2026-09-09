using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
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
                        case "toggledisplaytarget": return "Alterna entre monitor e TV, mantendo somente uma tela ativa";
                        case "togglegamepadhelp": return "Exibe/Oculta este painel de ajuda";
                        case "volumedown": return "Diminui o volume do sistema";
                        case "volumeup": return "Aumenta o volume do sistema";
                        case "showgamebar": return "Abre a Xbox Game Bar (Win+G)";
                        case "screenshot": return "Captura de tela (Win+PrtSc)";
                        case "forcekillforegroundapp": return "Força o fechamento da janela ativa";
                        case "togglegamepadmousemode": return "Ativa/desativa o modo mouse do gamepad";
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
        public NoMoreBorderSettings NoMoreBorder { get; set; } = new();
        public ForceKillSettings ForceKill { get; set; } = new();
        public GamepadSettings Gamepad { get; set; } = new();
        public GameConsoleSettings GameConsole { get; set; } = new();
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
            ForceKill ??= new ForceKillSettings();
            Gamepad ??= new GamepadSettings();
            GameConsole ??= new GameConsoleSettings();
            GameConsole.GuideHoldThresholdMs = System.Math.Clamp(GameConsole.GuideHoldThresholdMs, 200, 3000);
            Resolutions ??= new Dictionary<string, ResolutionInfo>();
            Monitoring.Games ??= new List<string>();
            Vibrance.AppRules ??= new List<VibranceAppRule>();
            NoMoreBorder ??= new NoMoreBorderSettings();
            NoMoreBorder.Apps ??= new List<BorderlessAppRule>();
            ForceKill.ProtectedProcesses ??= new List<string>();
            Gamepad.RecognizedDevices ??= new List<RecognizedGamepad>();
            Gamepad.MouseModeSensitivityPercent = System.Math.Clamp(Gamepad.MouseModeSensitivityPercent, 10, 200);
            EnsureKnownRawHidGamepadAxisOptions();
            EnsureDefaultProtectedProcesses();

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
                    new() { Name = "Modo Mouse", Description = "Ativa ou desativa o controle do mouse pelo gamepad", Buttons = new() { "L-THUMB" }, TriggerMode = "DoublePress", ActionType = "System", ActionValue = "ToggleGamepadMouseMode" },
                    new() { Name = "Painel de Atalhos (Controle)", Description = "Exibe ou oculta a lista de atalhos do gamepad", Buttons = new() { "START" }, TriggerMode = "Hold3s", ActionType = "System", ActionValue = "ToggleGamepadHelp" },
                    new() { Name = "Teclado Virtual", Description = "Abre o teclado virtual na tela", Buttons = new() { "BACK", "X" }, TriggerMode = "Press", ActionType = "System", ActionValue = "VirtualKeyboard" },
                    new() { Name = "Alternar Tela (Monitor/TV)", Description = "Alterna entre monitor e TV, mantendo somente uma tela ativa", Buttons = new() { "BACK", "B" }, TriggerMode = "Press", ActionType = "System", ActionValue = "ToggleDisplayTarget" },
                    new() { Name = "Fechar Janela (SuperF4)", Description = "Força o fechamento da janela ativa pelo helper admin", Buttons = new() { "BACK", "Y" }, TriggerMode = "Press", ActionType = "System", ActionValue = "ForceKillForegroundApp" },
                    new() { Name = "Diminuir Volume", Description = "Reduz o volume do sistema em um nível", Buttons = new() { "BACK", "LB" }, TriggerMode = "Press", ActionType = "System", ActionValue = "VolumeDown" },
                    new() { Name = "Aumentar Volume", Description = "Aumenta o volume do sistema em um nível", Buttons = new() { "BACK", "RB" }, TriggerMode = "Press", ActionType = "System", ActionValue = "VolumeUp" },
                    new() { Name = "Alt + R", Description = "Simula Alt + R", Buttons = new() { "BACK", "A" }, TriggerMode = "Press", ActionType = "SendKeys", ActionValue = "Alt+R" }
                });
            }
            else
            {
                foreach (var shortcut in GamepadShortcuts)
                {
                    if (IsLegacyForceKillShortcut(shortcut))
                    {
                        shortcut.Name = "Fechar Janela (SuperF4)";
                        shortcut.Description = "Força o fechamento da janela ativa pelo helper admin";
                        shortcut.Buttons = new() { "BACK", "Y" };
                        shortcut.TriggerMode = "Press";
                        shortcut.ActionType = "System";
                        shortcut.ActionValue = "ForceKillForegroundApp";
                    }

                    if (string.Equals(shortcut.ActionValue, "ToggleGamepadHelp", System.StringComparison.OrdinalIgnoreCase))
                    {
                        shortcut.Name = "Painel de Atalhos (Controle)";
                        shortcut.Description = "Exibe ou oculta a lista de atalhos do gamepad";
                        shortcut.Buttons = new() { "START" };
                        shortcut.TriggerMode = "Hold3s";
                        shortcut.ActionType = "System";
                        shortcut.ActionValue = "ToggleGamepadHelp";
                    }

                }

                bool hasScreenshotShortcut = GamepadShortcuts.Any(IsScreenshotShortcut);
                if (!hasScreenshotShortcut)
                {
                    GamepadShortcuts.Add(new GamepadShortcut
                    {
                        Name = "Captura de Tela",
                        Description = "Tira screenshot e salva em Imagens",
                        Buttons = new() { "R-THUMB" },
                        TriggerMode = "DoublePress",
                        ActionType = "System",
                        ActionValue = "Screenshot"
                    });
                }

                bool hasMouseModeShortcut = GamepadShortcuts.Any(IsMouseModeShortcut);
                if (!hasMouseModeShortcut)
                {
                    GamepadShortcuts.Add(new GamepadShortcut
                    {
                        Name = "Modo Mouse",
                        Description = "Ativa ou desativa o controle do mouse pelo gamepad",
                        Buttons = new() { "L-THUMB" },
                        TriggerMode = "DoublePress",
                        ActionType = "System",
                        ActionValue = "ToggleGamepadMouseMode"
                    });
                }

                bool hasGamepadHelpShortcut = GamepadShortcuts.Any(IsGamepadHelpShortcut);
                if (!hasGamepadHelpShortcut)
                {
                    GamepadShortcuts.Add(new GamepadShortcut
                    {
                        Name = "Painel de Atalhos (Controle)",
                        Description = "Exibe ou oculta a lista de atalhos do gamepad",
                        Buttons = new() { "START" },
                        TriggerMode = "Hold3s",
                        ActionType = "System",
                        ActionValue = "ToggleGamepadHelp"
                    });
                }

                bool hasForceKillShortcut = GamepadShortcuts.Any(IsForceKillShortcut);
                if (!hasForceKillShortcut)
                {
                    GamepadShortcuts.Add(new GamepadShortcut
                    {
                        Name = "Fechar Janela (SuperF4)",
                        Description = "Força o fechamento da janela ativa pelo helper admin",
                        Buttons = new() { "BACK", "Y" },
                        TriggerMode = "Press",
                        ActionType = "System",
                        ActionValue = "ForceKillForegroundApp"
                    });
                }

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

                bool hasDisplayToggle = GamepadShortcuts.Any(IsDisplayToggleShortcut)
                    || GamepadShortcuts.Any(s =>
                        s.Buttons != null
                        && s.Buttons.Count == 2
                        && s.Buttons.Any(button => button.Equals("BACK", System.StringComparison.OrdinalIgnoreCase) || button.Equals("SELECT", System.StringComparison.OrdinalIgnoreCase))
                        && s.Buttons.Any(button => button.Equals("B", System.StringComparison.OrdinalIgnoreCase)));

                if (!hasDisplayToggle)
                {
                    GamepadShortcuts.Add(new GamepadShortcut
                    {
                        Name = "Alternar Tela (Monitor/TV)",
                        Description = "Alterna entre monitor e TV, mantendo somente uma tela ativa",
                        Buttons = new() { "BACK", "B" },
                        TriggerMode = "Press",
                        ActionType = "System",
                        ActionValue = "ToggleDisplayTarget"
                    });
                }
            }
        }

        private void EnsureKnownRawHidGamepadAxisOptions()
        {
            if (Gamepad?.RecognizedDevices == null)
            {
                return;
            }

            foreach (var device in Gamepad.RecognizedDevices)
            {
                if (!string.Equals(device.Layout, "RawHidMappedV2", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool knownXboxRawHid = string.Equals(device.VidHex, "045E", System.StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(device.PidHex, "028E", System.StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(device.PidHex, "02FF", System.StringComparison.OrdinalIgnoreCase));

                if (!knownXboxRawHid)
                {
                    continue;
                }

                device.LeftY ??= new GamepadAxisMapping { Offset = 3, Format = "UInt16LE", Center = 32767.5, Range = 32767.5 };
                device.RightY ??= new GamepadAxisMapping { Offset = 7, Format = "UInt16LE", Center = 32767.5, Range = 32767.5 };

                device.LeftY.Invert = false;
                device.RightY.Invert = true;
            }
        }

        private void EnsureDefaultProtectedProcesses()
        {
            ForceKill.ProtectedProcesses = NormalizeProtectedProcesses(ForceKill.ProtectedProcesses);
            EnsureProtectedProcess("explorer.exe");
            EnsureProtectedProcess("TutzApp.exe");
        }

        private void EnsureProtectedProcess(string processName)
        {
            string normalized = NormalizeProcessName(processName);
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            if (!ForceKill.ProtectedProcesses.Any(p => p.Equals(normalized, System.StringComparison.OrdinalIgnoreCase)))
            {
                ForceKill.ProtectedProcesses.Add(normalized);
            }
        }

        private static List<string> NormalizeProtectedProcesses(IEnumerable<string>? processes)
        {
            var result = new List<string>();
            if (processes == null)
            {
                return result;
            }

            foreach (string process in processes)
            {
                string normalized = NormalizeProcessName(process);
                if (!string.IsNullOrEmpty(normalized) &&
                    !result.Any(existing => existing.Equals(normalized, System.StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        private static string NormalizeProcessName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return string.Empty;
            }

            return Path.GetFileName(processName.Trim());
        }

        private static bool IsLegacyForceKillShortcut(GamepadShortcut shortcut)
        {
            return shortcut.Buttons != null
                && shortcut.Buttons.Count == 2
                && shortcut.Buttons.Any(button => button.Equals("BACK", System.StringComparison.OrdinalIgnoreCase))
                && shortcut.Buttons.Any(button => button.Equals("Y", System.StringComparison.OrdinalIgnoreCase))
                && shortcut.ActionType.Equals("SendKeys", System.StringComparison.OrdinalIgnoreCase)
                && shortcut.ActionValue.Replace(" ", string.Empty).Equals("Ctrl+Alt+F4", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsForceKillShortcut(GamepadShortcut shortcut)
        {
            return shortcut.Buttons != null
                && shortcut.Buttons.Count == 2
                && shortcut.Buttons.Any(button => button.Equals("BACK", System.StringComparison.OrdinalIgnoreCase))
                && shortcut.Buttons.Any(button => button.Equals("Y", System.StringComparison.OrdinalIgnoreCase))
                && shortcut.ActionType.Equals("System", System.StringComparison.OrdinalIgnoreCase)
                && shortcut.ActionValue.Equals("ForceKillForegroundApp", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMouseModeShortcut(GamepadShortcut shortcut)
        {
            return shortcut.Buttons != null
                && shortcut.Buttons.Count == 1
                && shortcut.Buttons.Any(button =>
                    button.Equals("L-THUMB", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("LTHUMB", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("LS", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("LEFT_THUMB", System.StringComparison.OrdinalIgnoreCase))
                && shortcut.TriggerMode.Equals("DoublePress", System.StringComparison.OrdinalIgnoreCase)
                && shortcut.ActionType.Equals("System", System.StringComparison.OrdinalIgnoreCase)
                && shortcut.ActionValue.Equals("ToggleGamepadMouseMode", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGamepadHelpShortcut(GamepadShortcut shortcut)
        {
            return shortcut.Buttons != null
                && shortcut.Buttons.Count == 1
                && shortcut.Buttons.Any(button =>
                    button.Equals("START", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("START_MENU", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("MENU", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("OPTIONS", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("OPTION", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("PS_OPTIONS", System.StringComparison.OrdinalIgnoreCase))
                && string.Equals(shortcut.TriggerMode, "Hold3s", System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(shortcut.ActionType, "System", System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(shortcut.ActionValue, "ToggleGamepadHelp", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsScreenshotShortcut(GamepadShortcut shortcut)
        {
            return shortcut.Buttons != null
                && shortcut.Buttons.Count == 1
                && shortcut.Buttons.Any(button =>
                    button.Equals("R-THUMB", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("RTHUMB", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("RS", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("RIGHT_THUMB", System.StringComparison.OrdinalIgnoreCase))
                && shortcut.TriggerMode.Equals("DoublePress", System.StringComparison.OrdinalIgnoreCase)
                && shortcut.ActionType.Equals("System", System.StringComparison.OrdinalIgnoreCase)
                && shortcut.ActionValue.Equals("Screenshot", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDisplayToggleShortcut(GamepadShortcut shortcut)
        {
            return shortcut.Buttons != null
                && shortcut.Buttons.Count == 2
                && shortcut.Buttons.Any(button =>
                    button.Equals("BACK", System.StringComparison.OrdinalIgnoreCase) ||
                    button.Equals("SELECT", System.StringComparison.OrdinalIgnoreCase))
                && shortcut.Buttons.Any(button => button.Equals("B", System.StringComparison.OrdinalIgnoreCase))
                && shortcut.TriggerMode.Equals("Press", System.StringComparison.OrdinalIgnoreCase)
                && shortcut.ActionType.Equals("System", System.StringComparison.OrdinalIgnoreCase)
                && shortcut.ActionValue.Equals("ToggleDisplayTarget", System.StringComparison.OrdinalIgnoreCase);
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

    public class ForceKillSettings
    {
        public List<string> ProtectedProcesses { get; set; } = new() { "explorer.exe" };
    }

    public class GamepadSettings
    {
        public int MouseModeSensitivityPercent { get; set; } = 100;
        public List<RecognizedGamepad> RecognizedDevices { get; set; } = new();
    }

    public class GamepadButtonMapping
    {
        public int ByteOffset { get; set; } = -1;
        public byte Mask { get; set; }
        public bool ActiveLow { get; set; }
    }

    public class GamepadAxisMapping
    {
        public int Offset { get; set; } = -1;
        public string Format { get; set; } = "UInt16LE";
        public double Center { get; set; } = 32767.5;
        public double Range { get; set; } = 32767.5;
        public bool Invert { get; set; }
    }

    public class GamepadTriggerMapping
    {
        public int Offset { get; set; } = -1;
        public string Format { get; set; } = "CombinedUInt16LE";
        public double Center { get; set; } = 32768.0;
        public double Range { get; set; } = 32768.0;
        public bool LeftPositive { get; set; } = true;
    }

    public class GamepadDpadMapping
    {
        public int ByteOffset { get; set; } = -1;
        public int Shift { get; set; }
        public int Mask { get; set; } = 0x0F;
        public int Neutral { get; set; }
        public int Up { get; set; } = -1;
        public int Right { get; set; } = -1;
        public int Down { get; set; } = -1;
        public int Left { get; set; } = -1;
    }

    public class RecognizedGamepad
    {
        public string Name { get; set; } = string.Empty;
        public string DevicePath { get; set; } = string.Empty;
        public string VidHex { get; set; } = string.Empty;
        public string PidHex { get; set; } = string.Empty;
        public ushort UsagePage { get; set; }
        public ushort Usage { get; set; }
        public int InputReportByteLength { get; set; }
        public int ButtonByteOffset { get; set; }
        public string Layout { get; set; } = "RawHidMappedV2";
        public string LastSeenUtc { get; set; } = string.Empty;

        public GamepadAxisMapping LeftX { get; set; } = new() { Offset = 1, Format = "UInt16LE", Center = 32767.5, Range = 32767.5, Invert = false };
        public GamepadAxisMapping LeftY { get; set; } = new() { Offset = 3, Format = "UInt16LE", Center = 32767.5, Range = 32767.5, Invert = false };
        public GamepadAxisMapping RightX { get; set; } = new() { Offset = 5, Format = "UInt16LE", Center = 32767.5, Range = 32767.5, Invert = false };
        public GamepadAxisMapping RightY { get; set; } = new() { Offset = 7, Format = "UInt16LE", Center = 32767.5, Range = 32767.5, Invert = false };
        public GamepadTriggerMapping Triggers { get; set; } = new() { Offset = 9, Format = "CombinedUInt16LE", Center = 32768.0, Range = 32768.0, LeftPositive = true };
        public GamepadDpadMapping Dpad { get; set; } = new();
        public Dictionary<string, GamepadButtonMapping> Buttons { get; set; } = new();

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                string name = string.IsNullOrWhiteSpace(Name) ? "Gamepad HID" : Name;
                string ids = string.IsNullOrWhiteSpace(VidHex) || string.IsNullOrWhiteSpace(PidHex)
                    ? "VID/PID desconhecido"
                    : $"VID_{VidHex} PID_{PidHex}";
                string layout = string.IsNullOrWhiteSpace(Layout) ? "layout" : Layout;
                return $"{name} | {ids} | Report {InputReportByteLength} | {layout}";
            }
        }
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

    public class NoMoreBorderSettings
    {
        public List<BorderlessAppRule> Apps { get; set; } = new();
    }

    public class BorderlessAppRule : INotifyPropertyChanged
    {
        private string _displayName = string.Empty;
        private int _xOffset;
        private int _yOffset;
        private int _width;
        private int _height;

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName == value) return;
                _displayName = value;
                OnPropertyChanged();
            }
        }

        public string ExecutablePath { get; set; } = string.Empty;

        public string ProcessName { get; set; } = string.Empty;

        public string MonitorDevice { get; set; } = string.Empty;

        public int XOffset
        {
            get => _xOffset;
            set
            {
                int clamped = Math.Clamp(value, -100000, 100000);
                if (_xOffset == clamped) return;
                _xOffset = clamped;
                OnPropertyChanged();
            }
        }

        public int YOffset
        {
            get => _yOffset;
            set
            {
                int clamped = Math.Clamp(value, -100000, 100000);
                if (_yOffset == clamped) return;
                _yOffset = clamped;
                OnPropertyChanged();
            }
        }

        public int Width
        {
            get => _width;
            set
            {
                int clamped = Math.Clamp(value, 0, 20000);
                if (_width == clamped) return;
                _width = clamped;
                OnPropertyChanged();
            }
        }

        public int Height
        {
            get => _height;
            set
            {
                int clamped = Math.Clamp(value, 0, 20000);
                if (_height == clamped) return;
                _height = clamped;
                OnPropertyChanged();
            }
        }

        public string IconPath { get; set; } = string.Empty;

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

    public class GameConsoleSettings
    {
        public bool SuppressWindowsControllerVkMapping { get; set; } = true;
        public bool OwnGuideButton { get; set; } = true;
        public int GuideHoldThresholdMs { get; set; } = 700;
        public bool MapGamepadToKeyboardInGameBar { get; set; } = true;
        public bool LaunchFocusAssist { get; set; } = true;
        public bool PlayniteBridgeEnabled { get; set; } = true;
        public bool? WasVkMappingOriginallyMissing { get; set; }
        public int? OriginalVkMappingValue { get; set; }
    }
}
