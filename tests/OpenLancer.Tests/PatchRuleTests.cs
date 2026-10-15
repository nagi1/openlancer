using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rules that keep patched data equal to what the engine expects: value types in binary files, linked lines,
/// child sections, and conflict reports.
/// </summary>
public sealed class PatchRuleTests
{
    [Fact]
    public void ALargeNumberInABinaryFileStaysAFloat()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", "[Ship]\nnickname = s1\nangular_drag = 4000000000.0, 1.0, 1.0");

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nangular_drag = 4000000000, 4000000000, 4000000000",
        }));

        Assert.Empty(overlay.Diagnostics);
        var drag = TestWorkspace.Sections(overlay, @"DATA\ships.ini").Single()["angular_drag"]!;
        Assert.All(drag, v => Assert.Equal(4e9f, v.ToSingle()));
    }

    [Fact]
    public void ANewLargeIntegerInABinaryFileIsAnError()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", "[Ship]\nnickname = s1");

        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nhash = 4000000000" }));

        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("cannot store this value exactly"));
    }

    [Fact]
    public void ALargeIntegerAtTheLimitsOfTheHashRangeInABinaryFile()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", BiniSection("Ship",
            ("nickname", "s1"), ("below", 1), ("top", 1), ("above", 1)));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nbelow = -2147483649\ntop = 4294967295\nabove = 4294967296",
        }));

        // Only 2147483648 to 4294967295 over an Int32 is a hash. The file stores its 32 bits.
        var ship = TestWorkspace.Sections(overlay, @"DATA\ships.ini").Single();
        Assert.Equal(-1, Assert.IsType<Int32Value>(ship["top"]!.Single()).ToInt32());
        Assert.Equal(2, overlay.Diagnostics.Count);
        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error &&
                                                  d.Message.StartsWith("'below = -2147483649': a binary INI file cannot store this value exactly. Write -2147483649.0 for a number."));
        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error &&
                                                  d.Message.StartsWith("'above = 4294967296': a binary INI file cannot store this value exactly. Write 4294967296.0 for a number."));
    }

    [Theory]
    [InlineData("-2147483649")]
    [InlineData("-2147483648")]
    [InlineData("2147483648")]
    [InlineData("4294967295")]
    [InlineData("4294967296")]
    [InlineData("9223372036854775807")]
    public void ATextFileKeepsTheExactTextOfALargeInteger(string number)
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\world.ini", "[Group]\nnickname = g1\nbase = " + number + "\nrep = 0.5");

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\world.ini"] = "[Group]\nnickname = g1\nrep = 0.25\npatched = " + number,
        }));

        Assert.Empty(overlay.Diagnostics);
        using var stream = overlay.CreateFileSystem().Open(@"DATA\world.ini");
        var lines = new StreamReader(stream).ReadToEnd().Split("\r\n");
        Assert.Contains("base = " + number, lines);
        Assert.Contains("patched = " + number, lines);
    }

    [Fact]
    public void ALargeIntegerThatAFloatCannotHoldGivesAWarning()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", BiniSection("Ship",
            ("nickname", "s1"), ("hash", 1f), ("negative", 1f), ("exact", 1f)));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nhash = 2314954114\nnegative = -3000000001\nexact = 4294967296",
        }));

        // The file stores the float. A float holds 4294967296 exactly, so only two values give a warning.
        var ship = TestWorkspace.Sections(overlay, @"DATA\ships.ini").Single();
        Assert.Equal(2314954240f, Assert.IsType<SingleValue>(ship["hash"]!.Single()).ToSingle());
        Assert.Equal(4294967296f, Assert.IsType<SingleValue>(ship["exact"]!.Single()).ToSingle());
        Assert.All(overlay.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Equal(
        [
            "'hash = 2314954114': the value replaces a float, and a float cannot hold this number. " +
            "The file stores 2314954240. Write 2314954240.0 for that number, or -1980013182 for a hash.",
            "'negative = -3000000001': the value replaces a float, and a float cannot hold this number. " +
            "The file stores -3000000000. Write -3000000000.0 for that number.",
        ], overlay.Diagnostics.Select(d => d.Message));
    }

    [Fact]
    public void ABooleanThatReplacesAStringInABinaryFileStaysAString()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", BiniSection("Ship",
            ("nickname", "s1"), ("same", "TRUE"), ("changed", "true"), ("text", "li_gun"), ("yes", "yes")));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nsame = true\nchanged = false\ntext = true\nyes = true",
        }));

        Assert.Empty(overlay.Diagnostics);
        var ship = TestWorkspace.Sections(overlay, @"DATA\ships.ini").Single();
        // The same truth value keeps the original text.
        Assert.Equal("TRUE", Assert.IsType<StringValue>(ship["same"]!.Single()).ToString());
        Assert.Equal("false", Assert.IsType<StringValue>(ship["changed"]!.Single()).ToString());
        Assert.Equal("true", Assert.IsType<StringValue>(ship["text"]!.Single()).ToString());
        // Only true and false in any letter case are kept. The engine also reads "yes" as true.
        Assert.Equal("true", Assert.IsType<StringValue>(ship["yes"]!.Single()).ToString());
    }

    [Fact]
    public void ANewBooleanInABinaryFileBecomesLowercaseText()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", BiniSection("Ship", ("nickname", "s1"), ("flag", "TRUE")));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nflag = true, TRUE\nadded = FALSE\n\n" +
                                          "[Ship]\nnickname = s2\nol.new = true\nlootable = true",
        }));

        Assert.Empty(overlay.Diagnostics);
        var ships = TestWorkspace.Sections(overlay, @"DATA\ships.ini");
        // Retail files store no booleans. Only the first value replaces a value, so only it keeps the old text.
        Assert.Equal(["TRUE", "true"], ships[0]["flag"]!.Select(v => Assert.IsType<StringValue>(v).ToString()));
        Assert.Equal("false", Assert.IsType<StringValue>(ships[0]["added"]!.Single()).ToString());
        Assert.Equal("true", Assert.IsType<StringValue>(ships[1]["lootable"]!.Single()).ToString());
    }

    [Fact]
    public void AWholeNumberThatReplacesAFloatInABinaryFileStaysAFloatWhenTheFloatHoldsItExactly()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", BiniSection("Ship",
            ("nickname", "s1"), ("hit_pts", 100.5f), ("limit", 1f), ("negative", 1f), ("above", 1f), ("below", 1f),
            ("hash", 1f), ("count", 5)));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nhit_pts = 1000\nlimit = 16777216\n" +
                                          "negative = -16777216\nabove = 16777217\nbelow = -16777217\n" +
                                          "hash = -1980013182\ncount = 7",
        }));

        Assert.Empty(overlay.Diagnostics);
        var ship = TestWorkspace.Sections(overlay, @"DATA\ships.ini").Single();
        Assert.Equal(1000f, Assert.IsType<SingleValue>(ship["hit_pts"]!.Single()).ToSingle());
        Assert.Equal(16777216f, Assert.IsType<SingleValue>(ship["limit"]!.Single()).ToSingle());
        Assert.Equal(-16777216f, Assert.IsType<SingleValue>(ship["negative"]!.Single()).ToSingle());
        // A float cannot hold these numbers, so they stay Int32 values. Half of all hashes are negative.
        Assert.Equal(16777217, Assert.IsType<Int32Value>(ship["above"]!.Single()).ToInt32());
        Assert.Equal(-16777217, Assert.IsType<Int32Value>(ship["below"]!.Single()).ToInt32());
        Assert.Equal(-1980013182, Assert.IsType<Int32Value>(ship["hash"]!.Single()).ToInt32());
        Assert.Equal(7, Assert.IsType<Int32Value>(ship["count"]!.Single()).ToInt32());
    }

    [Fact]
    public void TheNthPatchLineOfANameReplacesTheNthTargetLine()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini",
            BiniSection("Ship", ("nickname", "s1"), ("x", 1.5f), ("x", "true")),
            BiniSection("Ship", ("nickname", "s2"), ("x", "true"), ("x", 1.5f), ("x", 2.5f)));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nx = 5\nx = true\n\n" +
                                          "[Ship]\nnickname = s2\nx = true\nx = 5\nx = -1980013182",
        }));

        Assert.Empty(overlay.Diagnostics);
        var ships = TestWorkspace.Sections(overlay, @"DATA\ships.ini");
        var first = ships[0].Where(e => e.Name == "x").Select(e => e.Single()).ToList();
        Assert.Equal(5f, Assert.IsType<SingleValue>(first[0]).ToSingle());
        Assert.Equal("true", Assert.IsType<StringValue>(first[1]).ToString());
        var second = ships[1].Where(e => e.Name == "x").Select(e => e.Single()).ToList();
        Assert.Equal("true", Assert.IsType<StringValue>(second[0]).ToString());
        Assert.Equal(5f, Assert.IsType<SingleValue>(second[1]).ToSingle());
        // The third line replaces a float, but a float cannot hold the hash.
        Assert.Equal(-1980013182, Assert.IsType<Int32Value>(second[2]).ToInt32());
    }

    [Fact]
    public void EachPatchValueReplacesTheValueAtTheSamePosition()
    {
        using var ws = new TestWorkspace();
        var ship = BiniSection("Ship", ("nickname", "s1"));
        ship.Add(new Entry(ship, "mixed") { 1.5f, "true", 7 });
        ws.WriteBaseBini(@"DATA\ships.ini", ship);

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nmixed = 5, true, 8",
        }));

        Assert.Empty(overlay.Diagnostics);
        var values = TestWorkspace.Sections(overlay, @"DATA\ships.ini").Single()["mixed"]!;
        Assert.Equal(5f, Assert.IsType<SingleValue>(values[0]).ToSingle());
        Assert.Equal("true", Assert.IsType<StringValue>(values[1]).ToString());
        Assert.Equal(8, Assert.IsType<Int32Value>(values[2]).ToInt32());
    }

    [Fact]
    public void ANewValueInABinaryFileKeepsTheTypeOfItsText()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", BiniSection("Ship", ("nickname", "s1"), ("ids", 5)));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nids = 5, 6.5\nmass = 100\nratio = 0.5\nhull = li_hull",
        }));

        Assert.Empty(overlay.Diagnostics);
        var ship = TestWorkspace.Sections(overlay, @"DATA\ships.ini").Single();
        // The second value of ids has no replaced value that gives its type.
        Assert.IsType<Int32Value>(ship["ids"]![0]);
        Assert.IsType<SingleValue>(ship["ids"]![1]);
        Assert.IsType<Int32Value>(ship["mass"]!.Single());
        Assert.IsType<SingleValue>(ship["ratio"]!.Single());
        Assert.IsType<StringValue>(ship["hull"]!.Single());
    }

    [Fact]
    public void ALineOfABinaryFileHoldsAtMost255Values()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", BiniSection("Ship", ("nickname", "s1")));
        var values = string.Join(", ", Enumerable.Range(0, 256));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nfull = " + values + "\n\n[Ship]\nnickname = s2\nol.new = true\nfull = " + values,
        }));

        Assert.Equal(2, overlay.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error &&
                                                     d.Message.Contains("holds at most 255 values, not 256")));
        // The merged file stays readable.
        Assert.All(TestWorkspace.Sections(overlay, @"DATA\ships.ini"), s => Assert.Equal(255, s["full"]!.Count));
    }

    [Fact]
    public void LinkedLinesMustBeChangedTogether()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\sys.ini", "[Zone]\nnickname = z1\nencounter = a, 5, 0.4\nfaction = fa, 1\nencounter = b, 5, 0.2\nfaction = fb, 1");

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\sys.ini"] = "[Zone]\nnickname = z1\nencounter = a, 10, 0.4\nencounter = b, 10, 0.2",
        }));

        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("belong together"));
        // On the error the section stays unchanged.
        var zone = TestWorkspace.Sections(overlay, @"DATA\sys.ini").Single();
        Assert.Equal(["a", "fa", "b", "fb"], zone.Where(e => e.Name is "encounter" or "faction").Select(e => e[0].ToString()));
    }

    [Fact]
    public void FactionWeightLinesBelongToTheEncounterBeforeThem()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\sys.ini",
            "[Zone]\nnickname = z1\nencounter = a, 5, 0.4\nfaction = fa, 1\nfaction_weight = fa, 2\nencounter = b, 5, 0.2\nfaction = fb, 1");

        // Without the faction_weight line, the block would give its weight to the last encounter.
        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\sys.ini"] = "[Zone]\nnickname = z1\nencounter = a, 10, 0.4\nfaction = fa, 1\nencounter = b, 10, 0.2\nfaction = fb, 1",
        }));

        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("the encounter, faction, faction_weight lines belong together") &&
            d.Message.EndsWith("so it must also list faction_weight."));
    }

    [Fact]
    public void KeepPlaceholdersModeKeepsThePlaceholderTextWithNoError()
    {
        var patch = "[Ship]\nnickname = s1\nids_name = $str:name\n\n[Ship]\nol.new = true\nnickname = s2\nids_info = $ifc:info";
        var kept = new List<Diagnostic>();
        var resolved = new List<Diagnostic>();
        var document = BiniDocument(BiniSection("Ship", ("nickname", "s1"), ("ids_name", 5)));
        var other = document.Clone();

        IniPatcher.Apply(document, ParseText(patch), Context(kept, keepPlaceholders: true));
        IniPatcher.Apply(other, ParseText(patch), Context(resolved, keepPlaceholders: false));

        Assert.Empty(kept);
        Assert.Equal("$str:name", Assert.IsAssignableFrom<StringValue>(document.Sections[0]["ids_name"]!.Single()).ToString());
        Assert.Equal("$ifc:info", Assert.IsAssignableFrom<StringValue>(document.Sections[1]["ids_info"]!.Single()).ToString());
        // Without the mode, a file without text ids cannot use placeholders.
        Assert.Equal(2, resolved.Count(d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("text placeholders cannot be used")));
    }

    /// <summary>
    /// The editor and the patch writer's check apply patches to a copy of the section list only. That is safe only
    /// when the merge never changes a section or an entry in place.
    /// </summary>
    [Fact]
    public void TheMergeNeverChangesASectionInPlace()
    {
        var document = IniDocument.Parse(@"DATA\x.ini", Encoding.ASCII.GetBytes("""
            [Good]
            nickname = a
            price = 1
            addon = x, internal, 1
            [Ship]
            nickname = s
            [CollisionGroup]
            obj = c
            [Ship]
            nickname = t
            [MBase]
            nickname = b1
            [GF_NPC]
            nickname = n1
            """.Replace("\r\n", "\n")));
        var sections = document.Sections.ToList();
        var entries = sections.Select(s => s.Select(e => (Entry: e, Values: e.ToList())).ToList()).ToList();
        var copy = document.WithSections(document.Sections);
        var diagnostics = new List<Diagnostic>();

        IniPatcher.Apply(copy, ParseText("""
            [Good]
            nickname = a
            price = 2
            ol.remove = addon
            [Ship]
            nickname = t
            ol.delete = true
            [Ship]
            nickname = s
            ol.delete = true
            [Ship]
            ol.new = true
            nickname = u
            [CollisionGroup]
            obj = d
            [GF_NPC]
            ol.new = true
            ol.parent = b1
            nickname = n2
            """), Context(diagnostics, keepPlaceholders: false));

        Assert.Empty(diagnostics);
        Assert.Equal(["Good", "MBase", "GF_NPC", "GF_NPC", "Ship", "CollisionGroup"], copy.Sections.Select(s => s.Name));
        Assert.Equal(sections, document.Sections);
        for (var i = 0; i < sections.Count; i++)
        {
            Assert.Equal(entries[i].Select(e => e.Entry), sections[i]);
            foreach (var (entry, values) in entries[i])
                Assert.Equal(values, entry);
        }
    }

    [Fact]
    public void DeletingAFuseRemovesAllItsSubSections()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\fuse.ini", """
            [fuse]
            name = first

            [destroy_group]
            group_name = g1

            [fuse]
            name = second

            [start_effect]
            effect = e2

            [damage_group]
            hardpoint = h2

            [destroy_root]
            at_t = 1
            """);

        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\fuse.ini"] = "[fuse]\nname = second\nol.delete = true" }));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["fuse", "destroy_group"], TestWorkspace.Sections(overlay, @"DATA\fuse.ini").Select(s => s.Name));
    }

    private const string Bases = """
        [MBase]
        nickname = b1

        [GF_NPC]
        nickname = npc1

        [MBase]
        nickname = b2

        [GF_NPC]
        nickname = npc2
        """;

    [Fact]
    public void ANewChildMustNotRepeatAKeyUnderItsParent()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\mbases.ini", Bases);

        var duplicate = ws.Build(ws.Mod("dup", new()
        {
            [@"patches\DATA\mbases.ini"] = "[GF_NPC]\nol.new = true\nol.parent = b1\nnickname = npc1",
        }));
        var sameKeyOtherParent = ws.Build(ws.Mod("other", new()
        {
            [@"patches\DATA\mbases.ini"] = "[GF_NPC]\nol.new = true\nol.parent = b2\nnickname = npc1",
        }));

        Assert.Contains(duplicate.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("already has this section"));
        Assert.Empty(sameKeyOtherParent.Diagnostics);
    }

    [Fact]
    public void TwoModsThatAddChildrenDoNotConflict()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\mbases.ini", Bases);
        var a = ws.Mod("a", new() { [@"patches\DATA\mbases.ini"] = "[GF_NPC]\nol.new = true\nol.parent = b1\nnickname = from_a" });
        var b = ws.Mod("b", new() { [@"patches\DATA\mbases.ini"] = "[GF_NPC]\nol.new = true\nol.parent = b1\nnickname = from_b" });

        var overlay = ws.Build(a, b);

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["b1", "npc1", "from_a", "from_b", "b2", "npc2"],
            TestWorkspace.Sections(overlay, @"DATA\mbases.ini").Select(s => s["nickname"]![0].ToString()));
    }

    [Fact]
    public void AWholeFileNamesEveryModWhoseChangesItDiscards()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\goods.ini", "[Good]\nnickname = a\nprice = 1");
        var first = ws.Mod("first", new() { [@"patches\DATA\goods.ini"] = "[Good]\nnickname = a\nprice = 2" });
        var second = ws.Mod("second", new() { [@"patches\DATA\goods.ini"] = "[Good]\nnickname = a\nids_name = 3" });
        var replacer = ws.Mod("replacer", new() { [@"files\DATA\goods.ini"] = "[Good]\nnickname = a\nprice = 4" });

        var overlay = ws.Build(first, second, replacer);

        Assert.Contains(overlay.Diagnostics, d => d.Message.Contains("mod 'first'") && d.Message.Contains("mod 'second'"));
    }

    [Fact]
    public void EngineLinesFromTextAndBinaryFilesNormalizeToOneForm()
    {
        IReadOnlyList<string> lines;
        using (var capture = EngineLogCapture.Start())
        {
            FLLog.Warning("Ini", "Unknown entry x at section Ship: a.ini, line 12");
            FLLog.Warning("Ini", "Unknown entry x at section Ship: a.ini, line 40");
            FLLog.Warning("Ini", "Unknown entry x in section Ship: a.ini (line not available)");
            lines = capture.Stop();
        }

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal("Warning Ini: Unknown entry x in section Ship: a.ini", l));
    }

    private static PatchContext Context(List<Diagnostic> diagnostics, bool keepPlaceholders) =>
        new("m", "patch.ini", @"DATA\x.ini", null, new PatchLedger(), diagnostics, keepPlaceholders);

    private static List<Section> ParseText(string text) =>
        IniFile.ParseFile("patch.ini", new MemoryStream(Encoding.ASCII.GetBytes(text.Replace("\r\n", "\n")))).ToList();

    private static IniDocument BiniDocument(params Section[] sections)
    {
        using var stream = new MemoryStream();
        IniWriter.WriteBini(stream, sections);
        return IniDocument.Parse(@"DATA\x.ini", stream.ToArray());
    }

    private static Section BiniSection(string name, params (string Name, ValueBase Value)[] entries)
    {
        var section = new Section(name);
        foreach (var (entryName, value) in entries)
            section.Add(new Entry(section, entryName) { value });
        return section;
    }
}
