using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

public sealed class OverlayPatchTests
{
    private const string GoodsPath = @"DATA\goods.ini";

    private const string Goods = """
        [Good]
        nickname = alpha
        price = 100
        addon = eng, internal, 1
        addon = gun, HpWeapon01, 1
        ids_name = 5

        [Good]
        nickname = beta
        price = 200
        """;

    [Fact]
    public void WritesTheChangedEntriesAsOneBlockInPatchOrder()
    {
        using var ws = Workspace();
        var mod = ws.Mod("m", Patch("""
            [Good]
            nickname = ALPHA
            addon = eng2, internal, 1
            addon = gun2, HpWeapon02, 1
            addon = gun2, HpWeapon03, 1
            price = 150
            """));

        var overlay = ws.Build(mod);

        Assert.Empty(overlay.Diagnostics);
        var alpha = Section(overlay, "alpha");
        // The block replaces 'price' and all 'addon' lines at the position of the first one ('price').
        Assert.Equal(["nickname", "addon", "addon", "addon", "price", "ids_name"], alpha.Select(e => e.Name));
        Assert.Equal(150, alpha["price"]![0].ToInt32());
        Assert.Equal(["eng2", "gun2", "gun2"], alpha.Where(e => e.Name == "addon").Select(e => e[0].ToString()));
        Assert.Equal(200, Section(overlay, "beta")["price"]![0].ToInt32());
    }

    [Fact]
    public void KeepsLinkedLinesInPatchOrder()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\sys.ini", """
            [Zone]
            nickname = z1
            shape = SPHERE
            encounter = area_old, 5, 0.4
            faction = old_grp, 1
            density = 5
            """);
        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\sys.ini"] = """
                [Zone]
                nickname = z1
                encounter = area_a, 5, 0.4
                faction = a_grp, 1
                encounter = area_b, 5, 0.2
                faction = b_grp, 1
                """,
        }));

        Assert.Empty(overlay.Diagnostics);
        var zone = TestWorkspace.Sections(overlay, @"DATA\sys.ini").Single();
        Assert.Equal(["nickname", "shape", "encounter", "faction", "encounter", "faction", "density"],
            zone.Select(e => e.Name));
        Assert.Equal(["area_a", "a_grp", "area_b", "b_grp"],
            zone.Where(e => e.Name is "encounter" or "faction").Select(e => e[0].ToString()));
    }

    private const string ShipsWithChildren = """
        [Ship]
        nickname = s1

        [CollisionGroup]
        obj = s1_part

        [Ship]
        nickname = s2

        [CollisionGroup]
        obj = s2_part
        """;

    [Fact]
    public void DeletingAParentAlsoDeletesItsChildSections()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\ships.ini", ShipsWithChildren);
        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\ships.ini"] = "[Ship]\nnickname = s1\nol.delete = true" }));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["Ship s2", "CollisionGroup s2_part"], Describe(overlay, @"DATA\ships.ini"));
    }

    [Fact]
    public void ANewChildGoesAfterTheChildrenOfItsParent()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\ships.ini", ShipsWithChildren);
        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\ships.ini"] = """
                [CollisionGroup]
                ol.new = true
                ol.parent = s1
                obj = s1_extra

                [Ship]
                ol.new = true
                nickname = s3

                [CollisionGroup]
                obj = s3_part
                """,
        }));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(
            ["Ship s1", "CollisionGroup s1_part", "CollisionGroup s1_extra", "Ship s2", "CollisionGroup s2_part",
                "Ship s3", "CollisionGroup s3_part"],
            Describe(overlay, @"DATA\ships.ini"));
    }

    [Fact]
    public void ANewChildWithoutParentIsAnError()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\ships.ini", ShipsWithChildren);
        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\ships.ini"] = "[CollisionGroup]\nol.new = true\nobj = x" }));

        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("ol.parent"));
    }

    [Fact]
    public void FreelancerIniCannotBePatched()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\EXE\freelancer.ini"] = "[Resources]\nDLL = x.dll" }));

        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("cannot be patched"));
    }

    [Fact]
    public void ResolvesPlaceholdersInWholeIniFiles()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"files\DATA\MODS\m\goods.ini"] = "[Good]\nnickname = gamma\nids_name = $str:name",
            ["strings.json"] = """{ "strings": { "name": "Gamma" } }""",
        }));

        Assert.Empty(overlay.Diagnostics);
        var gamma = TestWorkspace.Sections(overlay, @"DATA\MODS\m\goods.ini").Single();
        Assert.Equal(overlay.StringIds.DllIndex * 65536 + 1, gamma["ids_name"]![0].ToInt32());
    }

    [Fact]
    public void AWholeFileThatDiscardsAnotherModsChangesIsReported()
    {
        using var ws = Workspace();
        var patcher = ws.Mod("patcher", Patch("[Good]\nnickname = beta\nprice = 3"));
        var replacer = ws.Mod("replacer", new() { [@"files\DATA\goods.ini"] = "[Good]\nnickname = beta\nprice = 4" });
        var later = ws.Mod("later", Patch("[Good]\nnickname = beta\nprice = 5"));

        var overlay = ws.Build(patcher, replacer, later);

        Assert.Contains(overlay.Diagnostics, d => d.Message.Contains("'replacer' replaces") && d.Message.Contains("'patcher'"));
        // The conflict ledger forgets the discarded change, so the last patch names no lost mod.
        Assert.DoesNotContain(overlay.Diagnostics, d => d.Message.Contains("also changed by mod 'patcher'"));
        Assert.Equal(5, Section(overlay, "beta")["price"]![0].ToInt32());
    }

    [Fact]
    public void KeepsHashValuesAboveTheInt32RangeInARewrittenTextFile()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\world.ini", "[Group]\nnickname = g1\nlocked_gate = 2926089285\nrep = 0.5, x");

        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\world.ini"] = "[Group]\nnickname = g1\nrep = 0.25, x" }));

        Assert.Empty(overlay.Diagnostics);
        var group = TestWorkspace.Sections(overlay, @"DATA\world.ini").Single();
        Assert.Equal(unchecked((int)2926089285L), group["locked_gate"]![0].ToInt32());
    }

    [Fact]
    public void KeepsAPatchHashValueAboveTheInt32RangeInABinaryFile()
    {
        using var ws = new TestWorkspace();
        var path = System.IO.Path.Combine(ws.Base, "DATA", "world.ini");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var stream = System.IO.File.Create(path))
        {
            var text = "[Group]\nnickname = g1\nlocked_gate = 1"u8.ToArray();
            IniWriter.WriteBini(stream, IniFile.ParseFile("x", new System.IO.MemoryStream(text)).ToList());
        }

        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\world.ini"] = "[Group]\nnickname = g1\nlocked_gate = 2926089285" }));

        Assert.Empty(overlay.Diagnostics);
        var group = TestWorkspace.Sections(overlay, @"DATA\world.ini").Single();
        Assert.Equal(unchecked((int)2926089285L), group["locked_gate"]![0].ToInt32());
    }

    [Fact]
    public void ListsFoldersThatOnlyAModAdds()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", new() { [@"files\DATA\SHIPS\OL_NEW\model.txt"] = "x" }));
        var fs = overlay.CreateFileSystem();

        Assert.Contains("OL_NEW", fs.GetDirectories(@"DATA\SHIPS"), System.StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["model.txt"], fs.GetFiles(@"DATA\SHIPS\OL_NEW"));
        Assert.Empty(fs.GetDirectories(@"DATA\SHIPS\OL_NEW"));
    }

    [Fact]
    public void AWholeFileOverrideReportsItsModFileAsBackingFile()
    {
        using var ws = Workspace();
        var mod = ws.Mod("m", new() { [@"files\DATA\goods.ini"] = "[Good]\nnickname = only" });
        var overlay = ws.Build(mod);

        Assert.Equal(System.IO.Path.Combine(mod.Folder, "files", "DATA", "goods.ini"),
            overlay.CreateFileSystem().GetBackingFileName(@"DATA\goods.ini"));
    }

    [Fact]
    public void WithoutResourcesDllTheFirstListedDllIsIndexZero()
    {
        using var ws = Workspace();
        System.IO.File.Delete(System.IO.Path.Combine(ws.Base, "EXE", "resources.dll"));
        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\goods.ini"] = "[Good]\nnickname = beta\nids_name = $str:n",
            ["strings.json"] = """{ "strings": { "n": "N" } }""",
        }));

        // InfoCards.dll is index 0, so the generated DLL is index 1.
        Assert.Equal(1, overlay.StringIds.DllIndex);
    }

    [Fact]
    public void ADllThatAModAddsCountsForTheTextIndex()
    {
        using var ws = Workspace();
        var dllMod = ws.Mod("dlls", new()
        {
            [@"files\EXE\freelancer.ini"] = "[Freelancer]\ndata path = ..\\data\n\n" + TestWorkspace.ResourcesSection + "\nDLL = moddll.dll",
            [@"files\EXE\moddll.dll"] = "not a real dll",
        });
        var textMod = ws.Mod("text", new()
        {
            [@"patches\DATA\goods.ini"] = "[Good]\nnickname = beta\nids_name = $str:n",
            ["strings.json"] = """{ "strings": { "n": "N" } }""",
        });

        var overlay = ws.Build(dllMod, textMod);

        Assert.Empty(overlay.Diagnostics);
        // resources.dll (0), InfoCards.dll (1), moddll.dll (2). Missing.dll is skipped.
        Assert.Equal(3, overlay.StringIds.DllIndex);
    }

    [Fact]
    public void AddsAMissingKeyAtTheEnd()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("[Good]\nnickname = beta\nitem_icon = x.3db")));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["nickname", "price", "item_icon"], Section(overlay, "beta").Select(e => e.Name));
    }

    [Fact]
    public void RemovesAndDeletes()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("""
            [Good]
            nickname = alpha
            ol.remove = addon, ids_name

            [Good]
            nickname = beta
            ol.delete = true
            """)));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["nickname", "price"], Section(overlay, "alpha").Select(e => e.Name));
        Assert.DoesNotContain(TestWorkspace.Sections(overlay, GoodsPath), s => Key(s) == "beta");
    }

    [Fact]
    public void AddsANewSection()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("[Good]\nol.new = true\nnickname = gamma\nprice = 7")));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["alpha", "beta", "gamma"], TestWorkspace.Sections(overlay, GoodsPath).Select(Key));
        Assert.Equal(["nickname", "price"], Section(overlay, "gamma").Select(e => e.Name));
    }

    [Theory]
    [InlineData("[Good]\nnickname = missing\nprice = 1", "no section to patch")]
    [InlineData("[Good]\nol.new = true\nnickname = alpha", "already exists")]
    [InlineData("[Good]\nprice = 1", "needs exactly one 'nickname'")]
    [InlineData("[Good]\nnickname = alpha\nol.renmae = x", "unknown directive")]
    [InlineData("[Good]\nnickname = alpha\nol.delete = true\nprice = 1", "cannot be used with other entries")]
    [InlineData("[Good]\nnickname = alpha\nids_name = $str:nothing", "not in m's strings.json")]
    public void ReportsBadPatches(string patch, string expected)
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch(patch)));

        Assert.True(overlay.HasErrors);
        Assert.Contains(overlay.Diagnostics, d => d.Message.Contains(expected));
    }

    [Fact]
    public void ReportsAPatchForAFileThatDoesNotExist()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\nothere.ini"] = "[Good]\nnickname = a" }));

        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("does not exist"));
    }

    [Fact]
    public void ReportsTwoSectionsWithTheSameKey()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, "[Good]\nnickname = twin\n\n[Good]\nnickname = twin");
        var overlay = ws.Build(ws.Mod("m", Patch("[Good]\nnickname = twin\nprice = 1")));

        Assert.Contains(overlay.Diagnostics, d => d.Message.Contains("matches 2 sections"));
    }

    [Fact]
    public void AppendsBaseGoodSections()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\market.ini", "[BaseGood]\nbase = b1\nmarketgood = x, 0, -1, 1, 1, 0, 1");
        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\market.ini"] = "[BaseGood]\nbase = b1\nmarketgood = y, 0, -1, 1, 1, 0, 1",
        }));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(2, TestWorkspace.Sections(overlay, @"DATA\market.ini").Count(s => s.Name == "BaseGood"));
    }

    [Fact]
    public void ALaterModWinsAndTheConflictIsReported()
    {
        using var ws = Workspace();
        var first = ws.Mod("first", Patch("[Good]\nnickname = beta\nprice = 1"));
        var second = ws.Mod("second", Patch("[Good]\nnickname = beta\nprice = 2"));

        var overlay = ws.Build(first, second);

        Assert.False(overlay.HasErrors);
        Assert.Equal(2, Section(overlay, "beta")["price"]![0].ToInt32());
        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("'first'"));
    }

    [Fact]
    public void AWholeFileReplacesTheBaseAndLaterPatchesApplyToIt()
    {
        using var ws = Workspace();
        var replacer = ws.Mod("replacer", new() { [@"files\DATA\goods.ini"] = "[Good]\nnickname = only\nprice = 9" });
        var patcher = ws.Mod("patcher", Patch("[Good]\nnickname = only\nprice = 10"));

        var overlay = ws.Build(replacer, patcher);

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["only"], TestWorkspace.Sections(overlay, GoodsPath).Select(Key));
        Assert.Equal(10, Section(overlay, "only")["price"]![0].ToInt32());
    }

    [Fact]
    public void ResolvesTextPlaceholdersAndRegistersTheDll()
    {
        using var ws = Workspace();
        var mod = ws.Mod("m", new()
        {
            [@"patches\DATA\goods.ini"] = "[Good]\nnickname = beta\nids_name = $str:name\nids_info = $ifc:info",
            ["strings.json"] = """{ "strings": { "name": "Beta" }, "infocards": { "info": "<RDL/>" } }""",
        });

        var overlay = ws.Build(mod);

        Assert.Empty(overlay.Diagnostics);
        // resources.dll is index 0 and InfoCards.dll index 1. Missing.dll does not exist, so it is skipped.
        Assert.Equal(2, overlay.StringIds.DllIndex);
        var beta = Section(overlay, "beta");
        Assert.Equal(2 * 65536 + 1, beta["ids_name"]![0].ToInt32());
        Assert.Equal(2 * 65536 + 2, beta["ids_info"]![0].ToInt32());

        var resources = TestWorkspace.Sections(overlay, ModOverlay.FreelancerIniPath).Last(s => s.Name == "Resources");
        Assert.Equal("openlancer_strings.dll", resources.Last()[0].ToString());
        Assert.Contains(overlay.Files, f => f.Path == ModOverlay.StringsDllPath);
    }

    [Fact]
    public void WithoutModsTheOverlayIsEmpty()
    {
        using var ws = Workspace();
        var overlay = ws.Build();

        Assert.Empty(overlay.Files);
        Assert.Empty(overlay.Diagnostics);
    }

    [Fact]
    public void TheFileSystemFindsMergedFilesWithEnginePaths()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("[Good]\nnickname = beta\nprice = 3")));
        var fs = overlay.CreateFileSystem();

        Assert.True(fs.FileExists(@"EXE\..\data\goods.ini"));
        Assert.Contains("goods.ini", fs.GetFiles(@"DATA"), System.StringComparer.OrdinalIgnoreCase);
        // A merged file has no backing file, so a tool cannot save it in place.
        Assert.Null(fs.GetBackingFileName(@"DATA\goods.ini"));
        // An unchanged file reports its base file. Only its read-only attribute protects it.
        Assert.NotNull(fs.GetBackingFileName(@"EXE\InfoCards.dll"));
    }

    /// <summary>
    /// The editor needs the exact input of the active mod's patch. A parse of a merged file is not that input.
    /// </summary>
    [Fact]
    public void AKeptDocumentIsTheExactInputOfTheNextPatch()
    {
        using var ws = Workspace();
        ws.WriteBase(@"DATA\world.ini", "[Group]\nnickname = g1\nrep = 0.5");
        var lower = ws.Mod("lower", new()
        {
            [@"patches\DATA\goods.ini"] = "[Good]\nnickname = beta\nvolume = 100.0",
            [@"files\DATA\whole.ini"] = "[Group]\nnickname = w1",
        });

        var overlay = ModOverlay.Build(ws.Base, [lower], TestWorkspace.FakeDllWriter.Instance, keepDocuments: true);

        // The text writer writes the float 100 as "100", which the parser reads as an Int32.
        Assert.IsType<Int32Value>(Section(overlay, "beta")["volume"]![0]);
        var kept = overlay.Document(GoodsPath)!;
        Assert.IsType<SingleValue>(kept.Sections[1]["volume"]![0]);
        // Each call gives a new deep copy.
        kept.Sections[1].Clear();
        Assert.Equal(3, overlay.Document(GoodsPath)!.Sections[1].Count);
        // A path that no mod patches comes from its file in a mod or in the base folder.
        Assert.Equal("w1", overlay.Document(@"DATA\whole.ini")!.Sections.Single()[0][0].ToString());
        Assert.Equal("g1", overlay.Document(@"DATA\world.ini")!.Sections.Single()[0][0].ToString());
        Assert.Null(overlay.Document(@"DATA\none.ini"));
        Assert.Throws<System.InvalidOperationException>(() => ws.Build(lower).Document(GoodsPath));
    }

    private static TestWorkspace Workspace()
    {
        var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        return ws;
    }

    private static Dictionary<string, string> Patch(string text) => new() { [@"patches\DATA\goods.ini"] = text };

    private static Section Section(ModOverlay overlay, string nickname) =>
        TestWorkspace.Sections(overlay, GoodsPath).Single(s => Key(s) == nickname);

    private static string? Key(Section s) => s["nickname"]?[0].ToString();

    /// <summary>"SectionName firstValue" for each section of a merged file.</summary>
    private static string[] Describe(ModOverlay overlay, string path) =>
        TestWorkspace.Sections(overlay, path).Select(s => $"{s.Name} {s[0][0]}").ToArray();
}
