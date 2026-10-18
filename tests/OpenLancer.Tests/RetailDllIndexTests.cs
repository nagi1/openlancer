using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>
/// The index and the line of the text DLL of an export (section 7.4 of the M11 design). The retail game and its server
/// load resources.dll as index 0. Each DLL line of each [Resources] section gets the next index, also when its file
/// does not load.
/// </summary>
public sealed class RetailDllIndexTests
{
    private const string Strings = """{ "strings": { "name": "Name" } }""";

    private static Mod TextMod(ExportGame game) => game.Mod("m", new()
    {
        ["strings.json"] = Strings,
        [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nids_name = $str:name\n",
    });

    [Fact]
    public void TheIndexCountsEachDllLine()
    {
        using var game = new ExportGame(hd: true);

        var files = game.Build(TextMod(game));

        // HD Edition 0.7.1 lists 7 DLLs, so the mod DLL gets index 8.
        Assert.Equal(8, files.StringIds.DllIndex);
        Assert.Equal(8 * 65536 + 1, Section(files.File(GoodsPath)!.Merged!, "g2")["ids_name"]![0].ToInt32());
    }

    [Fact]
    public void AMissingListedDllKeepsItsIndex()
    {
        using var game = new ExportGame(hd: true);

        var files = game.Build(TextMod(game));

        // The fake game has none of the listed DLL files except Controls.dll.
        Assert.Equal(8, files.StringIds.DllIndex);
        Assert.Contains(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning &&
                                                d.Message == "freelancer.ini names InfoCards.dll, and the EXE folder does " +
                                                "not contain it. The game keeps its index, so the text ids of the mod " +
                                                "stay correct.");
        Assert.DoesNotContain(files.Diagnostics, d => d.Message.StartsWith("freelancer.ini names Controls.dll"));
    }

    [Fact]
    public void TheIndexCountsEachResourcesSection()
    {
        using var game = new ExportGame();
        game.Game.Write(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n[Resources]\nDLL = a.dll\nDLL = b.dll\n\n" +
                                               "[Data]\nuniverse = universe\\universe.ini\n\n[Resources]\nDLL = c.dll\n");

        var files = game.Build(TextMod(game));

        Assert.Equal(4, files.StringIds.DllIndex);
    }

    [Fact]
    public void TheDllLineGoesAfterTheLastDllLine()
    {
        using var game = new ExportGame();
        game.Game.Write(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n[Resources]\nDLL = a.dll\nDLL = b.dll\n" +
                                               "other = 1\n");

        var files = game.Build(TextMod(game));

        var ini = Text(files.File(ModOverlay.FreelancerIniPath)!.Bytes);
        Assert.Contains("DLL = b.dll\r\nDLL = ol_m.dll\r\nother = 1\r\n", ini);
        Assert.Equal(3, files.StringIds.DllIndex);
    }

    [Fact]
    public void TheDllHasTheNameOfTheMod()
    {
        using var game = new ExportGame();

        var files = game.Build(TextMod(game));

        Assert.NotNull(files.File(@"EXE\ol_m.dll"));
        Assert.Equal(TargetFileKind.TextDll, files.File(@"EXE\ol_m.dll")!.Kind);
        Assert.Null(files.File(ModOverlay.StringsDllPath));
        // The text base keeps its comment, and gets the line after its last DLL line.
        Assert.Contains("DLL = InfoCards.dll ; the infocards\r\nDLL = ol_m.dll\r\n",
            Text(files.File(ModOverlay.FreelancerIniPath)!.Bytes));
    }

    [Fact]
    public void PlayInOpenLancerKeepsTheLibrelancerRule()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, "[Good]\nnickname = g2\nprice = 1\n");
        var mod = ws.Mod("m", new Dictionary<string, string>
        {
            ["strings.json"] = Strings,
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nids_name = $str:name\n",
        });

        var play = ws.Build(mod);
        var retail = ModOverlay.Build(GameBase.Standard(ws.Base), [mod], TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator, new OverlayOptions { DllIndexRule = DllIndexRule.Retail });

        // Librelancer counts resources.dll and InfoCards.dll, which exist. Missing.dll does not count.
        Assert.Equal(2, play.StringIds.DllIndex);
        Assert.Equal(3, retail.StringIds.DllIndex);
        Assert.Equal(OverlayOptions.Default, play.Options);
        var lines = TestWorkspace.Sections(play, ModOverlay.FreelancerIniPath).Single(s => s.Name == "Resources")
            .Select(e => e[0].ToString()).ToList();
        Assert.Equal(["InfoCards.dll", "Missing.dll", ModOverlay.StringsDllName], lines);
    }
}
