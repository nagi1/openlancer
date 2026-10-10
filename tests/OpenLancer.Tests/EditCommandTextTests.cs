using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The edits that the Inspector makes: lines from the text of their values, Revert to the lower layer, and the change
/// state of a section.
/// </summary>
public sealed class EditCommandTextTests
{
    private const string BinaryGoodsPath = @"DATA\EQUIPMENT\bini_goods.ini";

    private static SectionRef BinaryGood(string nickname) => new(BinaryGoodsPath, new SectionIdentity("Good", nickname));

    /// <summary>The workspace of <see cref="EditSessionTests.Workspace"/> with a binary goods file: a float price
    /// and a boolean stored as the text <c>TRUE</c>, as retail files store them.</summary>
    private static TestWorkspace BinaryWorkspace(Dictionary<string, string>? activeFiles = null)
    {
        var ws = Workspace(activeFiles);
        var good = new Section("Good");
        good.Add(Line(good, "nickname", new StringValue("bg1")));
        good.Add(Line(good, "price", new SingleValue(100, null)));
        good.Add(Line(good, "combinable", new StringValue("TRUE")));
        ws.WriteBaseBini(BinaryGoodsPath, good);
        return ws;
    }

    private static Entry Line(Section section, string name, params ValueBase[] values)
    {
        var entry = new Entry(section, name);
        foreach (var value in values)
            entry.Add(value);
        return entry;
    }

    /// <summary>The type and the text of each value of the lines of a name, for example <c>float:2.5</c>.</summary>
    private static string[] Typed(EditSession session, SectionRef target, string name) =>
        session.Get(target)!.Lines(name)
            .Select(l => string.Join(", ", l.Values.Select(v => v switch
            {
                BooleanValue => "bool",
                Int32Value => "int",
                SingleValue => "float",
                _ => "string",
            } + ":" + v)))
            .ToArray();

    [Fact]
    public void TextGivesTheValuesThatTheMergeReadsFromAPatchLine()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Good("g1"), "volume", ["1,2.5 ,  li_gun01, true"]));
        session.Execute(EditCommand.SetLinesFromText(Good("g1"), "price", ["7", "8"]));
        session.Execute(EditCommand.SetLinesFromText(Good("g1"), "separable", [""]));

        Assert.Equal(["int:1, float:2.5, string:li_gun01, bool:True"], Typed(session, Good("g1"), "volume"));
        Assert.Equal(["int:7", "int:8"], Typed(session, Good("g1"), "price"));
        Assert.Empty(Assert.Single(session.Get(Good("g1"))!.Lines("separable")).Values);
        Assert.Equal("Set separable", session.UndoLabel);
    }

    [Fact]
    public void InABinaryFileTheTextGivesTheTypesThatTheMergeStores()
    {
        using var ws = BinaryWorkspace();
        var session = ws.Session("act");

        // An integer that replaces a float is a float. A boolean is text. The old text stays when the truth value
        // stays.
        session.Execute(EditCommand.SetLinesFromText(BinaryGood("bg1"), "price", ["200"]));
        session.Execute(EditCommand.SetLinesFromText(BinaryGood("bg1"), "combinable", ["true"]));
        session.Execute(EditCommand.SetLinesFromText(BinaryGood("bg1"), "volume", ["false, 3"]));

        Assert.Equal(["float:200"], Typed(session, BinaryGood("bg1"), "price"));
        Assert.Equal(["string:TRUE"], Typed(session, BinaryGood("bg1"), "combinable"));
        Assert.Equal(["string:false, int:3"], Typed(session, BinaryGood("bg1"), "volume"));
    }

    [Fact]
    public void AnEditFromTextSavesAndOpensAgainWithTheSameValues()
    {
        using var ws = BinaryWorkspace();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(BinaryGood("bg1"), "price", ["250.5"]));
        session.Execute(EditCommand.SetLinesFromText(BinaryGood("bg1"), "combinable", ["false"]));
        session.Execute(EditCommand.SetLinesFromText(BinaryGood("bg1"), "ids_name", ["-1980013182"]));
        var edited = Typed(session, BinaryGood("bg1"), "price")
            .Concat(Typed(session, BinaryGood("bg1"), "combinable"))
            .Concat(Typed(session, BinaryGood("bg1"), "ids_name"))
            .ToArray();

        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        var reopened = ws.Session("act");
        Assert.Equal(edited, Typed(reopened, BinaryGood("bg1"), "price")
            .Concat(Typed(reopened, BinaryGood("bg1"), "combinable"))
            .Concat(Typed(reopened, BinaryGood("bg1"), "ids_name")));
        Assert.Equal(["float:250.5", "string:false", "int:-1980013182"], edited);
    }

    [Theory]
    [InlineData("1; 2")]
    [InlineData("café")]
    [InlineData("1\n2")]
    public void TextThatAPatchLineCannotHoldChangesNothing(string text)
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        Assert.Throws<ArgumentException>(() => session.Execute(EditCommand.SetLinesFromText(Good("g1"), "price", [text])));

        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ABinaryFileRefusesALargeNumberThatItCannotStore()
    {
        using var ws = BinaryWorkspace();
        var session = ws.Session("act");

        var error = Assert.Throws<ArgumentException>(() =>
            session.Execute(EditCommand.SetLinesFromText(BinaryGood("bg1"), "ids_info", ["4000000000"])));

        Assert.Contains("signed form", error.Message);
        Assert.Empty(session.Get(BinaryGood("bg1"))!.Lines("ids_info"));
    }

    [Fact]
    public void TextCannotChangeTheKey()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        Assert.Throws<InvalidOperationException>(() =>
            session.Execute(EditCommand.SetLinesFromText(Good("g1"), "nickname", ["g9"])));
    }

    [Fact]
    public void RevertOfANamePutsBackTheLowerLinesAndTheSaveDropsThePatchLine()
    {
        using var ws = Workspace(new()
        {
            [@"patches\" + GoodsPath] = """
                [Good]
                nickname = g1
                price = 5
                volume = 9
                """,
        });
        var session = ws.Session("act");
        Assert.Equal(ValueOrigin.ActiveMod, session.Origin(Good("g1"), "price").Origin);

        session.Execute(EditCommand.Revert(Good("g1"), "price"));

        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        Assert.Equal(ValueOrigin.Base, session.Origin(Good("g1"), "price").Origin);
        Assert.Equal("Revert price", session.UndoLabel);
        Assert.True(session.Save().Saved);
        var patch = File.ReadAllText(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath));
        Assert.DoesNotContain("price", patch);
        Assert.Contains("volume = 9", patch);
    }

    [Fact]
    public void RevertOfASectionPutsBackTheLowerSection()
    {
        using var ws = Workspace(new()
        {
            [@"patches\" + GoodsPath] = """
                [Good]
                nickname = g1
                price = 100
                """,
        });
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "volume", new Int32Value(4)));
        Assert.True(session.Get(Good("g1"))!.IsChanged);

        session.Execute(EditCommand.Revert(Good("g1")));

        Assert.False(session.Get(Good("g1"))!.IsChanged);
        Assert.Equal(["1"], Values(session, Good("g1"), "volume"));
        Assert.True(session.Save().Saved);
        // The pinned price line went with the revert, so the patch has no section.
        Assert.False(File.Exists(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath)));
    }

    [Fact]
    public void RevertOfAnUnchangedNameOrSectionAddsNoEdit()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(EditCommand.Revert(Good("g1"), "price"));
        session.Execute(EditCommand.Revert(Good("g2")));

        Assert.False(session.CanUndo);
    }

    [Fact]
    public void RevertRefusesASectionThatTheActiveModAdds()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(GoodsPath, "Good", [new EntryView("nickname", [new StringValue("g9")])]));

        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.Revert(Good("g9"))));
        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.Revert(Good("g9"), "nickname")));
    }

    [Fact]
    public void RevertOfOneNameOfALinkedGroupIsRefused()
    {
        using var ws = Workspace();
        ws.WriteBase(@"DATA\UNIVERSE\zones.ini", """
            [Zone]
            nickname = z1
            encounter = e1, 1, 0.5
            faction = f1, 1
            """);
        var session = ws.Session("act");
        var zone = new SectionRef(@"DATA\UNIVERSE\zones.ini", new SectionIdentity("Zone", "z1"));
        session.Execute(EditCommand.SetLines(zone, "encounter", [[new StringValue("e2"), new Int32Value(1), new SingleValue(0.5f, null)]]));

        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.Revert(zone, "faction")));
        session.Execute(EditCommand.Revert(zone));
        Assert.False(session.Get(zone)!.IsChanged);
    }

    [Fact]
    public void ASectionIsChangedWhenTheActiveModAddsOrChangesIt()
    {
        using var ws = Workspace(new()
        {
            [@"patches\" + GoodsPath] = """
                [Good]
                nickname = g2
                price = 200
                """,
        });
        var session = ws.Session("act");

        var g1 = session.Get(Good("g1"))!;
        Assert.False(g1.IsNew);
        Assert.False(g1.IsChanged);
        // A patch line that sets the lower value still pins it.
        Assert.True(session.Get(Good("g2"))!.IsChanged);

        session.Execute(Set(Good("g1"), "price", new Int32Value(1)));
        Assert.True(session.Get(Good("g1"))!.IsChanged);
        session.Undo();
        Assert.False(session.Get(Good("g1"))!.IsChanged);

        session.Execute(EditCommand.AddSection(GoodsPath, "Good", [new EntryView("nickname", [new StringValue("g9")])]));
        Assert.True(session.Get(Good("g9"))!.IsNew);
        Assert.True(session.Get(Good("g9"))!.IsChanged);
    }

    [Fact]
    public void ASessionWithoutAnActiveModShowsNoChange()
    {
        using var ws = Workspace(new()
        {
            [@"patches\" + GoodsPath] = """
                [Good]
                nickname = g1
                price = 5
                """,
        });
        var session = EditSession.Open(ModProfile.Load(ws.ProfilePath), null, TestWorkspace.FakeDllWriter.Instance);

        // The edit view is the whole profile, and the lower layer is the same view.
        Assert.Equal(["5"], Values(session, Good("g1"), "price"));
        Assert.False(session.Get(Good("g1"))!.IsChanged);
    }
}
