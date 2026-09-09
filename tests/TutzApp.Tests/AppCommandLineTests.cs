using System.Reflection;

namespace TutzApp.Tests;

public sealed class AppCommandLineTests
{
    [Theory]
    [InlineData("--open-terminal", false)]
    [InlineData("--open-terminal-admin", true)]
    public void TerminalShellArguments_AreParsedAsOneShotCommands(
        string command,
        bool expectedElevated)
    {
        const string expectedDirectory = @"C:\Test Folder";

        (bool matched, bool elevated, string? directory) = ParseTerminalCommand(
            command,
            expectedDirectory);

        Assert.True(matched);
        Assert.Equal(expectedElevated, elevated);
        Assert.Equal(expectedDirectory, directory);
    }

    [Fact]
    public void UnrelatedArguments_AreNotParsedAsTerminalCommands()
    {
        (bool matched, bool elevated, string? directory) = ParseTerminalCommand(
            "--background");

        Assert.False(matched);
        Assert.False(elevated);
        Assert.Null(directory);
    }

    private static (bool Matched, bool Elevated, string? Directory) ParseTerminalCommand(
        params string[] args)
    {
        MethodInfo method = typeof(TutzApp.App).GetMethod(
            "TryParseOneShotTerminalCommand",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "TryParseOneShotTerminalCommand was not found.");

        object?[] invocationArguments = { args, false, null };
        bool matched = (bool)(method.Invoke(null, invocationArguments)
            ?? throw new InvalidOperationException("Parser returned null."));

        return (
            matched,
            (bool)(invocationArguments[1] ?? false),
            invocationArguments[2] as string);
    }
}
