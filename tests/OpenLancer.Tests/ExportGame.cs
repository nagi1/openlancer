using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;

namespace OpenLancer.Tests;

/// <summary>
/// A fake game for the export tests (milestone M11): a <see cref="FakeGame"/> with text INI files that have comments,
/// and a folder for mods. The game is Freelancer 1.0 by the hash of its code files, or HD Edition 0.7.1 by its marker
/// DLL. All files are hand-written. Deleted on dispose.
/// </summary>
internal sealed class ExportGame : IDisposable
{
    public const string GoodsPath = @"DATA\EQUIPMENT\goods.ini";
    public const string MarketPath = @"DATA\EQUIPMENT\market_ships.ini";

    public const string Goods = """
        ; The goods of the test.
        [Good]
        nickname = lf_hull
        price = 6600 ; the Patriot hull
        ids_name = 100

        [Good]
        nickname = g2
        price = 200
        """;

    public const string Market = """
        [BaseGood]
        base = li01_01_base
        marketgood = lf_hull, 0, -1, 1, 1, 0, 1, 1
        """;

    /// <summary>The <c>[Resources]</c> lines of the fake HD Edition: the 6 DLLs of the retail game and
    /// <c>Controls.dll</c>.</summary>
    public static readonly string[] HdDlls =
        ["InfoCards.dll", "MiscText.dll", "NameResources.dll", "EquipResources.dll", "OfferBribeResources.dll",
         "MiscTextInfo2.dll", "Controls.dll"];

    public ExportGame(bool hd = false)
    {
        Game = new FakeGame();
        IsHd = hd;
        ModsFolder = Path.Combine(Game.Folder, "mods");
        if (hd)
        {
            Game.Write(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n[Data]\nuniverse = universe\\universe.ini\n" +
                                              "goods = equipment\\goods.ini\nmarkets = equipment\\market_ships.ini\n\n" +
                                              "[Resources]\n" + string.Concat(HdDlls.Select(d => $"DLL = {d}\n")));
            Game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "HDE v0.7.1" }));
        }
        else
        {
            Game.Write(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n[Data]\nuniverse = universe\\universe.ini\n" +
                                              "goods = equipment\\goods.ini\nmarkets = equipment\\market_ships.ini\n\n" +
                                              "[Resources]\nDLL = InfoCards.dll ; the infocards\n");
        }
        Game.Write(GoodsPath, Goods);
        Game.Write(MarketPath, Market);
    }

    public FakeGame Game { get; }

    public bool IsHd { get; }

    public string Root => Game.Root;

    public string ModsFolder { get; }

    public ExportTarget Target => IsHd ? ExportTarget.HdEdition : ExportTarget.Vanilla;

    public GameBase Base => GameBase.Standard(Root);

    /// <summary>The builds of the detector: the build of the game with the files of the game now.</summary>
    public KnownBuilds Builds => FakeGame.Builds((IsHd ? "hde-0.7.1" : "vanilla-1.0", Game.Manifest()));

    /// <summary>The check of the game.</summary>
    public GameReport Report() => Game.Detect(Builds);

    /// <summary>Writes a mod of format version 2 and loads it.</summary>
    /// <param name="files">The path of each file in the mod folder to its text.</param>
    /// <param name="manifest">More fields of mod.json, for example <c>, "tier": 2</c>.</param>
    public Mod Mod(string id, Dictionary<string, string> files, string manifest = "")
    {
        var folder = Path.Combine(ModsFolder, id);
        Write(Path.Combine(folder, ModManifest.FileName),
            $$"""{ "id": "{{id}}", "name": "Mod {{id}}", "version": "1.2.0", "formatVersion": 2 {{manifest}} }""");
        foreach (var (path, text) in files)
            Write(Path.Combine(folder, path), text);
        return Data.Mods.Mod.Load(folder);
    }

    /// <summary>A mod with one patch of the goods.</summary>
    public Mod GoodsMod(string patch, string id = "m") => Mod(id, new() { [@"patches\" + GoodsPath] = patch });

    /// <summary>Merges one mod on the game with the retail rules.</summary>
    public TargetFiles Build(Mod mod, bool keepPlaceholders = false) =>
        TargetBuild.Run(Base, [mod], ModExport.DllName(mod.Id), TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator,
            keepPlaceholders);

    /// <summary>An export request of one mod into a folder of the test.</summary>
    public ExportRequest Request(Mod mod, params ExportTarget[] targets)
    {
        var report = Report();
        return new ExportRequest
        {
            Mod = mod,
            Targets = targets.Length == 0 ? [Target] : targets,
            BaseOf = t => t.Id == report.Target ? report : null,
            OutputFolder = Path.Combine(Game.Folder, "exports", mod.Id),
            StringWriter = TestWorkspace.FakeDllWriter.Instance,
            Thn = ThnScripts.Evaluator,
        };
    }

    /// <summary>The text of a file, decoded in code page 1252.</summary>
    public static string Text(byte[] bytes) => IniDocument.TextEncoding.GetString(bytes);

    /// <summary>Parses INI text as the engine parses it.</summary>
    public static IniDocument Parse(string text, string path = "test.ini") =>
        IniDocument.Parse(path, IniDocument.TextEncoding.GetBytes(text));

    /// <summary>A section of a parsed file by its key.</summary>
    public static Section Section(IniDocument document, string nickname) =>
        document.Sections.Single(s => s["nickname"]?[0].ToString() == nickname);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Encoding.ASCII);
    }

    public void Dispose() => Game.Dispose();
}
