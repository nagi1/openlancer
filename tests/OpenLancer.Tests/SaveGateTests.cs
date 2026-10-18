using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The format checks and the content checks of the retail targets in the save gate (section 9.2 of the M11 design). They
/// run for a tier-1 mod that names a retail target, and report only what the active mod adds.
/// </summary>
public sealed class SaveGateTests
{
    /// <summary>A workspace whose <c>freelancer.ini</c> names the goods file, with the mods "low" and "act".</summary>
    /// <param name="actManifest">More fields of a mod.json of format version 2 for "act", or empty for format
    /// version 1.</param>
    private static TestWorkspace Workspace(string actManifest = "", string lowPatch = "")
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n[Data]\ngoods = equipment\\goods.ini\n\n" +
                                            TestWorkspace.ResourcesSection);
        ws.WriteBase(GoodsPath, Goods);
        ws.Mod("low", lowPatch.Length == 0 ? new() : new() { [@"patches\" + GoodsPath] = lowPatch });
        ws.Mod("act", new());
        if (actManifest.Length > 0)
            File.WriteAllText(Path.Combine(ws.ModFolder("act"), "mod.json"),
                $$"""{ "id": "act", "formatVersion": 2 {{actManifest}} }""");
        ws.WriteProfile("low", "act");
        return ws;
    }

    private static string PatchFile(TestWorkspace ws) => Path.Combine(ws.ModFolder("act"), "patches", GoodsPath);

    [Fact]
    public void ACrashCheckBlocksTheSaveOfATier1ModWithARetailTarget()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(Set(Good("g2"), "price", new Int32Value(0)));
        var result = session.Save();

        Assert.False(result.Saved);
        var problem = Assert.Single(result.Problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.Equal("Good 'g2': the price is 0. The game can crash when a player buys or sells the good. Write a price " +
                     "above 0.", problem.Message);
        Assert.Equal(Good("g2"), problem.Target);
        Assert.False(File.Exists(PatchFile(ws)));

        session.Execute(Set(Good("g2"), "price", new Int32Value(250)));
        Assert.True(session.Save().Saved);
    }

    [Fact]
    public void AFormatErrorBlocksTheSave()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(Set(Good("g2"), "ids_name", new StringValue("a;b")));
        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Error &&
                                              p.Message.EndsWith("the value contains ';'. The game ignores the text after ';'."));
    }

    [Theory]
    [InlineData(""", "tier": 2""")]
    [InlineData(""", "targets": [ "openlancer" ]""")]
    public void AModWithoutARetailTargetSkipsTheRetailChecks(string manifest)
    {
        using var ws = Workspace(manifest);
        var session = ws.Session("act");

        session.Execute(Set(Good("g2"), "price", new Int32Value(0)));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Contains("price = 0", File.ReadAllText(PatchFile(ws)));
    }

    [Fact]
    public void AProblemOfALowerModDoesNotBlockTheSave()
    {
        using var ws = Workspace(lowPatch: "[Good]\nnickname = g1\nprice = 0\n");
        var session = ws.Session("act");

        session.Execute(Set(Good("g2"), "price", new Int32Value(250)));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.DoesNotContain(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);
    }
}
