using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer.World;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The parts of the stage of a space script that need no GPU (test 5.2 item 6 of the cutscene fixes plan): which objects
/// of the stage world show, and the cache that keeps one stage.
/// </summary>
public sealed class SpaceStageTests
{
    private static GameObject Object(string name) => new() { Name = new ObjectName(name), Nickname = name };

    private static bool Hidden(GameObject obj) => (obj.Flags & GameObjectFlags.Hidden) == GameObjectFlags.Hidden;

    /// <summary>The objects of the system always show. The objects of the shown player show, and the objects of the
    /// other player hide. "Script objects" off hides all script objects.</summary>
    [Fact]
    public void OnlyTheSystemAndTheShownPlayerShow()
    {
        var (planet, station) = (Object("planet"), Object("station"));
        var (shipA, markerA, shipB) = (Object("shipA"), Object("markerA"), Object("shipB"));
        station.Flags |= GameObjectFlags.Hidden;
        var world = new List<GameObject> { planet, station, shipA, markerA, shipB };
        var system = new HashSet<GameObject> { planet, station };
        var playerA = new HashSet<GameObject> { shipA, markerA };

        SpaceStage.ApplyVisibility(world, system, playerA, true);
        Assert.Equal([false, true, false, false, true], world.ConvertAll(Hidden));

        SpaceStage.ApplyVisibility(world, system, new HashSet<GameObject> { shipB }, true);
        Assert.Equal([false, true, true, true, false], world.ConvertAll(Hidden));

        SpaceStage.ApplyVisibility(world, system, playerA, false);
        Assert.Equal([false, true, true, true, true], world.ConvertAll(Hidden));

        SpaceStage.ApplyVisibility(world, system, null, true);
        Assert.Equal([false, true, true, true, true], world.ConvertAll(Hidden));
    }

    /// <summary>An object of the shown player that left the world causes no error.</summary>
    [Fact]
    public void AnObjectThatLeftTheWorldCausesNoError()
    {
        var (planet, ship, gone) = (Object("planet"), Object("ship"), Object("gone"));

        SpaceStage.ApplyVisibility([planet, ship], new HashSet<GameObject> { planet },
            new HashSet<GameObject> { ship, gone }, true);

        Assert.False(Hidden(ship));
        Assert.False(Hidden(gone));
    }

    private sealed class FakeStage(string name, List<string> log) : IDisposable
    {
        public string Name { get; } = name;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Assert.False(Disposed, $"The cache disposed {Name} two times.");
            Disposed = true;
            log.Add($"dispose {Name}");
        }
    }

    [Fact]
    public void TheCacheKeepsOneStageForASystemOfTheData()
    {
        var log = new List<string>();
        var cache = new StageCache<FakeStage>();
        var (data, newData) = (new object(), new object());
        FakeStage Load(string name)
        {
            log.Add($"load {name}");
            return new FakeStage(name, log);
        }

        var first = cache.Get("Zz01", data, () => Load("first"));
        Assert.Same(first, cache.Get("ZZ01", data, () => Load("again")));
        Assert.True(cache.Holds("zz01", data));
        Assert.False(cache.Holds("Zz02", data));

        var second = cache.Get("Zz02", data, () => Load("second"));
        var third = cache.Get("Zz02", newData, () => Load("third"));
        Assert.Equal(["load first", "dispose first", "load second", "dispose second", "load third"], log);
        Assert.True(first.Disposed && second.Disposed);
        Assert.Same(third, cache.Stage);

        cache.Reset();
        Assert.True(third.Disposed);
        Assert.Null(cache.Stage);
        Assert.Null(cache.Key);
    }

    /// <summary>A factory that throws leaves no stage, and the old stage is gone.</summary>
    [Fact]
    public void AFactoryThatThrowsLeavesNoStage()
    {
        var log = new List<string>();
        var cache = new StageCache<FakeStage>();
        var data = new object();
        var old = cache.Get("Zz01", data, () => new FakeStage("old", log));

        Assert.Throws<InvalidOperationException>(() =>
            cache.Get("Zz02", data, () => throw new InvalidOperationException("No system")));

        Assert.True(old.Disposed);
        Assert.Null(cache.Stage);
        Assert.Null(cache.Key);
        Assert.False(cache.Holds("Zz02", data));
    }

    /// <summary>The tooltip of a Space row names the trigger and the rule that found the system.</summary>
    [Fact]
    public void TheRowTooltipNamesTheTriggerAndTheRule()
    {
        var line = new ThnReference(@"DATA\MISSIONS\MX01\mx01.ini", "Trigger", "Act_CallThorn", "a.thn", 0);
        var key = new SpaceUse(line, "t1", "Zz01", SpaceSystemRule.TriggerKey, null, "Player", null, null, null, null);

        Assert.Equal("The trigger t1 plays this script. It runs only in Zz01.", CutsceneView.SpaceRowTooltip(key));
        Assert.Equal("The trigger t1 plays this script. Its condition names the base Zz02_01_Base, which is in Zz02.",
            CutsceneView.SpaceRowTooltip(key with
            {
                System = "Zz02", SystemRule = SpaceSystemRule.Condition, SystemSource = "the base Zz02_01_Base",
            }));
        Assert.Equal("The trigger t1 plays this script. Its condition names the system Zz02.",
            CutsceneView.SpaceRowTooltip(key with
            {
                System = "Zz02", SystemRule = SpaceSystemRule.Condition, SystemSource = "the system Zz02",
            }));
        Assert.Equal("The trigger t1 plays this script. The trigger t0 starts it and runs only in Zz01.",
            CutsceneView.SpaceRowTooltip(key with { SystemRule = SpaceSystemRule.ActivatingTrigger, SystemSource = "t0" }));
        Assert.Equal("No rule finds the system of the mission line of the trigger t1.",
            CutsceneView.SpaceRowTooltip(key with { System = null, SystemRule = null }));
    }

    /// <summary>The tooltip of a row adds the problem of the line, also when a rule found the system. A trigger without
    /// a nickname and a line outside a trigger name the file.</summary>
    [Fact]
    public void TheRowTooltipAddsTheProblemAndNamesTheFileOfATriggerWithoutANickname()
    {
        const string file = @"DATA\MISSIONS\MX01\mx01.ini";
        var line = new ThnReference(file, "Trigger", "Act_CallThorn", "a.thn", 0);
        var condition = new SpaceUse(line, "t1", "Zz02", SpaceSystemRule.Condition, "the system Zz02", "Player", null,
            null, null, null, "The system key of the trigger names Qq99, but the game data has no such system.");

        Assert.Equal("The trigger t1 plays this script. Its condition names the system Zz02. The system key of the " +
                     "trigger names Qq99, but the game data has no such system.", CutsceneView.SpaceRowTooltip(condition));
        Assert.Equal($"A trigger without a nickname in {file} plays this script. It runs only in Zz01.",
            CutsceneView.SpaceRowTooltip(condition with
            {
                Trigger = "", System = "Zz01", SystemRule = SpaceSystemRule.TriggerKey, SystemSource = null,
                Problem = null,
            }));
        Assert.Equal($"No rule finds the system of the mission line in {file}. The line is not in a [Trigger] section.",
            CutsceneView.SpaceRowTooltip(condition with
            {
                Trigger = "", System = null, SystemRule = null, SystemSource = null,
                Problem = "The line is not in a [Trigger] section.",
            }));
    }

    /// <summary>A system without a name shows its nickname once in the context menu.</summary>
    [Fact]
    public void TheRowOfASystemWithoutANameShowsTheNicknameOnce()
    {
        Assert.Equal("Zz Two (Zz02)", CutsceneView.SpaceRow("Zz02", "Zz Two"));
        Assert.Equal("Zz02", CutsceneView.SpaceRow("Zz02", "Zz02"));
    }

    /// <summary>
    /// The banners come in the order of section 3.3. The banner of the position of the mission line shows also without a
    /// system. The banner of a system that did not load replaces the note of the plan, which says that the preview
    /// shows the system.
    /// </summary>
    [Fact]
    public void TheBannersFollowTheOrderAndAFailedLoadReplacesTheNote()
    {
        var none = new Dictionary<string, string>();
        var plan = new PreviewPlan(null, [@"DATA\x.thn"], @"DATA\x.thn", null, null, null, none)
        {
            Note = "note", System = "Zz01", ScriptPosition = new Vector3(1, 2, 3),
        };

        Assert.Equal(["note", "main", CutsceneWorkspace.ScriptPositionLine, "unsaved"],
            CutsceneWorkspace.BannersOf(plan, "main", "unsaved", null));
        Assert.Equal(["main", CutsceneWorkspace.ScriptPositionLine, "failed"],
            CutsceneWorkspace.BannersOf(plan, "main", null, "failed"));
        Assert.Equal(["no system", CutsceneWorkspace.ScriptPositionLine],
            CutsceneWorkspace.BannersOf(plan with { Note = "no system", System = null }, null, null, null));
        Assert.Empty(CutsceneWorkspace.BannersOf(plan with { Note = null, ScriptPosition = null }, null, null, null));
    }

    /// <summary>A reload of the game data frees the stage of the old data, also when no cutscene is open. The stage of
    /// the current data stays.</summary>
    [Fact]
    public void TheCacheForgetsTheStageOfOtherData()
    {
        var log = new List<string>();
        var cache = new StageCache<FakeStage>();
        var (data, newData) = (new object(), new object());
        var stage = cache.Get("Zz01", data, () => new FakeStage("stage", log));

        cache.ForgetOtherData(data);
        Assert.Same(stage, cache.Stage);
        Assert.False(stage.Disposed);

        cache.ForgetOtherData(newData);
        Assert.True(stage.Disposed);
        Assert.Null(cache.Stage);
        Assert.Null(cache.Key);
        cache.ForgetOtherData(newData);
    }

    /// <summary>The Console lines and the banners of the stage name the system (section 3.3 of the cutscene fixes
    /// plan). The time does not depend on the culture.</summary>
    [Fact]
    public void TheLinesOfTheStageNameTheSystem()
    {
        Assert.Equal("The cutscene preview loaded the system Zz01 in 0.85 s.",
            CutsceneWorkspace.StageLoadedLine("Zz01", TimeSpan.FromMilliseconds(850)));
        Assert.Equal("The preview cannot load the system Zz01: No model. The preview shows the script without the system.",
            CutsceneWorkspace.StageFailedLine("Zz01", new InvalidOperationException("No model.")));
        Assert.Equal("The system Zz Two has unsaved edits. The Cutscene tab shows the saved system. Save to see the " +
                     "edits here.", CutsceneWorkspace.UnsavedSystemLine("Zz Two"));
        Assert.Equal(@"No mission line names the system of DATA\x.thn. The preview shows the script without a system.",
            CutsceneWorkspace.NoSystemLine(@"DATA\x.thn"));
    }
}
