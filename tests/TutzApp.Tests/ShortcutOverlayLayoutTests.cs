using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using TutzApp.Models;

namespace TutzApp.Tests;

public sealed class ShortcutOverlayLayoutTests
{
    [Fact]
    public void KeyboardOverlay_PreservesAllShortcutsWithoutScrolling()
    {
        string root = FindProjectRoot();
        string xamlPath = Path.Combine(
            root,
            "src",
            "Presentation",
            "Views",
            "KeyboardShortcutOverlayWindow.xaml");
        string codePath = xamlPath + ".cs";
        string xaml = File.ReadAllText(xamlPath);
        string code = File.ReadAllText(codePath);
        XDocument document = XDocument.Parse(xaml);
        XElement window = Assert.IsType<XElement>(document.Root);

        Assert.True(ParseDouble(window.Attribute("Width")?.Value) >= 1100);
        Assert.True(ParseDouble(window.Attribute("Height")?.Value) >= 680);
        Assert.DoesNotContain("<ScrollViewer", xaml, StringComparison.Ordinal);
        Assert.Contains("<Viewbox", xaml, StringComparison.Ordinal);
        Assert.Contains("StretchDirection=\"DownOnly\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<UniformGrid Columns=\"4\" Rows=\"4\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinition Width=\"142\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"142\"", xaml, StringComparison.Ordinal);

        string[] shortcuts =
        {
            "F1",
            "Ctrl + Alt + F4",
            "Ctrl + Alt + K",
            "Win + T",
            "Win + Ctrl + T",
            "Alt + Home",
            "Alt + F10",
            "Alt + F11 / F12",
            "Win + Alt + 1 / 2",
            "Tecla Win (solta)",
            "Alt + 1..5",
            "Alt + 6..7",
            "Alt + Setas",
            "Alt + BS / Del",
            "Alt direito + A / S",
            "Alt + N / Q / E / D"
        };

        foreach (string shortcut in shortcuts)
        {
            Assert.Contains(shortcut, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GamepadOverlay_FitsAllCardsWithoutScrollingAndDocumentsStartOptionsHold()
    {
        string path = Path.Combine(
            FindProjectRoot(),
            "src",
            "Presentation",
            "Views",
            "GamepadShortcutOverlayWindow.xaml");
        string xaml = File.ReadAllText(path);
        XDocument document = XDocument.Parse(xaml);
        XElement window = Assert.IsType<XElement>(document.Root);

        Assert.True(ParseDouble(window.Attribute("Width")?.Value) >= 1100);
        Assert.DoesNotContain("<ScrollViewer", xaml, StringComparison.Ordinal);
        Assert.Contains("<Viewbox", xaml, StringComparison.Ordinal);
        Assert.Contains("StretchDirection=\"DownOnly\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ColumnCount", xaml, StringComparison.Ordinal);
        Assert.Contains("Segure START / OPTIONS por 3 segundos", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinition Width=\"190\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void GamepadHold3s_PollsTheInterpretedHidStateAndRunsOnlyInTheMainMonitor()
    {
        string monitorPath = Path.Combine(
            FindProjectRoot(),
            "src",
            "Features",
            "Gamepad",
            "Services",
            "GamepadMonitorService.cs");
        string parserPath = Path.Combine(
            FindProjectRoot(),
            "src",
            "Features",
            "Gamepad",
            "Services",
            "RawInputGamepadStateProvider.cs");
        string appPath = Path.Combine(FindProjectRoot(), "App.xaml.cs");
        string monitor = File.ReadAllText(monitorPath);
        string parser = File.ReadAllText(parserPath);
        string app = File.ReadAllText(appPath);

        Assert.Contains("TryReadButtons(out ushort interpretedButtons)", monitor, StringComparison.Ordinal);
        Assert.Contains("UpdateGamepadHelpHoldFromInterpretedState", monitor, StringComparison.Ordinal);
        Assert.Contains("now - _gamepadHelpHoldStartedAt < 3000", monitor, StringComparison.Ordinal);
        Assert.Contains("(interpretedButtons & NativeMethods.XINPUT_GAMEPAD_START) != 0", monitor, StringComparison.Ordinal);
        Assert.Contains("if (!_forceKillOnly)", monitor, StringComparison.Ordinal);
        Assert.Contains("ExecuteSystemAction(\"ToggleGamepadHelp\")", monitor, StringComparison.Ordinal);
        Assert.DoesNotContain("StartGamepadHelpHoldTimer", monitor, StringComparison.Ordinal);
        Assert.DoesNotContain("_gamepadHelpHoldCancellation", monitor, StringComparison.Ordinal);
        Assert.Contains("OPTIONS", monitor, StringComparison.Ordinal);
        Assert.Contains("OPTIONS", parser, StringComparison.Ordinal);
        Assert.Contains("TrySendCommandToMainApp(\"toggle-gamepad-help\")", monitor, StringComparison.Ordinal);
        Assert.Contains("command.Equals(\"toggle-gamepad-help\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void AppConfig_ExistingGamepadConfiguration_GainsStartHoldHelpShortcut()
    {
        var config = new AppConfig
        {
            GamepadShortcuts =
            [
                new GamepadShortcut
                {
                    Name = "Atalho existente",
                    Buttons = ["BACK", "X"],
                    TriggerMode = "Press",
                    ActionType = "System",
                    ActionValue = "VirtualKeyboard"
                }
            ]
        };

        config.InitializeDefaults();

        Assert.Contains(config.GamepadShortcuts, shortcut =>
            shortcut.Buttons.Count == 1 &&
            shortcut.Buttons[0].Equals("START", StringComparison.OrdinalIgnoreCase) &&
            shortcut.TriggerMode.Equals("Hold3s", StringComparison.OrdinalIgnoreCase) &&
            shortcut.ActionValue.Equals("ToggleGamepadHelp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExternalContextMenuScanner_HasWindowsTerminalStoreFallback()
    {
        string path = Path.Combine(
            FindProjectRoot(),
            "src",
            "Features",
            "ContextMenu",
            "Services",
            "SystemControlService.ExternalContextMenu.cs");
        string source = File.ReadAllText(path);

        Assert.Contains("Microsoft.WindowsTerminal", source, StringComparison.Ordinal);
        Assert.Contains("{9F156763-7844-4DC4-B2B1-901F640F5155}", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenTerminalHere", source, StringComparison.Ordinal);
        Assert.Contains("Abrir no Terminal", source, StringComparison.Ordinal);
        Assert.Contains("DirectoryAndBackground", source, StringComparison.Ordinal);
        Assert.Contains("PackagedCom\\ClassIndex", source, StringComparison.Ordinal);
        Assert.Contains("WindowsApps", source, StringComparison.Ordinal);
        Assert.Contains("Directory.EnumerateDirectories", source, StringComparison.Ordinal);
    }

    private static double ParseDouble(string? value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value));
        return double.Parse(value!, CultureInfo.InvariantCulture);
    }

    private static string FindProjectRoot()
    {
        IEnumerable<string> startingDirectories = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (string startingDirectory in startingDirectories)
        {
            DirectoryInfo? directory = new(startingDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TutzApp.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the TutzApp project root.");
    }
}
