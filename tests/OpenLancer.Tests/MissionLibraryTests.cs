using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The items of the mission library and of quick add (section 8.3 of the M6b design), and the first line of
/// each item.</summary>
public sealed class MissionLibraryTests
{
    private sealed class NoEffect : IEngineCoverage
    {
        public EngineMark Of(string name) => MissionIndex.Is(name, "Act_PlayMusic") ? EngineMark.NoEffect : EngineMark.Runs;
    }

    [Fact]
    public void TheLibraryHasTheTenEventsTheActionsByGroupAndTheConditionsByEventGroup()
    {
        var sections = MissionLibrary.Sections(null);
        var schema = MissionSchema.Retail;

        Assert.Equal(["Events", "Actions", "Conditions"], sections.Select(s => s.Title));
        Assert.Equal(10, sections[0].Items.Count());
        Assert.Equal(17, sections[1].Groups.Count);
        Assert.Equal(schema.Actions.Count, sections[1].Items.Count());
        Assert.Equal(schema.Conditions.Count, sections[2].Items.Count());
        Assert.Equal(["Start", "Talk", "Place", "Distance", "Combat", "Story"], sections[2].Groups.Select(g => g.Title));
        Assert.All(sections.SelectMany(s => s.Items), i =>
        {
            Assert.NotEmpty(i.Help.WhatItIs);
            Assert.NotEmpty(i.Help.WhatItWrites);
        });
    }

    [Fact]
    public void AnItemHasTheMarksOfItsName()
    {
        var items = MissionLibrary.Sections(new NoEffect()).SelectMany(s => s.Items).ToList();

        var music = items.Single(i => i.Key == "Act_PlayMusic");
        Assert.Equal("OpenLancer: no effect", music.OpenLancerMark);
        Assert.Null(items.Single(i => i.Key == "Act_SpawnShip").OpenLancerMark);
        Assert.Equal(MissionSchema.Retail.Actions.Where(a => a.NoRetailExample).Select(a => a.Name).Order(),
            items.Where(i => i.Kind == LibraryKind.Action && i.NoRetailExample).Select(i => i.Key).Order());
    }

    [Fact]
    public void TheSearchMatchesEachWordInTheNameTheKeyOrTheGroup()
    {
        var sections = MissionLibrary.Sections(null);

        Assert.Contains(MissionLibrary.Search(sections, "spawn ship"), i => i.Key == "Act_SpawnShip");
        Assert.Contains(MissionLibrary.Search(sections, "Cnd_Timer"), i => i.Key == "Cnd_Timer");
        Assert.All(MissionLibrary.Search(sections, "comms"), i => Assert.Contains("comm", i.Name + i.Key + i.Group,
            System.StringComparison.OrdinalIgnoreCase));
        Assert.Empty(MissionLibrary.Search(sections, "no such item at all"));
    }

    [Fact]
    public void TheFirstLineOfAnItemGivesEachValueThatTheLineNeeds()
    {
        var items = MissionLibrary.Sections(null).SelectMany(s => s.Items).ToList();

        Assert.Equal(LineSpec.Raw("Cnd_True", "no_params"), MissionLibrary.Line(items.Single(i => i.Id == "atOnce")));
        Assert.Equal(LineSpec.Of("Cnd_Timer", ("seconds", MissionLibrary.DefaultDelay)),
            MissionLibrary.Line(items.Single(i => i.Id == "afterDelay")));
        Assert.Equal("accept", MissionLibrary.Line(items.Single(i => i.Id == "missionAnswer")).Values["answer"].Text);
        Assert.Equal("outside", MissionLibrary.Line(items.Single(i => i.Id == "leaveArea")).Values["side"].Text);
        var distVec = MissionLibrary.Starter(MissionSchema.Retail.Name("Cnd_DistVec")!);
        Assert.Equal(MissionValue.Of(0, 0, 0), distVec.Values["point"]);
        Assert.Equal(MissionLibrary.NoName, distVec.Values["ship"].Text);
        Assert.False(distVec.Values.ContainsKey("hold"));
    }

    /// <summary>Each item gives a line that the mission layer takes in a new trigger, so a drop never refuses.</summary>
    [Fact]
    public void EachItemGivesALineThatTheLayerTakes()
    {
        using var m = MissionEdits.Create();
        foreach (var item in MissionLibrary.Sections(null).SelectMany(s => s.Items))
        {
            var add = item.Kind == LibraryKind.Action
                ? new AddTrigger(MissionEdits.TestScript) { Actions = [MissionLibrary.Line(item)] }
                : new AddTrigger(MissionEdits.TestScript) { Condition = MissionLibrary.Line(item) };
            var result = m.Missions.Preview(new MissionBatch("Test", [add]));

            Assert.True(result.Status is OpenLancer.Data.Editing.Commands.EditStatus.Previewed, $"{item.Key}: " +
                string.Join(" ", result.Errors.Select(e => e.Message)));
        }
    }

    [Fact]
    public void ASearchFindsAnEventByItsId()
    {
        var found = MissionLibrary.Search(MissionLibrary.Sections(null), "afterdelay");

        Assert.Equal("afterDelay", Assert.Single(found).Id);
        Assert.Same(MissionLibrary.Studio(false), MissionLibrary.Studio(false));
        Assert.NotSame(MissionLibrary.Studio(false), MissionLibrary.Studio(true));
    }

    [Fact]
    public void TheStripClosesInANarrowTabAndTheToggleOpensItAgain()
    {
        Assert.True(LibraryStrip.Shows(setting: true, narrow: false, openedInNarrowTab: false));
        Assert.False(LibraryStrip.Shows(setting: true, narrow: true, openedInNarrowTab: false));
        Assert.True(LibraryStrip.Shows(setting: true, narrow: true, openedInNarrowTab: true));
        Assert.False(LibraryStrip.Shows(setting: false, narrow: false, openedInNarrowTab: true));

        // In a narrow tab, the toggle opens the strip and turns the setting on. A close keeps the setting.
        Assert.Equal((true, true), LibraryStrip.Toggled(setting: false, narrow: true, shown: true));
        Assert.Equal((true, false), LibraryStrip.Toggled(setting: true, narrow: true, shown: false));
        // In a wide tab, the toggle sets the setting.
        Assert.Equal((false, false), LibraryStrip.Toggled(setting: true, narrow: false, shown: false));
    }
}
