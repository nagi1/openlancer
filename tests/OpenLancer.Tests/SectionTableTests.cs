using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using Xunit;
using static OpenLancer.Tests.ListWorkspace;

namespace OpenLancer.Tests;

/// <summary>The shared section index of the edit view (decision D121, section 8.1 of the M21 design).</summary>
public sealed class SectionTableTests
{
    [Fact]
    public void AnEditOfOneFileBuildsOnlyTheTableOfThatFileAgain()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var table = session.Table;
        var li01 = table.File(Li01Path)!;
        var loadouts = table.File(ShipLoadoutsPath)!;
        var builds = table.Builds;
        Assert.Same(li01, table.File(Li01Path));
        Assert.Equal(builds, table.Builds);

        session.Execute(EditCommand.SetLinesFromText(Li01("Li01_01"), "pos", ["1, 2, 3"]));

        Assert.Same(loadouts, table.File(ShipLoadoutsPath));
        var edited = table.File(Li01Path)!;
        Assert.NotSame(li01, edited);
        Assert.Equal(builds + 1, table.Builds);
        Assert.Equal(["Li01_01", "Li01_to_Li02", "ring1", "ring2", "Li01_child"],
            edited.OfType("OBJECT").Select(i => edited.Ids[i].Key));
        Assert.Null(table.File(@"DATA\no_such_file.ini"));
    }

    /// <summary>A call of the item index at the same revision opens no document, and an edit of a file that is not
    /// an item file keeps its maps.</summary>
    [Fact]
    public void TheItemIndexReadsNoFileAtTheSameRevision()
    {
        var opened = new List<string>();
        var documents = new Dictionary<string, EditDocument>
        {
            [@"DATA\SHIPS\shiparch.ini"] = Document(@"DATA\SHIPS\shiparch.ini", "[Ship]\nnickname = s1\n"),
            [@"DATA\EQUIPMENT\goods.ini"] = Document(@"DATA\EQUIPMENT\goods.ini", "[Good]\nnickname = g1\n"),
        };
        var table = new SectionTable(path =>
        {
            opened.Add(path);
            return documents.GetValueOrDefault(path);
        });
        var revision = 1;
        var index = new ItemIndex([(@"DATA\SHIPS\shiparch.ini", ItemFileKind.Ships), (@"DATA\EQUIPMENT\goods.ini",
            ItemFileKind.Goods)], table, () => revision);

        Assert.NotNull(index.Find(ItemFileKind.Ships, "s1"));
        var reads = opened.Count;
        Assert.NotNull(index.Find(ItemFileKind.Goods, "g1"));
        Assert.Single(index.List(ItemFileKind.Ships));
        Assert.Equal(reads, opened.Count);

        revision++;
        Assert.NotNull(index.Find(ItemFileKind.Ships, "s1"));
        Assert.Equal(reads + 2, opened.Count);
        Assert.Equal(2, table.Builds);
    }

    private static EditDocument Document(string path, string text) =>
        EditDocument.Open(path, IniDocument.Parse(path, Encoding.ASCII.GetBytes(text)), null, []);
}
