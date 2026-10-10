using System.Collections.Generic;
using System.Linq;
using OpenLancer.Editor.Database;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

public sealed class DatabaseTableTests
{
    // The data columns: nickname, name, type and price. Only the price is numeric.
    private static readonly bool[] Numeric = [false, false, false, true];

    private static readonly ObjectKind Gun = new("Gun", EditorIcons.Bullseye, EditorTheme.Text);

    private static DatabaseRow Row(int index, string nickname, string name, string type, double price) =>
        new(index, nickname, type == "Gun" ? Gun : ObjectKinds.AnyEquipment,
            [nickname, name, type, price.ToString(System.Globalization.CultureInfo.InvariantCulture)], [0, 0, 0, price]);

    private static List<DatabaseRow> Rows() =>
    [
        Row(0, "li_gun01", "Justice", "Gun", 500),
        Row(1, "br_gun01", "Ripper", "Gun", 3710),
        Row(2, "li_shield01", "Shield", "Shield", 500),
        Row(3, "Ku_gun01", "Kunai", "Gun", 1200),
        Row(4, "no_price", "", "Commodity", double.NaN),
    ];

    private static string[] Nicknames(IEnumerable<DatabaseRow> rows) => rows.Select(r => r.Nickname).ToArray();

    [Fact]
    public void SortsATextColumnWithoutCase()
    {
        var rows = Rows();
        DatabaseTable.Sort(rows, [new SortKey(0, false)], Numeric);
        Assert.Equal(["br_gun01", "Ku_gun01", "li_gun01", "li_shield01", "no_price"], Nicknames(rows));
    }

    [Fact]
    public void SortsANumericColumnByItsNumberAndPutsAMissingNumberLastWhenDescending()
    {
        var rows = Rows();
        DatabaseTable.Sort(rows, [new SortKey(3, true)], Numeric);
        // "3710" sorts before "500" as text, and after it as a number. The ties keep the order of the game data.
        Assert.Equal(["br_gun01", "Ku_gun01", "li_gun01", "li_shield01", "no_price"], Nicknames(rows));
    }

    [Fact]
    public void UsesTheSecondKeyOnlyForRowsWithTheSameFirstKey()
    {
        var rows = Rows();
        DatabaseTable.Sort(rows, [new SortKey(2, false), new SortKey(3, true)], Numeric);
        Assert.Equal(["no_price", "br_gun01", "Ku_gun01", "li_gun01", "li_shield01"], Nicknames(rows));
    }

    [Fact]
    public void KeepsTheGameDataOrderForRowsWithTheSameKeys()
    {
        var rows = Rows();
        rows.Reverse();
        DatabaseTable.Sort(rows, [new SortKey(3, false)], Numeric);
        // The two rows with price 500 are in the order of their index, not in the order of the list.
        Assert.Equal(["no_price", "li_gun01", "li_shield01", "Ku_gun01", "br_gun01"], Nicknames(rows));
    }

    [Fact]
    public void FilterMatchesTheNicknameOrTheNameWithoutCase()
    {
        Assert.Equal(["li_gun01", "li_shield01"], Nicknames(DatabaseTable.Filter(Rows(), "LI_", null)));
        Assert.Equal(["br_gun01"], Nicknames(DatabaseTable.Filter(Rows(), "ripp", null)));
        Assert.Equal(5, DatabaseTable.Filter(Rows(), "", null).Count());
        Assert.Empty(DatabaseTable.Filter(Rows(), "xyz", null));
    }

    [Fact]
    public void FilterKeepsOnlyTheRowsOfTheKind()
    {
        var any = ObjectKinds.AnyEquipment;
        Assert.Equal(["li_shield01", "no_price"], Nicknames(DatabaseTable.Filter(Rows(), "", any)));
        Assert.Equal(["no_price"], Nicknames(DatabaseTable.Filter(Rows(), "price", any)));
    }

    [Fact]
    public void ARowOfAMarkedItemHasTheMark()
    {
        var table = DatabaseTable.Create("##test", "items", ["a", "b", "c"],
            [new Column<string>("Nickname", t => t, 100), new Column<string>("Name", t => t.ToUpperInvariant(), 100)],
            _ => Gun, marked: t => t == "b");

        Assert.Equal([false, true, false], table.Rows.Select(r => r.Marked));
        Assert.Equal(["A", "B", "C"], table.Rows.Select(r => r.Name));
    }
}
