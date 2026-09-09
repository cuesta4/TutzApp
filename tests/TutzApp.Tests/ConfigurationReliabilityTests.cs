using System.Collections.Generic;
using System.IO;

namespace TutzApp.Tests;

public sealed class ConfigurationReliabilityTests
{
    [Fact]
    public void ExternalConfiguration_IsValidatedWithoutAFileWatcher()
    {
        string appSource = File.ReadAllText(FindProjectFile("App.xaml.cs"));

        Assert.Contains("AddValidatedExternalConfiguration", appSource);
        Assert.Contains("JsonDocument.Parse", appSource);
        Assert.Contains("QuarantineInvalidExternalConfiguration", appSource);
        Assert.Contains("AddJsonStream", appSource);
        Assert.DoesNotContain("AddJsonFile(", appSource);
        Assert.DoesNotContain("reloadOnChange", appSource);
    }

    [Fact]
    public void AppSettingsWrites_AreSerializedAndAtomicallyReplaced()
    {
        string source = File.ReadAllText(FindProjectFile(
            "src", "Infrastructure", "System", "SystemControlService.cs"));

        Assert.Contains("AppSettingsWriteSyncRoot", source);
        Assert.Contains("WriteAppSettingsAtomically", source);
        Assert.Contains("FileOptions.WriteThrough", source);
        Assert.Contains("stream.Flush(flushToDisk: true)", source);
        Assert.Contains("File.Replace(temporaryPath, path", source);
        Assert.DoesNotContain("File.WriteAllText(path, json)", source);
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
