using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The commands and the document state that the scene edits of milestone M3 need: a rename of a section of the lower
/// layer (<c>ol.rename</c>), a change of one value in place, the removal of a run of lines, and the renames of a
/// change.
/// </summary>
public sealed class EditCommandSceneTests
{
    private const string ZonePath = @"DATA\zones.ini";

    private const string Zones = """
        [Zone]
        nickname = z1
        encounter = e1, 5, 0.4
        faction = f1, 1
        encounter = e2, 5, 0.2
        faction = f2, 1
        shape = SPHERE

        [Exclusion Zones]
        exclusion = z1
        fog_far = 100
        exclusion = z2
        exclusion = z1
        zone_shell = x
        """;

    [Fact]
    public void SetValueAtChangesOneValueInPlaceAndKeepsTheOrderOfAGroup()
    {
        using var ws = Workspace(Zones);
        var session = ws.Session("act");
        var zone = new SectionRef(ZonePath, new SectionIdentity("Zone", "z1"));

        session.Execute(EditCommand.SetValueAt(zone, "faction", 1, 0, new StringValue("f9")));

        Assert.Equal(["nickname", "encounter", "faction", "encounter", "faction", "shape"],
            session.Get(zone)!.Entries.Select(e => e.Name));
        Assert.Equal(["f1, 1", "f9, 1"], Values(session, zone, "faction"));
        Assert.Throws<ArgumentException>(() => session.Execute(EditCommand.SetValueAt(zone, "faction", 2, 0, new StringValue("x"))));
        Assert.Throws<InvalidOperationException>(() =>
            session.Execute(EditCommand.SetValueAt(zone, "nickname", 0, 0, new StringValue("x"))));
        Assert.True(session.Save().Saved);
        // The writer writes the whole group of lines that belong together.
        Assert.Contains("faction = f9, 1", ws.ReadModPatch(ZonePath));
    }

    [Fact]
    public void RemoveEntriesRemovesARunOfLinesAndKeepsTheOthers()
    {
        using var ws = Workspace(Zones);
        var session = ws.Session("act");
        var holes = new SectionRef(ZonePath, new SectionIdentity("Exclusion Zones", null));

        session.Execute(EditCommand.RemoveEntries(holes, 3, 2));
        session.Execute(EditCommand.RemoveEntries(holes, 0, 2));

        Assert.Equal(["exclusion = z2"], session.Get(holes)!.Entries.Select(e => $"{e.Name} = {e.Values[0]}"));
        Assert.Throws<ArgumentException>(() => session.Execute(EditCommand.RemoveEntries(holes, 1, 1)));
        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.RemoveEntries(
            new SectionRef(ZonePath, new SectionIdentity("Zone", "z1")), 0, 1)));
        Assert.True(session.Save().Saved);
        // The writer writes the whole group of lines that belong together, and removes the names that W does not have.
        Assert.Equal(PatchWriterTests.Lines("[Exclusion Zones]", "exclusion = z2", "ol.remove = fog_far, zone_shell"),
            ws.ReadModPatch(ZonePath));
    }

    /// <summary>A rename of a section of the lower layer keeps its place, and the change of the rename, its undo and
    /// its redo name the pair of references.</summary>
    [Fact]
    public void ARenameOfALowerSectionGivesItsPairsInEachChange()
    {
        using var ws = EditSessionTests.Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        session.Execute(EditCommand.Rename(Good("g1"), "g9"));
        session.Undo();
        session.Redo();

        Assert.Equal([(Good("g1"), Good("g9"))], changes[0].Renames);
        Assert.Equal([(Good("g9"), Good("g1"))], changes[1].Renames);
        Assert.Equal([(Good("g1"), Good("g9"))], changes[2].Renames);
        Assert.All(changes, c => Assert.True(c.Structural));
        Assert.Equal([Good("g9"), Good("g2")], session.Sections(GoodsPath));
        // An edit that renames nothing has no pairs.
        session.Execute(Set(Good("g9"), "price", new Int32Value(5)));
        Assert.Empty(changes[3].Renames);
    }

    /// <summary>A revert of a renamed section of the lower layer gives it its old key, so its change names the pair, as
    /// a rename does. The scene then keeps the item of the section.</summary>
    [Fact]
    public void ARevertOfARenamedSectionEndsTheRenameAndGivesThePair()
    {
        using var ws = EditSessionTests.Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Execute(EditCommand.Rename(Good("g1"), "g9"));
        session.Changed += changes.Add;

        session.Execute(EditCommand.Revert(Good("g9")));
        session.Undo();
        session.Execute(EditCommand.Revert(Good("g9"), "nickname"));

        Assert.Equal([(Good("g9"), Good("g1"))], changes[0].Renames);
        Assert.True(changes[0].Structural);
        Assert.Equal([(Good("g1"), Good("g9"))], changes[1].Renames);
        Assert.Equal([(Good("g9"), Good("g1"))], changes[2].Renames);
        Assert.Equal([Good("g1"), Good("g2")], session.Sections(GoodsPath));
        Assert.Empty(session.OpenDocument(GoodsPath)!.State.Renamed);
        Assert.Empty(session.OpenDocument(GoodsPath)!.Write().Sections);
        // A revert that keeps the key names no pair.
        session.Execute(Set(Good("g1"), "price", new Int32Value(5)));
        session.Execute(EditCommand.Revert(Good("g1")));
        Assert.Empty(changes[^1].Renames);
        Assert.False(changes[^1].Structural);
    }

    [Fact]
    public void TheDocumentRecordsTheRenameAfterAnEditAnUndoARedoAndALoad()
    {
        using var ws = EditSessionTests.Workspace();
        var session = ws.Session("act");
        var document = session.OpenDocument(GoodsPath)!;

        session.Execute(EditCommand.Rename(Good("g1"), "g9"));
        var g9 = Good("g9").Id;
        Assert.Equal(Good("g1").Id, document.State.Renamed[g9]);
        // A rename is not a delete (review finding R19).
        Assert.Empty(document.State.Deleted);
        Assert.True(document.IsRenamed(g9));
        Assert.Equal(Good("g1").Id, document.BaseIdOf(g9));
        session.Undo();
        Assert.Empty(document.State.Renamed);
        session.Redo();
        Assert.True(document.IsRenamed(g9));

        Assert.True(session.Save().Saved);
        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = g1", "ol.rename = g9"), ws.ReadModPatch(GoodsPath));
        // The save opens the document again from the saved patch, so the load gives the rename.
        var reopened = session.OpenDocument(GoodsPath)!;
        Assert.Equal(Good("g1").Id, reopened.State.Renamed[g9]);
        Assert.Empty(reopened.State.Deleted);
        Assert.False(session.Get(Good("g9"))!.IsNew);
    }

    [Fact]
    public void ARenameBackGivesNoPatchAndADeleteAfterARenameDeletesTheOldKey()
    {
        using var ws = EditSessionTests.Workspace();
        var session = ws.Session("act");

        session.Execute(EditCommand.Rename(Good("g1"), "g9"));
        session.Execute(EditCommand.Rename(Good("g9"), "g1"));
        Assert.Empty(session.OpenDocument(GoodsPath)!.Write().Sections);

        session.Execute(EditCommand.Rename(Good("g1"), "g9"));
        session.Execute(Set(Good("g9"), "price", new Int32Value(7)));
        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = g1", "ol.rename = g9", "price = 7"),
            session.OpenDocument(GoodsPath)!.Write().Text);

        session.Execute(EditCommand.DeleteSection(Good("g9")));
        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = g1", "ol.delete = true"),
            session.OpenDocument(GoodsPath)!.Write().Text);
    }

    /// <summary>The identities of the child sections hold their parent, so the editor does not rename a section of the
    /// lower layer with child sections (review finding R20). A child section of the lower layer cannot get a new key
    /// either.</summary>
    [Fact]
    public void ALowerSectionWithChildSectionsKeepsItsKey()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(ShipsPath, "[Ship]\nnickname = s1\n[CollisionGroup]\nnickname = c1\nobj = a\n[Ship]\nnickname = s2");
        ws.Mod("act", new());
        ws.WriteProfile("act");
        var session = ws.Session("act");

        var parent = Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.Rename(Ship("s1"), "s9")));
        Assert.Contains("with child sections", parent.Message);
        var child = new SectionRef(ShipsPath, new SectionIdentity("CollisionGroup", "c1", Ship("s1").Id));
        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.Rename(child, "c2")));
        session.Execute(EditCommand.Rename(Ship("s2"), "s3"));
        Assert.NotNull(session.Get(Ship("s3")));
    }

    /// <summary>A loaded patch with <c>ol.rename</c> and comments saves again byte for byte, and a change keeps its
    /// text.</summary>
    [Fact]
    public void ALoadedRenamePatchKeepsItsText()
    {
        const string patch = "; renames\r\n[Good]\r\nnickname = g1 ; the old key\r\nol.rename = g9\r\nprice = 150\r\n";
        using var ws = EditSessionTests.Workspace(new() { [@"patches\" + GoodsPath] = patch });
        var session = ws.Session("act");
        var document = session.OpenDocument(GoodsPath)!;

        Assert.Equal(patch, document.Write().Text);
        Assert.Null(document.ChangedPatch());
        Assert.Equal(["150"], Values(session, Good("g9"), "price"));

        session.Execute(Set(Good("g9"), "volume", new Int32Value(2)));
        Assert.Equal("; renames\r\n[Good]\r\nnickname = g1 ; the old key\r\nol.rename = g9\r\nprice = 150\r\nvolume = 2\r\n",
            document.Write().Text);
    }

    /// <summary>The recovery file holds the patch text, so a rename comes back as a rename (review finding R26).</summary>
    [Fact]
    public void ARenameSurvivesTheRecoveryFile()
    {
        using var ws = EditSessionTests.Workspace();
        var first = ws.Session("act");
        first.Execute(EditCommand.Rename(Good("g1"), "g9"));
        Assert.Empty(first.WriteRecovery());
        first.Dispose();

        var session = ws.Session("act");
        Assert.Empty(session.RestoreRecovery());

        Assert.Null(session.Get(Good("g1")));
        Assert.False(session.Get(Good("g9"))!.IsNew);
        Assert.True(session.OpenDocument(GoodsPath)!.IsRenamed(Good("g9").Id));
        Assert.True(session.Save().Saved);
        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = g1", "ol.rename = g9"), ws.ReadModPatch(GoodsPath));
    }

    private static TestWorkspace Workspace(string zones)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(ZonePath, zones);
        ws.Mod("act", new());
        ws.WriteProfile("act");
        return ws;
    }
}
