using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// <c>oltool validate --active &lt;mod&gt; --json</c> on the retail data. The editor runs it after each save (roadmap
/// rule R4): it reports only the problems that the active mod adds to the mods that load before it.
/// </summary>
public sealed class OltoolValidateTests
{
    private static string ToolPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oltool.exe" : "oltool");

    [Fact]
    public void TheActiveModGetsOnlyTheProblemsThatItAdds()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var root = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            // The sample mod loads first. Each other mod sells a good that does not exist.
            var lower = Mod(root, "low", "no_such_lower_good");
            var active = Mod(root, "act", "no_such_good");
            var profile = Path.Combine(root, "profiles", "test.json");
            Write(profile, JsonSerializer.Serialize(new
            {
                @base = baseFolder,
                mods = new[] { GameDataPaths.SampleMod("ol-sample"), lower, active },
            }));

            var (exit, output, errors) = Run("validate", profile, "--active", "act", "--json");

            Assert.True(exit == 1, errors);
            var problems = ValidationJson.Read(output);
            Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error && p.Message.Contains("no_such_good"));
            // The problem of the lower mod is in the lower layer too, so it is not new.
            Assert.DoesNotContain(problems, p => p.Message.Contains("no_such_lower_good"));
            Assert.DoesNotContain(problems, p => p.Message.Contains("ol_starrunner", StringComparison.OrdinalIgnoreCase));

            // The sample mod adds no problem to the base data. The later mod does not count.
            var (sampleExit, sampleOutput, sampleErrors) = Run("validate", profile, "--active", "ol-sample", "--json");
            Assert.True(sampleExit == 0, sampleErrors);
            Assert.DoesNotContain(ValidationJson.Read(sampleOutput), p => p.Severity == DiagnosticSeverity.Error);

            var (missing, missingOutput, missingErrors) = Run("validate", profile, "--active", "none", "--json");
            Assert.Equal(2, missing);
            Assert.Equal("", missingOutput.Trim());
            Assert.Contains("has no mod 'none'", missingErrors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A mod that sells a good at li01_01_base. Returns its folder.</summary>
    private static string Mod(string root, string id, string good)
    {
        var folder = Path.Combine(root, "mods", id);
        Write(Path.Combine(folder, "mod.json"), $$"""{ "id": "{{id}}", "formatVersion": 1 }""");
        Write(Path.Combine(folder, "patches", "DATA", "EQUIPMENT", "market_misc.ini"),
            $"[BaseGood]\r\nbase = li01_01_base\r\nmarketgood = {good}, 0, -1, 1, 1, 0, 1\r\n");
        return folder;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static (int Exit, string Output, string Errors) Run(params string[] arguments)
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
