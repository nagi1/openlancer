using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Validation;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>
/// The format checks of the export (section 7.7 of the M11 design): data that the retail text reader reads in another
/// way, or does not accept. Each test triggers one check. The last test has data that passes each check.
/// </summary>
public sealed class FormatCheckTests
{
    private static IReadOnlyList<Diagnostic> Check(ExportGame game, string patch) => Check(game, game.GoodsMod(patch));

    private static IReadOnlyList<Diagnostic> Check(ExportGame game, OpenLancer.Data.Mods.Mod mod)
    {
        var files = game.Build(mod);
        Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return FormatChecks.Run(files);
    }

    [Fact]
    public void ALineLongerThan1023BytesIsAnError()
    {
        using var game = new ExportGame();
        var values = string.Join(", ", Enumerable.Repeat(new string('a', 300), 4));

        var problem = Assert.Single(Check(game, $"[Good]\nnickname = g2\nnames = {values}\n"));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.EndsWith("The game reads only the first 1,023 bytes of the line. It reads the rest as the next line.",
            problem.Message);
        Assert.Equal(new Data.Editing.SectionRef(GoodsPath, new Data.Editing.SectionIdentity("Good", "g2")), problem.Target);
    }

    [Fact]
    public void AKeyOrAValueLongerThan1023BytesIsAnError()
    {
        using var game = new ExportGame();

        var problem = Assert.Single(Check(game, $"[Good]\nnickname = g2\nname = {new string('a', 1100)}\n"));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.EndsWith("a key or a value is 1100 bytes long. The game writes past the end of its buffer.", problem.Message);
    }

    [Fact]
    public void ASectionNameLongerThan1023BytesIsAnError()
    {
        using var game = new ExportGame();
        var name = new string('S', 1100);

        var problems = Check(game, $"[{name}]\nol.new = true\nnickname = x\n");

        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error &&
                                       p.Message.EndsWith("the section name is 1100 bytes long. The game writes past the end of its buffer."));
    }

    [Fact]
    public void ASemicolonInABinaryStringIsAnError()
    {
        using var game = new ExportGame();
        BiniGoods(game, ("note", new StringValue("a;b")));

        var problems = Check(game, "[Good]\nnickname = g2\nprice = 300\n");

        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error &&
                                       p.Message.EndsWith("the value contains ';'. The game ignores the text after ';'."));
    }

    [Fact]
    public void ACommaInABinaryStringIsAnError()
    {
        using var game = new ExportGame();
        BiniGoods(game, ("note", new StringValue("a,b")));

        var problems = Check(game, "[Good]\nnickname = g2\nprice = 300\n");

        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error &&
                                       p.Message.EndsWith("the value contains ','. The game reads two values in place of one."));
    }

    [Fact]
    public void ABooleanInPlaceOfANumberIsAnErrorInText()
    {
        using var game = new ExportGame();

        var problem = Assert.Single(Check(game, "[Good]\nnickname = g2\nprice = true\n"));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("[Good] nickname = g2 price: write 1 or 0. The game reads true as 0 in a number key.", problem.Message);
        // A new line of a key that the engine reads as a number gets the error too.
        Assert.Single(Check(game, game.GoodsMod("[Good]\nol.new = true\nnickname = g9\nprice = true\n", "n")));
    }

    [Fact]
    public void AFloatThatIsNotFiniteIsAnError()
    {
        using var game = new ExportGame();

        var problem = Assert.Single(Check(game, "[Good]\nnickname = g2\nprice = 1e40\n"));

        Assert.EndsWith("the value is not a finite number. The game reads 0.", problem.Message);
    }

    [Fact]
    public void ANumberWithASuffixIsAWarning()
    {
        using var game = new ExportGame();

        var problem = Assert.Single(Check(game, "[Good]\nnickname = g2\nprice = 12h\n"));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal("[Good] nickname = g2 price = 12h: the game reads this number in another base.", problem.Message);
    }

    [Fact]
    public void ACharacterOutsideCodePage1252IsAWarning()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod("[Good]\nnickname = g2\nprice = 1\n");
        // A patch with a byte order mark is UTF-8, so it can hold any character.
        File.WriteAllBytes(Path.Combine(mod.Folder, "patches", GoodsPath),
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("[Good]\nnickname = g2\nnote = Ā\n")]);

        var problem = Assert.Single(Check(game, OpenLancer.Data.Mods.Mod.Load(mod.Folder)));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.EndsWith("the value has a character outside code page 1252. The game shows another character.", problem.Message);
    }

    [Fact]
    public void AnInfocardThatIsNotXmlIsAnError()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "infocards": { "info": "<RDL><TEXT>open</RDL>" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nids_info = $ifc:info\n",
        });

        var problem = Assert.Single(Check(game, mod));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.StartsWith("Infocard 1: the text is not valid XML", problem.Message);
        Assert.EndsWith("The game shows no text.", problem.Message);
    }

    [Fact]
    public void AnInfocardWithASinglePercentIsAWarning()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "infocards": { "info": "<RDL><TEXT>50% off</TEXT></RDL>" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nids_info = $ifc:info\n",
        });

        var problem = Assert.Single(Check(game, mod));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal("Infocard 1: the text has a single %. The game can show wrong text. Write %% for one %.",
            problem.Message);
    }

    [Fact]
    public void ALibrelancerOnlyThnNameIsAWarning()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            [@"files\DATA\SCRIPTS\new.thn"] = "duration = 5\nentities = {\n{ entity_name = \"Sub\", type = SUBTITLE }\n}\n",
        });

        var problem = Assert.Single(Check(game, mod));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal("SUBTITLE: retail files never use this name. Write its number in the file. Check the cutscene in " +
                     "the game.", problem.Message);
    }

    [Fact]
    public void TheFixedDataPassesEachCheck()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "strings": { "n": "Name" }, "infocards": { "info": "<RDL><TEXT>50%% off</TEXT></RDL>" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nprice = 1\nvolume = 0.5\nids_name = $str:n\n" +
                                        "ids_info = $ifc:info\nnote = plain text\n\n[Good]\nol.new = true\nnickname = g3\nprice = 1e3\n",
            [@"files\DATA\SCRIPTS\new.thn"] = "duration = 5\nentities = {\n{ entity_name = \"Sub\", type = 19 }\n}\n",
        });

        Assert.Empty(Check(game, mod));
    }

    /// <summary>Makes the goods file of the game a binary file with one more line in the section g2.</summary>
    private static void BiniGoods(ExportGame game, (string Name, ValueBase Value) line)
    {
        var sections = Parse(Goods).Sections;
        var g2 = sections[1];
        var entry = new Entry(g2, line.Name, 1);
        entry.Add(line.Value);
        g2.Add(entry);
        using var stream = new MemoryStream();
        IniWriter.WriteBini(stream, sections);
        game.Game.WriteBytes(GoodsPath, stream.ToArray());
    }
}

/// <summary>A boolean cannot replace a number in a binary INI file (decision D333 of the M11 design).</summary>
public sealed class BinaryValueTypesTests
{
    [Fact]
    public void ABooleanInPlaceOfANumberIsAnError()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\goods.ini", "[Good]\nnickname = g1\nprice = 100\nvolume = 1.5\nflag = true\n");

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [@"patches\DATA\goods.ini"] = "[Good]\nnickname = g1\nprice = true\nvolume = false\nflag = false\n",
        }));

        Assert.Equal(["price: write 1 or 0. The game reads true as 0 in a number key.",
                      "volume: write 1 or 0. The game reads true as 0 in a number key."],
            overlay.Diagnostics.Select(d => d.Message));
        Assert.All(overlay.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        var line = new Entry(new Section("Good"), "price", 1) { new BooleanValue(true) };
        var replaced = new Entry(new Section("Good"), "price", 1) { new Int32Value(100) };
        Assert.Null(BinaryValueTypes.Store(line, 0, replaced, null));
    }
}
