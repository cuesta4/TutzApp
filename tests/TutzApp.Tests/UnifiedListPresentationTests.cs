using System;
using System.Collections.Generic;
using System.IO;

namespace TutzApp.Tests;

public sealed class UnifiedListPresentationTests
{
    [Fact]
    public void MainWindow_UsesOneEntryLanguageAcrossRequestedLists()
    {
        string xaml = File.ReadAllText(FindProjectFile(
            "src", "Presentation", "Views", "MainWindow.xaml"));

        Assert.Contains("x:Key=\"UnifiedEntryBorderStyle\"", xaml);
        Assert.Contains("x:Key=\"UnifiedEntryListStyle\"", xaml);
        Assert.Contains("x:Key=\"UnifiedEntryListItemStyle\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding VibranceAppRules}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding RecognizedGamepads}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding GamepadShortcuts}\"", xaml);
        Assert.Contains("Style=\"{StaticResource UnifiedShortcutKeyBorderStyle}\"", xaml);
    }

    [Fact]
    public void ExternalContextMenuResults_AreSearchableAndHeightLimited()
    {
        string xaml = File.ReadAllText(FindProjectFile(
            "src", "Presentation", "Views", "MainWindow.xaml"));
        string viewModel = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Presentation", "MainViewModel.ContextMenu.cs"));

        Assert.Contains("ExplorerContextMenuSearchText", xaml);
        Assert.Contains("ItemsSource=\"{Binding ExplorerContextMenuEntriesView}\"", xaml);
        Assert.Contains("MaxHeight=\"356\"", xaml);
        Assert.Contains("CollectionViewSource.GetDefaultView", viewModel);
        Assert.Contains("MatchesExplorerContextMenuSearch", viewModel);
        Assert.Contains("CurrentCultureIgnoreCase", viewModel);
    }


    [Fact]
    public void VibranceDeleteButton_IsAlignedWithTheNumberBoxRow()
    {
        string xaml = File.ReadAllText(FindProjectFile(
            "src", "Presentation", "Views", "MainWindow.xaml"));

        Assert.Contains("<TextBlock Grid.Row=\"0\" Grid.Column=\"2\"", xaml);
        Assert.Contains("<ui:NumberBox Grid.Row=\"1\" Grid.Column=\"2\"", xaml);
        Assert.Contains("<ui:Button Grid.Row=\"1\" Grid.Column=\"3\"", xaml);
    }

    private static string FindProjectFile(params string[] relativeParts)
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
                string candidate = directory.FullName;
                foreach (string part in relativeParts)
                {
                    candidate = Path.Combine(candidate, part);
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate project file: {Path.Combine(relativeParts)}");
    }
}
