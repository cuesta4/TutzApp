using System;
using System.Collections.Generic;
using System.IO;

namespace TutzApp.Tests;

public sealed class ProjectLayoutTests
{
    [Fact]
    public void Solution_UsesFeatureOrientedSourceLayout()
    {
        string root = FindProjectRoot();

        Assert.True(Directory.Exists(Path.Combine(root, "src", "Core")));
        Assert.True(Directory.Exists(Path.Combine(root, "src", "Features")));
        Assert.True(Directory.Exists(Path.Combine(root, "src", "Infrastructure")));
        Assert.True(Directory.Exists(Path.Combine(root, "src", "Presentation")));
        Assert.True(Directory.Exists(Path.Combine(root, "build", "ContextMenu")));
        Assert.True(Directory.Exists(Path.Combine(root, "tests", "TutzApp.Tests")));
        Assert.True(Directory.Exists(Path.Combine(root, "tools", "GamepadLiveTest")));
    }

    [Fact]
    public void LegacyFlatSourceDirectories_AreNotPresent()
    {
        string root = FindProjectRoot();
        string[] legacyDirectories =
        {
            "Common",
            "Models",
            "Services",
            "ViewModels",
            "Views",
            "ShellExtension",
            "Packaging",
            "TutzApp.Tests",
            "GamepadLiveTest"
        };

        foreach (string directory in legacyDirectories)
        {
            Assert.False(
                Directory.Exists(Path.Combine(root, directory)),
                $"Legacy root directory should have been moved: {directory}");
        }
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
