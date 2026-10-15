using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>Runs the oltool that the build puts in the test folder, as the editor runs it.</summary>
internal static class Oltool
{
    private static string ToolPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oltool.exe" : "oltool");

    /// <returns>The exit code, the standard output and the standard error.</returns>
    public static (int Exit, string Output, string Errors) Run(params string[] arguments)
    {
        Assert.True(File.Exists(ToolPath), $"{ToolPath} is not in the test folder.");
        var info = new ProcessStartInfo(ToolPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("oltool did not end.");
        }
        return (process.ExitCode, output.Result, errors.Result);
    }
}
