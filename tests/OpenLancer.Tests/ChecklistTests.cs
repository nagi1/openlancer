using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The checklist for testers (decision D322 of the M11 design): one line for each entry that the mod adds,
/// changes or removes, in plain words.</summary>
public sealed class ChecklistTests
{
    private const string CutscenePath = @"DATA\SCRIPTS\BASES\city.thn";

    private const string Cutscene = """
        duration = 10
        entities = {
            { entity_name = "Camera_0", type = CAMERA, spatialprops = { pos = { 1, 2, 3 } } },
            { entity_name = "Light_0", type = LIGHT, spatialprops = { pos = { 0, 0, 0 } } }
        }
        events = {}
        """;

    /// <summary>A mod like the sample mod: a lower price, a new ship package for sale, and a moved camera.</summary>
    internal static Data.Mods.Mod SampleLike(ExportGame game)
    {
        game.Game.WriteBytes(CutscenePath, ThnScripts.Compile(Cutscene));
        return game.Mod("ol-sample", new()
        {
            ["strings.json"] = """{ "strings": { "starrunner": "Starrunner" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = lf_hull\nprice = 5600\n\n[Good]\nol.new = true\n" +
                                        "nickname = ol_starrunner_package\ncategory = ship\nids_name = $str:starrunner\n",
            [@"patches\" + MarketPath] = "[BaseGood]\nbase = li01_01_base\nmarketgood = ol_starrunner_package, 0, -1, 1, 1, 0, 1, 1\n",
            [@"patches\" + CutscenePath] = "ol_thn_patch = 1\nentities = {\n" +
                                           "\t{ ol_match = \"Camera_0\", ol_set = { [\"spatialprops.pos\"] = { 1, 32, 3 } } }\n}\n",
        });
    }

    [Fact]
    public void TheChecklistOfTheSampleHasThePriceLineThePackageLineAndTheCutsceneLine()
    {
        using var game = new ExportGame();

        var lines = PackageTexts.Checklist(game.Build(SampleLike(game)));

        Assert.Contains("- Good 'lf_hull': price 6600 -> 5600.", lines);
        Assert.Contains("- New Good 'ol_starrunner_package'.", lines);
        Assert.Contains("- New market good 'ol_starrunner_package' at base 'li01_01_base'.", lines);
        Assert.Contains($"- Cutscene {CutscenePath}: 1 entity changed.", lines);
        Assert.Contains("- Text: 1 string and 0 infocards in EXE\\ol_ol-sample.dll.", lines);
        Assert.Contains(GoodsPath, lines);
    }

    [Fact]
    public void TheMergedCutsceneIsTextWithTheChange()
    {
        using var game = new ExportGame();

        var files = game.Build(SampleLike(game));

        var cutscene = files.File(CutscenePath)!;
        Assert.Equal(TargetFileKind.Cutscene, cutscene.Kind);
        var text = System.Text.Encoding.ASCII.GetString(cutscene.Bytes);
        Assert.StartsWith("duration = 10\n", text);
        Assert.Contains("pos = { 1, 32, 3 }", text);
        Assert.Equal(ThnScripts.Evaluate(text), cutscene.MergedTree);
    }
}

/// <summary>The THN names that only Librelancer defines (section 7.6 of the M11 design).</summary>
public sealed class ThnRetailNamesTests
{
    [Fact]
    public void EachLibrelancerOnlyNameBecomesItsNumber()
    {
        // The numbers of the retail tables of the THN format.
        var expected = new Dictionary<string, float>
        {
            ["UNKNOWN_ENTITY"] = 0, ["SUB_SCENE"] = 10, ["DELETED"] = 12, ["UNDEFINED_EVENT"] = 0, ["USER_EVENT"] = 1,
            ["START_SUB_SCENE"] = 12, ["START_REVERB_PROP_ANIM"] = 17, ["SUBTITLE"] = 19, ["STREAM"] = 4,
        };
        Assert.Equal(expected.Keys.Order(), ThnRetailNames.LibrelancerOnly.Order());
        foreach (var (name, number) in expected)
        {
            var tree = ThnTable.FromRecord(new ThnField("x", new ThnSymbol(name)),
                new ThnField("list", ThnTable.FromList(new ThnSymbol(name), new ThnSymbol("CAMERA"))));

            var retail = ThnRetailNames.Apply(tree, ThnScripts.Evaluator.Symbols);

            Assert.Equal(new ThnNumber(number), retail["x"]);
            Assert.Equal(ThnTable.FromList(new ThnNumber(number), new ThnSymbol("CAMERA")), retail["list"]);
            Assert.Equal([name], ThnRetailNames.Find(tree));
            Assert.Empty(ThnRetailNames.Find(retail));
        }
    }

    [Fact]
    public void ARetailNameStaysAName()
    {
        var tree = ThnTable.FromRecord(new ThnField("type", new ThnSymbol("CAMERA")));

        Assert.Equal(tree, ThnRetailNames.Apply(tree, ThnScripts.Evaluator.Symbols));
    }

    [Fact]
    public void AnUnchangedTreeIsLeftOut()
    {
        using var game = new ExportGame();
        game.Game.WriteBytes(@"DATA\SCRIPTS\a.thn", ThnScripts.Compile("duration = 5\nentities = {\n" +
                                                                       "{ entity_name = \"Cam\", type = CAMERA }\n}\nevents = {}\n"));
        // The patch sets the value that the cutscene has.
        var mod = game.Mod("m", new()
        {
            [@"patches\DATA\SCRIPTS\a.thn"] = "ol_thn_patch = 1\nentities = {\n\t{ ol_match = \"Cam\", ol_set = { type = CAMERA } } }\n",
        });

        var files = game.Build(mod);

        Assert.Empty(files.Files);
        Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }
}

/// <summary>The file list of a game folder (decision D319 of the M11 design).</summary>
public sealed class HdeManifestTests
{
    [Fact]
    public void WriteManifestLeavesOutUninstallerSaveAndLogs()
    {
        using var game = new FakeGame();
        game.Write("unins000.exe", "x");
        game.Write("unins000.dat", "x");
        game.Write("UNINSTAL.EXE", "x");
        game.Write(@"SAVE\PerfOptions.ini", "x");
        game.Write(@"EXE\flhook.log", "x");
        game.Write(@"DATA\unins.txt", "kept, because it is not in the root");
        game.Write("README.md", "kept");
        var file = Path.Combine(game.Folder, "list.tsv");

        var count = ManifestWriter.Write(game.Root, file);

        var paths = File.ReadAllLines(file).Select(l => l.Split('\t')[0]).ToList();
        Assert.Equal(count, paths.Count);
        Assert.Equal(paths.Order(System.StringComparer.Ordinal), paths);
        Assert.Contains("data/unins.txt", paths);
        Assert.Contains("readme.md", paths);
        Assert.Contains("exe/freelancer.ini", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("unins") || p.StartsWith("save/") || p.EndsWith(".log"));
        Assert.DoesNotContain(File.ReadAllText(file), "\r");
        Assert.Equal(paths.Count, KnownBuilds.Parse("test", File.ReadAllText(file)).Count);
    }

    [Fact]
    public void WriteManifestRefusesAPlayCopy()
    {
        using var game = new FakeGame();
        game.Write(ManifestWriter.PlayCopyRecord, "{}");

        var error = Assert.Throws<System.InvalidOperationException>(() =>
            ManifestWriter.Write(game.Root, Path.Combine(game.Folder, "list.tsv")));

        Assert.Contains("is an OpenLancer play copy", error.Message);
        Assert.False(File.Exists(Path.Combine(game.Folder, "list.tsv")));
    }

    [Fact]
    public void WriteManifestNeverWritesIntoTheGameFolder()
    {
        using var game = new FakeGame();
        var before = FakeGame.Snapshot(game.Root);

        var error = Assert.Throws<System.InvalidOperationException>(() =>
            ManifestWriter.Write(game.Root, Path.Combine(game.Root, "list.tsv")));

        Assert.Contains("OpenLancer does not write into the game folder", error.Message);
        Assert.Equal(before, FakeGame.Snapshot(game.Root));
    }

    [Fact]
    public void WriteManifestNeverWritesIntoAnotherGameFolder()
    {
        using var game = new FakeGame();
        using var other = new FakeGame();
        var before = FakeGame.Snapshot(other.Root);
        var file = Path.Combine(other.Root, "DATA", "lists", "list.tsv");

        var error = Assert.Throws<System.InvalidOperationException>(() => ManifestWriter.Write(game.Root, file));

        Assert.Contains($"is in the game folder {other.Root}. OpenLancer does not write into a game folder.", error.Message);
        Assert.Equal(before, FakeGame.Snapshot(other.Root));
    }

    [Fact]
    public void TheManifestParsesAndHasNoSaveOrUninstallerRow()
    {
        var rows = KnownBuilds.Embedded.Rows("hde-0.7.1");

        Assert.True(rows.Count > 8000);
        Assert.DoesNotContain(rows.Keys, p => p.StartsWith("save/") || p.EndsWith(".log") ||
                                              !p.Contains('/') && p.StartsWith("unins"));
        Assert.Contains("exe/freelancer.ini", rows.Keys);
        Assert.Contains("data/scripts/bases/li_01_cityscape_hardpoint_01.thn", rows.Keys);
        Assert.Null(KnownBuilds.ParentOf("hde-0.7.1"));
    }

    [Fact]
    public void AnHdInstallComparesWithItsFileList()
    {
        using var game = new ExportGame(hd: true);
        var builds = game.Builds;

        Assert.Equal(DataState.NotChanged, game.Game.Detect(builds).Data);
        game.Game.Write(GoodsPath, "[Good]\nnickname = changed\n");
        var report = game.Game.Detect(builds);

        Assert.Equal(DataState.Changed, report.Data);
        Assert.Contains(report.Findings, f => f.Severity == GameSeverity.Warning &&
                                              f.Text.EndsWith(" " + GameTexts.HdeInstallerOptions));
        Assert.Contains("hde-0.7.1", KnownBuilds.Embedded.Ids);
    }
}
