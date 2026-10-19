using OpenLancer.Data.Editing;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// One kept selection for each center tab (section 4.2 of the M6a design, decision D550), with the Scene, Cutscene and
/// Mission tabs.
/// </summary>
public sealed class CenterSelectionsTests
{
    private const string Script = @"DATA\MISSIONS\TEST\test.ini";

    private static readonly object SceneItem = new();
    private static readonly object Element = new();
    private static readonly object CutsceneRoot = new();
    private static readonly MissionRef Mission = MissionRef.Of(Script);
    private static readonly MissionRef Trigger = new(Script, MissionItemKind.Trigger,
        new SectionRef(Script, new SectionIdentity("Trigger", "t1")));

    private static CenterSelections Tabs() => new(item => item switch
    {
        MissionRef => CenterMode.Mission,
        _ when item == Element || item == CutsceneRoot => CenterMode.Cutscene,
        _ => null,
    });

    /// <summary>The Cutscene tab shows its root without a kept element. The Mission tab shows the mission without a kept
    /// item, and a trigger that is gone becomes the mission.</summary>
    private static object? Valid(CenterMode mode, object? item) => mode switch
    {
        CenterMode.Cutscene => item ?? CutsceneRoot,
        CenterMode.Mission => item is MissionRef { Key: "gone" } ? Mission : item ?? Mission,
        _ => item,
    };

    [Fact]
    public void EachOfTheThreeTabsGetsBackItsOwnSelection()
    {
        var tabs = Tabs();

        // Scene, then Mission: the scene item waits, and the mission shows.
        Assert.Equal(Mission, tabs.Follow(CenterMode.Mission, SceneItem, Valid));
        // The user selects a trigger. Then the Cutscene tab: the trigger waits, and the cutscene shows.
        Assert.Equal(CutsceneRoot, tabs.Follow(CenterMode.Cutscene, Trigger, Valid));
        Assert.Equal(Trigger, tabs.Kept(CenterMode.Mission));
        // The user selects an element. Then the Scene: the element waits, and the scene item comes back.
        Assert.Equal(SceneItem, tabs.Follow(CenterMode.Scene, Element, Valid));
        Assert.Equal(Element, tabs.Kept(CenterMode.Cutscene));
        // Back to the Mission and the Cutscene tabs: each gets its own selection.
        Assert.Equal(Trigger, tabs.Follow(CenterMode.Mission, SceneItem, Valid));
        Assert.Equal(Element, tabs.Follow(CenterMode.Cutscene, Trigger, Valid));
        Assert.Equal(CenterMode.Cutscene, tabs.Shown);
    }

    [Fact]
    public void ADatabaseSelectionStaysWithTheTabThatShows()
    {
        var tabs = Tabs();
        tabs.Follow(CenterMode.Mission, SceneItem, Valid);
        var section = new SectionRef(@"DATA\SHIPS\shiparch.ini", new SectionIdentity("Ship", "a"));

        // A Database click while the Mission tab shows selects a section. The Scene tab then shows it, and the Mission
        // tab keeps no trigger.
        Assert.Equal(section, tabs.Follow(CenterMode.Scene, section, Valid));
        Assert.Null(tabs.Kept(CenterMode.Mission));
        Assert.Equal(Mission, tabs.Follow(CenterMode.Mission, section, Valid));
        Assert.Equal(section, tabs.Kept(CenterMode.Scene));
    }

    [Fact]
    public void AKeptSelectionThatIsNotValidBecomesTheRootItem()
    {
        var tabs = Tabs();
        tabs.Follow(CenterMode.Mission, SceneItem, Valid);
        var gone = Trigger with { Key = "gone" };

        tabs.Follow(CenterMode.Scene, gone, Valid);

        Assert.Equal(Mission, tabs.Follow(CenterMode.Mission, SceneItem, Valid));
    }

    [Fact]
    public void AnItemOfAThirdTabGoesIntoItsOwnSlot()
    {
        var tabs = Tabs();
        tabs.Follow(CenterMode.Mission, SceneItem, Valid);

        // A mission link selects a cutscene element while the Mission tab shows, then the Scene tab shows.
        Assert.Equal(SceneItem, tabs.Follow(CenterMode.Scene, Element, Valid));
        Assert.Equal(Element, tabs.Kept(CenterMode.Cutscene));
    }

    [Fact]
    public void AResetForgetsTheKeptSelections()
    {
        var tabs = Tabs();
        tabs.Park(CenterMode.Mission, Trigger);
        tabs.Follow(CenterMode.Cutscene, SceneItem, Valid);

        tabs.Reset();

        Assert.Equal(CenterMode.Scene, tabs.Shown);
        Assert.Null(tabs.Kept(CenterMode.Mission));
        Assert.Null(tabs.Kept(CenterMode.Scene));
        Assert.Equal(CenterMode.Mission, tabs.OwnerOf(Trigger));
    }

    [Fact]
    public void AMissionRefKeepsItsValueAcrossANewIndex()
    {
        var again = new MissionRef(@"data\missions\test\TEST.ini", MissionItemKind.Trigger,
            new SectionRef(@"data\missions\test\TEST.ini", new SectionIdentity("TRIGGER", "T1")));

        Assert.Equal(Trigger, again);
        Assert.Equal(Trigger.GetHashCode(), again.GetHashCode());
        Assert.NotEqual(Trigger, Trigger with { Row = 2 });
    }
}
