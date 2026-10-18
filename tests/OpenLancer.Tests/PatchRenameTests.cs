using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The directive <c>ol.rename</c> of the merge (decision D231 of the M3 design): a patch section gives a section of a
/// lower layer a new key, and the section keeps its place.
/// </summary>
public sealed class PatchRenameTests
{
    private const string ShipsPath = @"DATA\ships.ini";

    private const string Ships = """
        [Ship]
        nickname = s1
        mass = 100
        hit_pts = 50

        [CollisionGroup]
        obj = s1_part

        [Ship]
        nickname = s2
        mass = 5

        [Simple]
        nickname = s3
        """;

    [Fact]
    public void ARenameKeepsThePlaceAndTheChildSectionsAndChangesTheOtherLines()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("[Ship]\nnickname = S1\nol.rename = s9\nmass = 120\nol.remove = hit_pts")));

        Assert.Empty(overlay.Diagnostics);
        var sections = TestWorkspace.Sections(overlay, ShipsPath);
        Assert.Equal(["Ship s9", "CollisionGroup s1_part", "Ship s2", "Simple s3"], Describe(sections));
        Assert.Equal(["nickname", "mass"], sections[0].Select(e => e.Name));
        Assert.Equal(120, sections[0]["mass"]![0].ToInt32());
    }

    [Fact]
    public void ALaterPatchOfTheOldKeyFailsAndOfTheNewKeyWorks()
    {
        using var ws = Workspace();
        var rename = ws.Mod("rename", Patch("[Ship]\nnickname = s1\nol.rename = s9"));
        var later = ws.Mod("later", Patch("[Ship]\nnickname = s9\nmass = 7\n\n[Ship]\nnickname = s1\nmass = 8"));

        var overlay = ws.Build(rename, later);

        var error = Assert.Single(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("[Ship] nickname = s1: no section to patch", error.Message);
        Assert.Equal(7, TestWorkspace.Sections(overlay, ShipsPath)[0]["mass"]![0].ToInt32());
    }

    /// <summary>A later section of the same patch finds the renamed section by its new key.</summary>
    [Fact]
    public void ALaterSectionOfThePatchFindsTheNewKey()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("[Ship]\nnickname = s1\nol.rename = s9\n\n[Ship]\nnickname = s9\nmass = 3")));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(3, TestWorkspace.Sections(overlay, ShipsPath)[0]["mass"]![0].ToInt32());
    }

    [Theory]
    [InlineData("[Ship]\nnickname = s1\nol.rename = s9\nol.new = true", "cannot be used with ol.new, ol.delete or ol.parent")]
    [InlineData("[Ship]\nnickname = s1\nol.rename = s9\nol.delete = true", "cannot be used with ol.new, ol.delete or ol.parent")]
    [InlineData("[Ship]\nnickname = s1\nol.rename = s9\nol.parent = s2", "cannot be used with ol.new, ol.delete or ol.parent")]
    [InlineData("[SystemInfo]\nol.rename = x", "needs a section type with a key line")]
    [InlineData("[CollisionGroup]\nnickname = c\nol.rename = d", "cannot rename a child section")]
    [InlineData("[Ship]\nnickname = s1\nol.rename = S1", "must differ from the old key in more than letter case")]
    [InlineData("[Ship]\nnickname = s1\nol.rename = s2", "another [Ship] section in DATA\\ships.ini has nickname = s2")]
    [InlineData("[Ship]\nnickname = s7\nol.rename = s9", "[Ship] nickname = s7: no section to rename")]
    [InlineData("[Ship]\nnickname = s1\nol.rename = s8\nol.rename = s9", "needs exactly one 'ol.rename' line")]
    [InlineData("[Ship]\nnickname = s1\nol.rename = a, b", "needs exactly one 'ol.rename' line")]
    [InlineData("[Ship]\nol.rename = s9\nmass = 1", "needs exactly one 'nickname' entry")]
    public void AnInvalidRenameIsAnErrorAndChangesNothing(string patch, string message)
    {
        using var ws = Workspace();
        ws.WriteBase(@"DATA\system.ini", "[SystemInfo]\nspace_color = 0, 0, 0");
        var mod = ws.Mod("m", new()
        {
            [@"patches\" + ShipsPath] = patch,
            [@"patches\DATA\system.ini"] = patch.StartsWith("[SystemInfo]") ? patch : "[SystemInfo]\nspace_color = 1, 1, 1",
        });

        var overlay = ws.Build(mod);

        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains(message));
        Assert.Equal(["Ship s1", "CollisionGroup s1_part", "Ship s2", "Simple s3"],
            Describe(TestWorkspace.Sections(overlay, ShipsPath)));
    }

    [Fact]
    public void TheLedgerRecordsADeleteAnAddAndTheRename()
    {
        var target = PatchWriterTests.Text(Ships);
        var ledger = new PatchLedger();
        var diagnostics = new List<Diagnostic>();
        var patch = PatchWriterTests.Text("[Ship]\nnickname = s1\nol.rename = s9\nmass = 1\n\n[Ship]\nnickname = s9\nol.rename = s10");

        IniPatcher.Apply(target, patch.Sections,
            new PatchContext("m", "patch.ini", ShipsPath, null, ledger, diagnostics, keepPlaceholders: true));

        Assert.Empty(diagnostics);
        var s1 = new SectionIdentity("Ship", "s1");
        var s10 = new SectionIdentity("Ship", "s10");
        // Two renames of one section give one pair.
        Assert.Equal([(s1, s10)], ledger.Renames(ShipsPath, "m"));
        Assert.Contains((s1, (string?)null), ledger.Changes(ShipsPath, "m"));
        Assert.Contains((new SectionIdentity("Ship", "s9"), (string?)"mass"), ledger.Changes(ShipsPath, "m"));
        Assert.Equal("m", ledger.Owner(ShipsPath, s1, "hit_pts"));
        Assert.Equal("s10", target.Sections[0]["nickname"]![0].ToString());
        // A rename back to the old key gives no pair.
        var back = new PatchLedger();
        IniPatcher.Apply(PatchWriterTests.Text(Ships),
            PatchWriterTests.Text("[Ship]\nnickname = s1\nol.rename = s9\n\n[Ship]\nnickname = s9\nol.rename = s1").Sections,
            new PatchContext("m", "patch.ini", ShipsPath, null, back, diagnostics, keepPlaceholders: true));
        Assert.Empty(back.Renames(ShipsPath, "m"));
    }

    /// <summary>A binary file gets the new key as a string, as a new key line of the editor holds it.</summary>
    [Fact]
    public void ABinaryFileKeepsAStringKey()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(ShipsPath, "[Ship]\nnickname = s1\nmass = 100\n");

        var overlay = ws.Build(ws.Mod("m", Patch("[Ship]\nnickname = s1\nol.rename = 600")));

        Assert.Empty(overlay.Diagnostics);
        var key = TestWorkspace.Sections(overlay, ShipsPath)[0]["nickname"]![0];
        Assert.IsType<StringValue>(key);
        Assert.Equal("600", key.ToString());
    }

    /// <summary>The section <c>[locked_gates]</c> of <c>InitialWorld.ini</c> is a singleton, so a patch can change its
    /// lines (decision D257 of the M3 design). The text file keeps each hash as its exact number.</summary>
    [Fact]
    public void APatchChangesTheLockedGates()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\initialworld.ini", "[locked_gates]\nlocked_gate = 2926089285\nnpc_locked_gate = 12\n");

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\initialworld.ini"] = "[locked_gates]\nlocked_gate = 3000000000\n",
        }));

        Assert.Empty(overlay.Diagnostics);
        var gates = TestWorkspace.Sections(overlay, @"DATA\initialworld.ini").Single();
        Assert.Equal(unchecked((int)3000000000L), gates["locked_gate"]![0].ToInt32());
        Assert.Equal(12, gates["npc_locked_gate"]![0].ToInt32());
        Assert.Equal(SectionMatchKind.Singleton, SectionKeys.For("locked_gates").Kind);
    }

    private static TestWorkspace Workspace()
    {
        var ws = new TestWorkspace();
        ws.WriteBase(ShipsPath, Ships);
        return ws;
    }

    private static Dictionary<string, string> Patch(string text) => new() { [@"patches\" + ShipsPath] = text };

    private static string[] Describe(IEnumerable<Section> sections) => sections.Select(s => $"{s.Name} {s[0][0]}").ToArray();
}
