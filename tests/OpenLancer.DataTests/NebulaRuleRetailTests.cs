using System;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the rules of the <c>zone</c> and <c>file</c> keys of the <c>[Nebula]</c> and <c>[Asteroids]</c>
/// sections of a system file. Sections 11 and 16.3 of the M22 design give the rules. The engine reads a mod with one bad line, and
/// the test records what the engine does with it. The effects of the rules in <c>list-rules.json</c> come from these
/// results (decision D82).
/// </summary>
public sealed class NebulaRuleRetailTests(ITestOutputHelper output)
{
    private const string Li01 = @"DATA\UNIVERSE\SYSTEMS\LI01\li01.ini";
    private const string Badlands = "Zone_Li01_Badlands_Nebula";
    private const string BadlandsField = "Zone_Li01_Badlands_Asteroids";

    static NebulaRuleRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// A <c>file</c> that names no file makes the game crash while it loads the data. The parse of the universe reads
    /// the file of each nebula and field (Schema/Universe/Nebula.cs:38-45, AsteroidField.cs:39-46). For a missing file,
    /// <c>FileSystem.Open</c> throws, and nothing catches the exception. <c>FreelancerData.LoadData</c> runs the parse on
    /// a worker thread of <c>ParallelActionRunner</c>, where the exception ends the process. On the calling thread, the
    /// exception ends the load. So the rule effect is load-break. The test parses only the universe, because the full
    /// load would end the test process too.
    /// </summary>
    [Theory]
    [InlineData("Nebula", Badlands, @"solar\nebula\ol_no_such_file.ini")]
    [InlineData("Asteroids", BadlandsField, @"solar\asteroids\ol_no_such_file.ini")]
    public void AMissingFileStopsTheLoad(string type, string zone, string file)
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = Patched(baseFolder, $"[{type}]\r\nzone = {zone}\r\nfile = {file}\r\n", out var view);
        var data = new FreelancerData(new FreelancerIni(view), view);

        var thrown = Assert.ThrowsAny<Exception>(() => new UniverseIni(data.Freelancer.UniversePath!, data));

        var missing = Assert.IsType<FileNotFoundException>(thrown is AggregateException all
            ? all.Flatten().InnerExceptions.Single()
            : thrown);
        output.WriteLine(missing.Message);
        Assert.Contains("ol_no_such_file", missing.Message, StringComparison.OrdinalIgnoreCase);

        // Without the bad line, the same parse works.
        using var clean = Patched(baseFolder, "", out var cleanView);
        var cleanData = new FreelancerData(new FreelancerIni(cleanView), cleanView);
        Assert.Contains(new UniverseIni(cleanData.Freelancer.UniversePath!, cleanData).Systems,
            s => s.Nickname.Equals("li01", StringComparison.OrdinalIgnoreCase));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>
    /// A <c>[Nebula]</c> or <c>[Asteroids]</c> whose zone the system does not have. The engine drops the section with a
    /// log line, and the system keeps its other nebulae and fields. So the rule effect is drop.
    /// </summary>
    [Theory]
    [InlineData("Nebula", @"solar\nebula\Li01_Badlands_Nebula.ini")]
    [InlineData("Asteroids", @"solar\asteroids\Li01_Badlands_Asteroids.ini")]
    public void AnItemWithAnUnknownZoneIsDropped(string type, string file)
    {
        // A new section: the merge adds it at the end of the file.
        var result = Load($"[{type}]\r\nol.new = true\r\nzone = ol_no_such_zone\r\nfile = {file}\r\n");

        foreach (var problem in result.Problems.Where(p => p.Severity == DiagnosticSeverity.Error))
            output.WriteLine($"{problem.Source}: {problem.Message}");
        var db = result.RequireComplete();
        var li01 = db.Systems.Get("li01")!;
        Assert.Equal((2, 9), (li01.Nebulae.Count, li01.AsteroidFields.Count));
        Assert.Contains(result.Problems, p => p.Source == EngineDataLoader.EngineSource &&
                                               p.Message.Contains("ol_no_such_zone", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A <c>[Nebula]</c> or <c>[Asteroids]</c> without a <c>file</c> line. The parser drops the section, because the
    /// key is a required field of the section class (Schema/Universe/ZoneReference.cs). So a missing line drops the
    /// item, and it does not stop the load. The zone of the new section is the zone of the other kind, which the system
    /// has.
    /// </summary>
    [Theory]
    [InlineData("Nebula", BadlandsField)]
    [InlineData("Asteroids", Badlands)]
    public void AnItemWithoutAFileIsDropped(string type, string zone)
    {
        var result = Load($"[{type}]\r\nol.new = true\r\nzone = {zone}\r\n");

        foreach (var problem in result.Problems.Where(p => p.Severity == DiagnosticSeverity.Error))
            output.WriteLine($"{problem.Source}: {problem.Message}");
        var li01 = result.RequireComplete().Systems.Get("li01")!;
        Assert.Equal((2, 9), (li01.Nebulae.Count, li01.AsteroidFields.Count));
        Assert.Contains(result.Problems, p => p.Source == EngineDataLoader.EngineSource &&
                                               p.Message.Contains($"Missing required field file in section {type}",
                                                   StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A workspace whose new mod patches Li01, and the file system of its merged data.</summary>
    private static RetailWorkspace Patched(string baseFolder, string patch, out FileSystem view)
    {
        var workspace = new RetailWorkspace(baseFolder, "m22rules");
        if (patch.Length > 0)
        {
            var patchFile = Path.Combine(workspace.ModFolder, "patches", Li01);
            Directory.CreateDirectory(Path.GetDirectoryName(patchFile)!);
            File.WriteAllText(patchFile, "; M22 retail proof\r\n" + patch, Encoding.ASCII);
        }
        var profile = ModProfile.Load(workspace.ProfilePath);
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance,
            ThornEvaluator.Instance);
        Assert.DoesNotContain(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        view = overlay.CreateFileSystem();
        return workspace;
    }

    /// <summary>Loads the retail data with a patch of Li01 in a new mod, with the engine.</summary>
    private static EngineLoadResult Load(string patch)
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = Patched(baseFolder, patch, out var view);
        var result = EngineDataLoader.Load(view);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
        return result;
    }
}
