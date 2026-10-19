using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ListWorkspace;

namespace OpenLancer.Tests;

/// <summary>The rows of the list picker, without ImGui (section 12.2 of the M21 design).</summary>
public sealed class ListPickerModelTests
{
    private static ListEntry Entry(string value, ValueOrigin origin = ValueOrigin.Base, bool unsaved = false) =>
        new(value, null, origin, origin == ValueOrigin.ActiveMod ? "act" : null, "type = STATION") { Unsaved = unsaved };

    private static readonly (ListEntry, string)[] Entries =
    [
        (Entry("li_b"), "Bravo"),
        (Entry("li_a"), "Alpha"),
        (Entry("li_c"), ""),
        (Entry("li_twin1"), "Twin"),
        (Entry("li_twin2"), "Twin"),
        (Entry("li_new", ValueOrigin.ActiveMod, unsaved: true), "New one"),
    ];

    [Fact]
    public void TheRowsAreNoneThenTheMissingValueThenTheEntriesByNameAndNickname()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_typo");

        Assert.Equal([ListRowKind.None, ListRowKind.Missing], model.Rows.Take(2).Select(r => r.Kind));
        Assert.Equal("Missing: li_typo", model.Rows[1].Text);
        // A name without text shows the nickname, and repeated names keep their own rows.
        Assert.Equal(["Alpha", "Bravo", "li_c", "New one", "Twin", "Twin"], model.Rows.Skip(2).Select(r => r.Text));
        Assert.Equal(["li_twin1", "li_twin2"], model.Rows.Where(r => r.Name == "Twin").Select(r => r.Value));
        Assert.Equal(1, model.Current);
        Assert.Equal("new", model.Rows.Single(r => r.Value == "li_new").Tag);
        Assert.Equal("", model.Rows.Single(r => r.Value == "li_a").Tag);
    }

    /// <summary>
    /// A rule with the flag exists accepts a value outside the list of files when its file exists (decision D160 of the
    /// M22 design). Its row reads "value (other folder)", not "Missing: value", and it is the current row.
    /// </summary>
    [Fact]
    public void AnExistingFileOutsideTheListIsNotMissing()
    {
        var files = new (ListEntry, string)[] { (Entry(@"SOLAR\NEBULA\cloud.ini"), "cloud") };
        var model = new ListPickerModel(files, none: false, current: @"solar\more\far_cloud.ini", currentExists: true);

        var row = model.Rows[0];
        Assert.Equal((ListRowKind.Other, @"solar\more\far_cloud.ini (other folder)"), (row.Kind, row.Text));
        Assert.Equal(0, model.Current);
        Assert.Contains("exists", ListPickerModel.Footer(row, "Nebula file", _ => 0).Single());
        // A filter shows only the entries.
        Assert.Equal([1], model.Visible("cloud"));

        // Without the file, the value is missing.
        var missing = new ListPickerModel(files, none: false, current: @"solar\more\far_cloud.ini");
        Assert.Equal("Missing: solar\\more\\far_cloud.ini", missing.Rows[0].Text);
    }

    [Fact]
    public void NoneShowsOnlyForAKeyThatIsNotRequired()
    {
        var required = new ListPickerModel(Entries, none: false, current: "");
        Assert.DoesNotContain(required.Rows, r => r.Kind == ListRowKind.None);
        Assert.Equal(-1, required.Current);

        var optional = new ListPickerModel(Entries, none: true, current: "");
        Assert.Equal(0, optional.Current);
        Assert.Equal("", optional.Rows[0].Value);
    }

    /// <summary>The current value is selected, and the list scrolls to it. The name compares without letter
    /// case.</summary>
    [Fact]
    public void TheCurrentValueIsSelected()
    {
        var model = new ListPickerModel(Entries, none: true, current: "LI_B");

        Assert.Equal("li_b", model.Rows[model.Current].Value);
        Assert.DoesNotContain(model.Rows, r => r.Kind == ListRowKind.Missing);
    }

    [Fact]
    public void TheFilterFindsNamesAndNicknamesAndHidesNoneAndMissing()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_typo");

        Assert.Equal(model.Rows.Count, model.Visible("").Count);
        Assert.Equal(["li_twin1", "li_twin2"], model.Visible("TWIN").Select(i => model.Rows[i].Value));
        Assert.Equal(["li_a"], model.Visible("alp").Select(i => model.Rows[i].Value));
        Assert.Empty(model.Visible("nothing"));
    }

    /// <summary>Enter assigns the selected row, or the first row when the filter hides the selection. Up and Down move
    /// in the visible rows.</summary>
    [Fact]
    public void EnterAndTheArrowKeysMoveInTheVisibleRows()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_a");
        var all = model.Visible("");
        var twins = model.Visible("twin");

        Assert.Equal(model.Current, ListPickerModel.EnterRow(all, model.Current));
        Assert.Equal(twins[0], ListPickerModel.EnterRow(twins, model.Current));
        Assert.Equal(-1, ListPickerModel.EnterRow([], model.Current));
        Assert.Equal("li_b", model.Rows[ListPickerModel.Step(all, model.Current, 1)].Value);
        Assert.Equal(0, ListPickerModel.Step(all, model.Current, -1));
        Assert.Equal(0, ListPickerModel.Step(all, 0, -1));
        Assert.Equal(twins[0], ListPickerModel.Step(twins, model.Current, 1));
        Assert.Equal(twins[^1], ListPickerModel.Step(twins, model.Current, -1));
    }

    [Fact]
    public void TheFooterTellsTheEntry()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_typo");
        var row = model.Rows.Single(r => r.Value == "li_new");

        var footer = ListPickerModel.Footer(row, "Loadout", e => e.Value == "li_new" ? 3 : 0);

        Assert.Equal(["New one (li_new)", "Loadout", "The active mod 'act' adds it. It is not saved.", "Used by 3 lines",
            "type = STATION"], footer);
        Assert.Equal(["li_typo: no loadout has this name."], ListPickerModel.Footer(model.Rows[1], "Loadout", _ => 0));
    }
}

/// <summary>The list widgets of the bound rows (section 12 of the M21 design).</summary>
public sealed class ListWidgetTests
{
    [Fact]
    public void AKeyWithOneRuleOnItsFirstValueGetsThePicker()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var lists = session.Lists;
        var loadout = lists.Rules(Li01("Li01_01"), "loadout");
        var gotos = lists.Rules(Li01("Li01_to_Li02"), "goto");
        var key = session.Schema(Li01("Li01_01"), "loadout");

        Assert.True(PropertyGrid.IsListed(loadout));
        Assert.False(PropertyGrid.IsListed(gotos));
        Assert.Equal(PropertyGrid.BoundWidget.List, PropertyGrid.WidgetOf(key, 1, 1, null, listed: true));
        Assert.Equal(PropertyGrid.BoundWidget.List, PropertyGrid.WidgetOf(key, 0, 0, null, listed: true));
        // More than one line keeps the text field, and a key without a list keeps the widget of its type.
        Assert.Equal(PropertyGrid.BoundWidget.Text, PropertyGrid.WidgetOf(key, 2, 1, null, listed: true));
        Assert.Equal(PropertyGrid.BoundWidget.Text, PropertyGrid.WidgetOf(key, 1, 1, null));
    }

    /// <summary>A one-line key with labeled rules on its values gets a slot for each value, with the label of the
    /// rule.</summary>
    [Fact]
    public void AKeyWithLabeledRulesGetsSlots()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var gotos = session.Lists.Rules(Li01("Li01_to_Li02"), "goto");
        var labels = PropertyGrid.SlotLabels(gotos)!;

        Assert.Equal(["System", "Exit object", "Tunnel"], labels(3)!.Select(l => l.Text));
        Assert.Equal(["System", "Exit object", "Tunnel", "Value 4"], labels(4)!.Select(l => l.Text));
        Assert.Null(PropertyGrid.SlotLabels(session.Lists.Rules(Li01("Li01_01"), "loadout")));
        Assert.Equal(PropertyGrid.BoundWidget.Components,
            PropertyGrid.WidgetOf(session.Schema(Li01("Li01_to_Li02"), "goto"), 1, 3, labels));
    }

    /// <summary>A pick of a line of a multiline key or of a slot keeps the other lines and values.</summary>
    [Fact]
    public void APickKeepsTheOtherLinesAndValues()
    {
        Assert.Equal("gun1, HpWeapon01\ngun2, HpWeapon02",
            PropertyGrid.PickedLine("gun1, HpWeapon01\ngun_typo, HpWeapon02", 1, 0, "gun2"));
        Assert.Equal("Li02, Li02_to_Li01, gate_x", PropertyGrid.PickedLine("Li02, typo, gate_x", 0, 1, "Li02_to_Li01"));
        Assert.Equal("a", PropertyGrid.PickedLine("a", 3, 0, "b"));
    }

    /// <summary>Decision D123: a section of a file whose schema the editor does not know is read-only in the
    /// Inspector, for example the [Solar] that the follow button of an archetype selects.</summary>
    [Fact]
    public void ASectionOfAFileWithoutASchemaIsReadOnly()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var solar = new SectionRef(SolarPath, new SectionIdentity("Solar", "station1"));

        var field = FieldBinding.Create(session, solar, session.Get(solar)!, "loadout");

        Assert.Equal(FieldBinding.NoSchemaReason, field.ReadOnlyReason);
        Assert.False(session.KnowsSchema(SolarPath));
        Assert.True(session.KnowsSchema(Li01Path));
        Assert.True(session.KnowsSchema(EquipmentPath));
        Assert.Null(FieldBinding.Create(session, Li01("Li01_01"), session.Get(Li01("Li01_01"))!, "loadout").ReadOnlyReason);
    }

    [Fact]
    public void AnEnumListHoldsTheNamesOfTheEnum()
    {
        var list = ListView.Of(typeof(LibreLancer.Data.Schema.Solar.DockSphereType));

        Assert.True(list.IsEnum);
        Assert.NotNull(list.Find("BERTH"));
        Assert.Null(list.Find("no_such_type"));
        Assert.Same(list, ListView.Of(typeof(LibreLancer.Data.Schema.Solar.DockSphereType)));

        // The picker selects the current name of the enum. A name outside the enum gets no "Missing" row.
        var rows = list.Entries.Select(e => (e, e.Value)).ToList();
        var current = new ListPickerModel(rows, none: true, PropertyGrid.PickerValue(list, "BERTH"));
        Assert.Equal("berth", current.Rows[current.Current].Value, ignoreCase: true);
        var outside = new ListPickerModel(rows, none: true, PropertyGrid.PickerValue(list, "no_such_type"));
        Assert.DoesNotContain(outside.Rows, r => r.Kind == ListRowKind.Missing);
        Assert.Equal(ListRowKind.None, outside.Rows[outside.Current].Kind);
    }
}
