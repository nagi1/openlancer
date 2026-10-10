using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The save of an edit session: which files it writes, which it keeps, and when it writes nothing.
/// </summary>
public sealed class EditSessionSaveTests
{
    private static string Act(TestWorkspace ws, string relative = "") => Path.Combine(ws.ModFolder("act"), relative);

    private static string PatchOf(TestWorkspace ws, string installPath) => Act(ws, Path.Combine("patches", installPath));

    private static void AssertSaved(SaveResult result)
    {
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void OnlyTheActiveModGetsFiles()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        ws.WriteBase(WeaponsPath, Weapons);
        // The lower mod's file name has its own letter case. A new patch of the same file takes it.
        ws.Mod("low", new() { [@"patches\DATA\EQUIPMENT\Goods.ini"] = "[Good]\nnickname = g1\nprice = 150" });
        ws.Mod("act", new());
        ws.WriteProfile("low", "act");
        var baseFiles = TestWorkspace.Files(ws.Base);
        var lowFiles = TestWorkspace.Files(ws.ModFolder("low"));
        var session = ws.Session("act");

        session.Execute(Set(Good("g2"), "price", new Int32Value(250)));
        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(11)));
        var result = session.Save();

        AssertSaved(result);
        Assert.Equal(baseFiles, TestWorkspace.Files(ws.Base));
        Assert.Equal(lowFiles, TestWorkspace.Files(ws.ModFolder("low")));
        Assert.Equal(["mod.json", @"patches\DATA\EQUIPMENT\Goods.ini", @"patches\DATA\EQUIPMENT\weapon_equip.ini"],
            TestWorkspace.Files(Act(ws)).Keys);
        Assert.Equal("[Good]\r\nnickname = g2\r\nprice = 250\r\n", File.ReadAllText(Act(ws, @"patches\DATA\EQUIPMENT\Goods.ini")));
        Assert.Equal("[Gun]\r\nnickname = gun1\r\ndamage = 11\r\n", File.ReadAllText(PatchOf(ws, WeaponsPath)));
        Assert.Equal(2, result.Written.Count);
        Assert.False(Directory.Exists(Act(ws, SaveTransaction.FolderName)));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void ASaveWithoutEditsWritesNothing()
    {
        using var ws = Workspace(new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 120" });
        var before = TestWorkspace.Files(Act(ws));
        var session = ws.Session("act");

        var result = session.Save();

        AssertSaved(result);
        Assert.Empty(result.Written);
        Assert.Equal(before, TestWorkspace.Files(Act(ws)));
    }

    /// <summary>A hand-written patch that no edit changes keeps its bytes, also when an edit was undone (rule P2).</summary>
    [Fact]
    public void AnUntouchedHandWrittenPatchStaysByteForByte()
    {
        var hand = Encoding.ASCII.GetBytes("; prices\n[good]\nNICKNAME=g1   ; odd spacing\nprice = 120\n\n\n");
        using var ws = Workspace(new() { [@"patches\" + GoodsPath] = "" });
        File.WriteAllBytes(PatchOf(ws, GoodsPath), hand);
        var session = ws.Session("act");

        session.Execute(Set(Good("g2"), "price", new Int32Value(5)));
        session.Undo();
        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(11)));
        var result = session.Save();

        AssertSaved(result);
        Assert.Equal([PatchOf(ws, WeaponsPath)], result.Written);
        Assert.Equal(hand, File.ReadAllBytes(PatchOf(ws, GoodsPath)));
    }

    /// <summary>
    /// The header, the comment block above a section, and a loaded line that no edit touched stay, also when the line
    /// equals B. So the mod keeps its value when the lower mod changes (review finding F3).
    /// </summary>
    [Fact]
    public void AnUntouchedPinLineStaysAfterAnEditToItsSection()
    {
        const string patch = """
            ; Header of my mod

            ; g1 is cheaper
            [Good]
            nickname = g1
            price = 150 ; the lower mod sets this too

            [Good]
            nickname = g2
            price = 200
            """;
        using var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        ws.Mod("low", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 150" });
        ws.Mod("act", new() { [@"patches\" + GoodsPath] = patch });
        ws.WriteProfile("low", "act");
        var session = ws.Session("act");

        session.Execute(Set(Good("g1"), "volume", new Int32Value(3)));
        AssertSaved(session.Save());

        Assert.Equal(PatchWriterTests.Lines(
            "; Header of my mod", "", "; g1 is cheaper", "[Good]", "nickname = g1", "price = 150 ; the lower mod sets this too",
            "volume = 3", "", "[Good]", "nickname = g2", "price = 200"), File.ReadAllText(PatchOf(ws, GoodsPath)));

        // The lower mod changes its price. The active mod still pins its own.
        File.WriteAllText(Path.Combine(ws.ModFolder("low"), "patches", GoodsPath), "[Good]\r\nnickname = g1\r\nprice = 999\r\n");
        var merged = TestWorkspace.Sections(ws.Build(Data.Mods.Mod.Load(ws.ModFolder("low")), Data.Mods.Mod.Load(Act(ws))), GoodsPath);
        Assert.Equal(150, merged[0]["price"]![0].ToInt32());
        Assert.Equal(3, merged[0]["volume"]![0].ToInt32());
    }

    /// <summary>An undo gives back the pin that the edit removed, so the save writes the pinned line again.</summary>
    [Fact]
    public void AnUndoGivesBackThePinOfTheName()
    {
        using var ws = Workspace(new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 100" });
        var session = ws.Session("act");

        session.Execute(Set(Good("g1"), "volume", new Int32Value(2)));
        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        session.Undo();
        AssertSaved(session.Save());

        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = g1", "price = 100", "volume = 2"),
            File.ReadAllText(PatchOf(ws, GoodsPath)));
    }

    [Fact]
    public void AnUndoAfterASaveGivesTheOlderFilesAtTheNextSave()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        AssertSaved(session.Save());
        Assert.Equal(300, TestWorkspace.Sections(session.EditView, GoodsPath)[0]["price"]![0].ToInt32());
        session.Undo();
        var undone = session.Save();
        AssertSaved(undone);

        Assert.Equal([PatchOf(ws, GoodsPath)], undone.Deleted);
        Assert.False(File.Exists(PatchOf(ws, GoodsPath)));
        Assert.Equal(100, TestWorkspace.Sections(session.EditView, GoodsPath)[0]["price"]![0].ToInt32());
        session.Redo();
        AssertSaved(session.Save());
        Assert.Equal("[Good]\r\nnickname = g1\r\nprice = 300\r\n", File.ReadAllText(PatchOf(ws, GoodsPath)));
    }

    /// <summary>
    /// After a save, W is what a reload of the saved files gives (review finding F14). So edits in a second session
    /// give the same bytes as the same edits in one session.
    /// </summary>
    [Fact]
    public void ASecondSessionContinuesFromTheSavedEdits()
    {
        EditCommand[] first =
        [
            EditCommand.AddSection(GoodsPath, "Good",
            [
                new EntryView("nickname", [new StringValue("g3")]), new EntryView("ids_name", [new StringValue("$str:g3")]),
                new EntryView("price", [new Int32Value(5)]),
            ]),
            EditCommand.SetText(StringKind.String, "g3", "Gun three"),
            Set(Good("g1"), "price", new Int32Value(300)),
        ];
        EditCommand[] second =
        [
            Set(Good("g3"), "price", new Int32Value(6)),
            Set(Good("g1"), "volume", new Int32Value(4)),
            Set(Gun("gun1"), "damage", new Int32Value(11)),
            EditCommand.SetText(StringKind.String, "g3", "Good three"),
        ];
        using var split = Workspace();
        var session = split.Session("act");
        foreach (var command in first)
            session.Execute(command);
        AssertSaved(session.Save());

        var reopened = split.Session("act");
        Assert.Equal(["$str:g3"], Values(reopened, Good("g3"), "ids_name"));
        Assert.Equal("Gun three", reopened.Text(new StringPlaceholder(StringKind.String, "g3")));
        foreach (var command in second)
            reopened.Execute(command);
        AssertSaved(reopened.Save());

        using var direct = Workspace();
        var one = direct.Session("act");
        foreach (var command in first.Concat(second))
            one.Execute(command);
        AssertSaved(one.Save());

        Assert.Equal(TestWorkspace.Files(Act(direct)), TestWorkspace.Files(Act(split)));
    }

    /// <summary>An empty patch deletes its file and its empty folders, up to the patches folder (rule P4).</summary>
    [Fact]
    public void AnEmptyPatchDeletesItsFileAndItsEmptyFolders()
    {
        using var ws = Workspace(new() { [@"patches\" + WeaponsPath] = "[Gun]\nnickname = gun1\ndamage = 20" });
        var session = ws.Session("act");

        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(10)));
        var result = session.Save();

        AssertSaved(result);
        Assert.Equal([PatchOf(ws, WeaponsPath)], result.Deleted);
        Assert.False(Directory.Exists(Act(ws, @"patches\DATA")));
        Assert.True(Directory.Exists(Act(ws, "patches")));
        Assert.Equal(["mod.json"], TestWorkspace.Files(Act(ws)).Keys);
    }

    /// <summary>
    /// A file of the active mod that changes outside the editor stops the save. A save with overwrite replaces it
    /// (rule P7).
    /// </summary>
    [Fact]
    public void AnOutsideChangeOfTheActiveModStopsTheSave()
    {
        using var ws = Workspace(new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 120" });
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "volume", new Int32Value(3)));
        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(11)));
        session.Execute(EditCommand.SetText(StringKind.String, "n", "N"));

        File.WriteAllText(PatchOf(ws, GoodsPath), "[Good]\r\nnickname = g1\r\nprice = 130\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(PatchOf(ws, WeaponsPath))!);
        File.WriteAllText(PatchOf(ws, WeaponsPath), "[Gun]\r\nnickname = gun1\r\ndamage = 12\r\n");
        File.WriteAllText(Act(ws, "strings.json"), """{ "strings": { "m": "M" } }""");
        var outside = TestWorkspace.Files(Act(ws));
        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Equal([PatchOf(ws, GoodsPath), PatchOf(ws, WeaponsPath), Act(ws, "strings.json")], result.ChangedOnDisk);
        Assert.Equal(outside, TestWorkspace.Files(Act(ws)));
        Assert.True(session.IsDirty);

        AssertSaved(session.Save(overwrite: true));
        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = g1", "price = 120", "volume = 3"),
            File.ReadAllText(PatchOf(ws, GoodsPath)));
        Assert.Equal("{\n  \"strings\": {\n    \"n\": \"N\"\n  }\n}\n", File.ReadAllText(Act(ws, "strings.json")));
        Assert.False(session.IsDirty);
    }

    /// <summary>A change of a lower mod after the open stops the save, and the edits go to the recovery file (F11).</summary>
    [Fact]
    public void AChangeOfALowerModStopsTheSave()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        ws.Mod("low", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 150" });
        ws.Mod("act", new());
        ws.WriteProfile("low", "act");
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "volume", new Int32Value(3)));

        File.WriteAllText(Path.Combine(ws.ModFolder("low"), "patches", GoodsPath), "[Good]\r\nnickname = g1\r\nprice = 160\r\n");
        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Error && p.Source == GoodsPath);
        Assert.False(File.Exists(PatchOf(ws, GoodsPath)));
        Assert.True(File.Exists(RecoveryFile.PathOf(Act(ws))));
        Assert.True(session.IsDirty);
    }

    /// <summary>
    /// A change below the active mod after the open stops the save: the base data, a whole file, a patch or the text
    /// of a lower mod, the mod list or the load order of the profile, and the index of the text DLL. The edits go to
    /// the recovery file (F11).
    /// </summary>
    [Theory]
    [InlineData("base file")]
    [InlineData("lower whole file")]
    [InlineData("lower patch")]
    [InlineData("lower text")]
    [InlineData("new lower mod")]
    [InlineData("lower load order")]
    [InlineData("active mod gone")]
    [InlineData("text dll index")]
    public void AChangeBelowTheActiveModStopsTheSave(string change)
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        ws.WriteBase(WeaponsPath, Weapons);
        ws.Mod("low", new()
        {
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nids_name = $str:a",
            [@"files\" + WeaponsPath] = Weapons,
            ["strings.json"] = "{ \"strings\": { \"a\": \"A\" } }",
        });
        ws.Mod("mid", new());
        ws.Mod("act", new());
        ws.WriteProfile("mid", "low", "act");
        var session = ws.Session("act");
        session.Execute(Set(Good("g2"), "price", new Int32Value(3)));
        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(11)));
        var low = ws.ModFolder("low");

        var source = change switch
        {
            "base file" => Write(Path.Combine(ws.Base, GoodsPath), Goods.Replace("price = 200", "price = 3"), GoodsPath),
            "lower whole file" => Write(Path.Combine(low, "files", WeaponsPath), Weapons.Replace("10", "12"), WeaponsPath),
            "lower patch" => Write(Path.Combine(low, "patches", GoodsPath), "[Good]\r\nnickname = g1\r\nprice = 160\r\n", GoodsPath),
            "lower text" => Write(Path.Combine(low, "strings.json"), "{ \"strings\": { \"0\": \"Z\", \"a\": \"A\" } }", LayerInputs.TextInputs),
            "new lower mod" => Profile(ws, "new", "mid", "new", "low", "act"),
            "lower load order" => Write(Path.Combine(ws.ModFolder("mid"), "mod.json"),
                "{ \"id\": \"mid\", \"formatVersion\": 1, \"loadAfter\": [ \"low\" ] }", ws.ProfilePath),
            "active mod gone" => Profile(ws, null, "mid", "low"),
            // The base freelancer.ini lists Missing.dll. With the file, the text DLL of the mods moves one place on.
            "text dll index" => Write(Path.Combine(low, "files", "EXE", "Missing.dll"), "a dll", LayerInputs.TextInputs),
            _ => throw new ArgumentException(change),
        };
        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Error && p.Source == source);
        Assert.Equal(["mod.json"], TestWorkspace.Files(Act(ws), exclude: SaveTransaction.FolderName).Keys);
        Assert.True(File.Exists(RecoveryFile.PathOf(Act(ws))));
        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Warning && p.Source == RecoveryFile.PathOf(Act(ws)));
        Assert.True(session.IsDirty);
    }

    /// <summary>Writes a file, and returns the source that the save names for the change.</summary>
    private static string Write(string path, string text, string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return source;
    }

    /// <summary>Writes the profile with the mods, after it makes the mod <paramref name="newMod"/> when it is not
    /// null, and returns the profile file.</summary>
    private static string Profile(TestWorkspace ws, string? newMod, params string[] mods)
    {
        if (newMod != null)
            ws.Mod(newMod, new());
        ws.WriteProfile(mods);
        return ws.ProfilePath;
    }

    /// <summary>A lower mod that starts to patch a file after the open also stops the save of that file.</summary>
    [Fact]
    public void ANewPatchOfALowerModStopsTheSave()
    {
        using var ws = Workspace();
        ws.Mod("low", new());
        ws.WriteProfile("low", "act");
        var session = ws.Session("act");
        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(11)));

        var lowPatch = Path.Combine(ws.ModFolder("low"), "patches", WeaponsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lowPatch)!);
        File.WriteAllText(lowPatch, "[Gun]\r\nnickname = gun1\r\ndamage = 15\r\n");
        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, p => p.Source == WeaponsPath);
        Assert.False(File.Exists(PatchOf(ws, WeaponsPath)));
    }

    /// <summary>One file that the save cannot write stops the whole save (review finding F1).</summary>
    [Fact]
    public void AProblemInOneFileWritesNoFile()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "volume", new Int32Value(3)));
        session.Execute(Set(Gun("gun1"), "ids_name", new StringValue("$str:gun1_name")));
        var before = TestWorkspace.Files(Act(ws));

        var result = session.Save();

        Assert.False(result.Saved);
        var problem = Assert.Single(result.Problems);
        Assert.Contains("$str:gun1_name", problem.Message);
        Assert.Equal(Gun("gun1"), problem.Target);
        Assert.Equal(before, TestWorkspace.Files(Act(ws)));
        Assert.True(session.IsDirty);

        session.Execute(EditCommand.SetText(StringKind.String, "gun1_name", "Gun"));
        AssertSaved(session.Save());
        Assert.Equal(["mod.json", @"patches\DATA\EQUIPMENT\goods.ini", @"patches\DATA\EQUIPMENT\weapon_equip.ini", "strings.json"],
            TestWorkspace.Files(Act(ws)).Keys);
    }

    /// <summary>
    /// A patch line of a text key with the id of a text of a mod is an error (rule S8). The merge gives the id from the
    /// position of the text in the generated text DLL, so the line shows other text when a mod adds text.
    /// </summary>
    [Fact]
    public void APatchLineWithAnIdOfTheTextOfTheModsIsAnError()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var id = session.EditView.StringIds.DllIndex * 65536 + 1;
        session.Execute(Set(Gun("gun1"), "ids_name", new Int32Value(id)));
        // A key that holds no text id can have any number.
        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(id)));
        var before = TestWorkspace.Files(Act(ws));

        var result = session.Save();

        Assert.False(result.Saved);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(Gun("gun1"), problem.Target);
        Assert.Contains($"'ids_name = {id}'", problem.Message);
        Assert.EndsWith("Copy this text into the mod.", problem.Message);
        Assert.Equal(before, TestWorkspace.Files(Act(ws)));
        // An id of a DLL of the base data is not an id of the text of the mods.
        session.Execute(Set(Gun("gun1"), "ids_name", new Int32Value(id - 65536)));
        AssertSaved(session.Save());
    }

    /// <summary>
    /// A text edit changes the text of each placeholder of the mod. So the save checks the placeholders of the files
    /// that it keeps too: a patch that no edit touched and a whole INI file (rule S8).
    /// </summary>
    [Fact]
    public void ATextEditChecksEachPlaceholderOfTheMod()
    {
        using var ws = Workspace(new()
        {
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nids_name = $str:g1name",
            [@"files\" + ShipsPath] = "[Ship]\nnickname = s1\nids_info = $ifc:s1info",
            ["strings.json"] = "{ \"strings\": { \"g1name\": \"Good one\" }, \"infocards\": { \"s1info\": \"<RDL/>\" } }",
        });
        var before = TestWorkspace.Files(Act(ws));
        var session = ws.Session("act");

        session.Execute(EditCommand.SetText(StringKind.String, "g1name", null));
        var result = session.Save();

        Assert.False(result.Saved);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(PatchOf(ws, GoodsPath), problem.Source);
        Assert.Contains("[Good], line 3: '$str:g1name' is not in the strings.json", problem.Message);
        Assert.Equal(before, TestWorkspace.Files(Act(ws)));
        Assert.True(session.IsDirty);

        session.Undo();
        session.Execute(EditCommand.SetText(StringKind.Infocard, "s1info", null));
        result = session.Save();
        Assert.False(result.Saved);
        problem = Assert.Single(result.Problems);
        Assert.Equal(Act(ws, @"files\" + ShipsPath), problem.Source);
        Assert.Contains("'$ifc:s1info'", problem.Message);
        Assert.Equal(before, TestWorkspace.Files(Act(ws)));

        // Text that no file uses can go.
        session.Undo();
        session.Execute(EditCommand.SetText(StringKind.String, "extra", "Extra"));
        AssertSaved(session.Save());
        session.Execute(EditCommand.SetText(StringKind.String, "extra", null));
        AssertSaved(session.Save());
    }

    /// <summary>The active mod starts to replace a file with a whole file after the open. A patch of it then does
    /// nothing, so the save stops.</summary>
    [Fact]
    public void ANewWholeFileOfTheActiveModStopsTheSave()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(Set(Good("g2"), "price", new Int32Value(3)));
        Directory.CreateDirectory(Path.GetDirectoryName(Act(ws, @"files\" + GoodsPath))!);
        File.WriteAllText(Act(ws, @"files\" + GoodsPath), Goods);

        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains("replaces", Assert.Single(result.Problems).Message);
        Assert.False(File.Exists(PatchOf(ws, GoodsPath)));
    }

    /// <summary>A new patch file takes the letter case of the base file when no lower mod has the file (rule P8).</summary>
    [Fact]
    public void ANewPatchFileTakesTheLetterCaseOfTheBaseFile()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\Equipment\Market_Misc.ini", Market);
        ws.Mod("act", new());
        ws.WriteProfile("act");
        var session = ws.Session("act");

        session.Execute(EditCommand.AddSection(MarketPath, "BaseGood", [new EntryView("base", [new StringValue("b2")])]));
        AssertSaved(session.Save());

        Assert.Equal(["mod.json", @"patches\DATA\Equipment\Market_Misc.ini"], TestWorkspace.Files(Act(ws)).Keys);
    }

    /// <summary>
    /// A file that already has the content of the save needs no write, also when it changed outside the editor after
    /// the editor read it.
    /// </summary>
    [Fact]
    public void AFileThatAlreadyHasTheNewContentNeedsNoWrite()
    {
        using var direct = Workspace();
        var first = direct.Session("act");
        first.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        AssertSaved(first.Save());
        var saved = File.ReadAllBytes(PatchOf(direct, GoodsPath));
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        Directory.CreateDirectory(Path.GetDirectoryName(PatchOf(ws, GoodsPath))!);
        File.WriteAllBytes(PatchOf(ws, GoodsPath), saved);

        var result = session.Save();

        AssertSaved(result);
        Assert.Empty(result.Written);
        Assert.False(session.IsDirty);
        Assert.Equal(saved, File.ReadAllBytes(PatchOf(ws, GoodsPath)));

        // A file that the save deletes is gone already.
        session.Execute(Set(Good("g1"), "price", new Int32Value(100)));
        File.Delete(PatchOf(ws, GoodsPath));
        result = session.Save();
        AssertSaved(result);
        Assert.Empty(result.Deleted);
        Assert.False(session.IsDirty);
    }

    /// <summary>strings.json has sorted keys, no byte order mark, and changes only when the text changes (rule P9).</summary>
    [Fact]
    public void StringsJsonIsSortedAndWrittenOnlyWhenTheTextChanges()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetText(StringKind.String, "zeta", "Z"));
        session.Execute(EditCommand.SetText(StringKind.String, "alpha", "A"));
        session.Execute(EditCommand.SetText(StringKind.Infocard, "info", "<RDL><TEXT>x & y</TEXT></RDL>"));

        AssertSaved(session.Save());
        var path = Act(ws, "strings.json");
        Assert.Equal(
            "{\n  \"strings\": {\n    \"alpha\": \"A\",\n    \"zeta\": \"Z\"\n  },\n  \"infocards\": {\n    \"info\": \"<RDL><TEXT>x & y</TEXT></RDL>\"\n  }\n}\n",
            File.ReadAllText(path));
        Assert.Equal((byte)'{', File.ReadAllBytes(path)[0]);

        session.Execute(EditCommand.SetText(StringKind.String, "beta", "B"));
        session.Undo();
        Assert.Empty(session.Save().Written);

        session.Execute(EditCommand.Group("Remove all", [
            EditCommand.SetText(StringKind.String, "zeta", null),
            EditCommand.SetText(StringKind.String, "alpha", null),
            EditCommand.SetText(StringKind.Infocard, "info", null),
        ]));
        Assert.Equal([path], session.Save().Deleted);
        Assert.False(File.Exists(path));
    }

    /// <summary>strings.json keeps its other fields, its byte order mark and its line ends.</summary>
    [Fact]
    public void StringsJsonKeepsItsOtherFields()
    {
        using var ws = Workspace();
        var path = Act(ws, "strings.json");
        File.WriteAllBytes(path, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("{\r\n\"note\": \"keep me\",\r\n\"Strings\": { \"b\": \"B\" }\r\n}\r\n")]);
        var session = ws.Session("act");

        session.Execute(EditCommand.SetText(StringKind.String, "a", "A"));
        AssertSaved(session.Save());

        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(
            "{\r\n  \"note\": \"keep me\",\r\n  \"Strings\": {\r\n    \"a\": \"A\",\r\n    \"b\": \"B\"\r\n  }\r\n}\r\n")], File.ReadAllBytes(path));
    }

    /// <summary>A strings.json with comments cannot be written again, so the save writes nothing (F13).</summary>
    [Fact]
    public void AStringsJsonWithCommentsStopsTheSave()
    {
        using var ws = Workspace();
        var path = Act(ws, "strings.json");
        File.WriteAllText(path, "{\n  // names\n  \"strings\": { \"b\": \"B\" }\n}\n");
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "volume", new Int32Value(3)));
        session.Execute(EditCommand.SetText(StringKind.String, "a", "A"));

        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains("comment", Assert.Single(result.Problems).Message);
        Assert.False(File.Exists(PatchOf(ws, GoodsPath)));
        Assert.Equal("{\n  // names\n  \"strings\": { \"b\": \"B\" }\n}\n", File.ReadAllText(path));
    }

    /// <summary>A save transaction writes only files in the mod folder, and never in its .olsave folder (rule P6).</summary>
    [Fact]
    public void ASaveWritesOnlyInTheModFolder()
    {
        using var ws = Workspace();
        var outside = Path.Combine(ws.Root, "outside.ini");

        Assert.Throws<ArgumentException>(() =>
            SaveTransaction.Commit(Act(ws), [new FileChange(outside, [1])], []));
        Assert.Throws<ArgumentException>(() =>
            SaveTransaction.Commit(Act(ws), [new FileChange(Act(ws, @".olsave\x.ini"), [1])], []));
        Assert.Throws<ArgumentException>(() =>
            SaveTransaction.Commit(Act(ws), [new FileChange(Act(ws, @"..\low\mod.json"), [1])], []));

        Assert.False(File.Exists(outside));
        Assert.False(Directory.Exists(Act(ws, SaveTransaction.FolderName)));
    }
}
