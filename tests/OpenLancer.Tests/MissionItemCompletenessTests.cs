using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The completeness of the mission items in the data layer (sections 11.4 and 13.5 of the M6a design). The items are
/// the mission ship, the conversation, the cutscene link and the base scene link. The step "Archetype, loadout and
/// pilot" of a ship compares the names of its build with the base game (decision D551). So a mod that breaks the build
/// of a retail ship gives a missing step, also when the mod does not change the script.
/// </summary>
public sealed class MissionItemCompletenessTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";
    private const string ShipFile = @"DATA\MISSIONS\npcships.ini";

    private const string Text = """
        [Mission]

        [NPC]
        nickname = npc_one
        npc_ship_arch = build_one

        [MsnShip]
        nickname = s_one
        npc = npc_one
        system = sys_a
        position = 1, 2, 3

        [Dialog]
        nickname = d_one
        system = sys_a
        line = s_one, Player, comm_a

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_CallThorn = scripts\a.thn, s_one
        Act_AddRTC = scenes\offer.ini
        Act_StartDialog = d_one
        Act_ActTrig = t_end

        [Trigger]
        nickname = t_end
        Cnd_True = no_params
        Act_RevertCam = no_params
        Act_ChangeState = SUCCEED
        """;

    private static string Build(string loadout) => $"""
        [NPCShipArch]
        nickname = build_one
        ship_archetype = ship_a
        loadout = {loadout}
        pilot = pilot_a
        """;

    /// <summary>The names of the game data: the ship, the pilot and the loadouts of a list.</summary>
    private static MissionWorld Names(params string[] loadouts) => World(new Dictionary<MissionValueKind, string[]>
    {
        [MissionValueKind.ShipArch] = ["ship_a"],
        [MissionValueKind.Pilot] = ["pilot_a"],
        [MissionValueKind.Loadout] = loadouts,
    });

    /// <summary>The step "Archetype, loadout and pilot" of the ship of a run.</summary>
    private static CompletenessStep Parts(MissionRunResult result)
    {
        var analysis = result.Of(Script)!;
        var ship = analysis.Document.OfKind(MissionSectionKind.Ship).Single();
        return MissionCompleteness.OfShip(result, analysis, ship).Steps.Single(s => s.Name == "Archetype, loadout and pilot");
    }

    [Fact]
    public void AShipWithAGoodBuildHasAllStepsDone()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Text);
        folder.Write(ShipFile, Build("load_a"));
        var result = MissionRun.Execute(folder.Inputs(world: Names("load_a")));

        var analysis = result.Of(Script)!;
        var report = MissionCompleteness.OfShip(result, analysis, analysis.Document.OfKind(MissionSectionKind.Ship).Single());

        Assert.Equal(["Pilot and voice", "Ship build", "Archetype, loadout and pilot"], report.Steps.Select(s => s.Name));
        Assert.True(report.Complete);
        Assert.Equal("build_one", MissionCompleteness.BuildOf(result.ShipBuilds, analysis.Index,
            analysis.Document.OfKind(MissionSectionKind.Ship).Single())!.Nickname);
    }

    /// <summary>A mod removes the loadout of a build of the base game, and the script does not change. The base game
    /// has the loadout, so the step is missing, with the reason and the build to go to.</summary>
    [Fact]
    public void AModThatRemovesALoadoutGivesAMissingStepInAScriptThatItDoesNotChange()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Text);
        folder.Write(ShipFile, Build("load_a"));
        var names = Names("load_b");
        var baseNames = names.WithRemoved(new Dictionary<MissionValueKind, IReadOnlyDictionary<string, string>>
        {
            [MissionValueKind.Loadout] = new Dictionary<string, string> { ["load_a"] = "mod_a" },
        });

        var step = Parts(MissionRun.Execute(folder.Inputs(world: names, baseWorld: baseNames)));

        Assert.Equal(CompletenessState.Missing, step.State);
        Assert.True(step.IsError);
        Assert.Contains("load_a", step.Detail);
        // Go to selects the ship build in the mission ship file.
        Assert.EndsWith("npcships.ini", ((OpenLancer.Data.Editing.SectionRef)step.GoTo!).Path,
            System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The base game misses the loadout too, so the step is hidden and counts as done.</summary>
    [Fact]
    public void ALoadoutThatTheBaseGameMissesTooGivesAHiddenStep()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Text);
        folder.Write(ShipFile, Build("load_a"));

        var step = Parts(MissionRun.Execute(folder.Inputs(world: Names("load_b"))));

        Assert.Equal(CompletenessState.Hidden, step.State);
        Assert.False(step.IsError);
    }

    /// <summary>A mod changes the script. The base game misses the same loadout for the same ship, so the step stays
    /// hidden. A changed script does not show a gap of the base game as a mod problem.</summary>
    [Fact]
    public void AChangedScriptKeepsAGapOfTheBaseGameHidden()
    {
        using var folder = new MissionFolder();
        using var baseFolder = new MissionFolder();
        folder.Write(Script, Text.Replace("position = 1, 2, 3", "position = 4, 5, 6"));
        folder.Write(ShipFile, Build("load_a"));
        baseFolder.Write(Script, Text);
        baseFolder.Write(ShipFile, Build("load_a"));
        var ledger = new TestLedger();
        ledger.Change(Script, "mod_a");

        var step = Parts(MissionRun.Execute(folder.Inputs(baseFolder, ledger, "mod_a", world: Names("load_b"))));

        Assert.Equal(CompletenessState.Hidden, step.State);
    }

    /// <summary>A mod changes the ship file so that the build names a loadout that the game data does not have. The
    /// build of the base game is good, so the step is missing.</summary>
    [Fact]
    public void AModThatChangesTheShipFileGivesAMissingStep()
    {
        using var folder = new MissionFolder();
        using var baseFolder = new MissionFolder();
        folder.Write(Script, Text);
        folder.Write(ShipFile, Build("load_x"));
        baseFolder.Write(Script, Text);
        baseFolder.Write(ShipFile, Build("load_a"));
        var ledger = new TestLedger();
        ledger.Change(ShipFile, "mod_a");

        var result = MissionRun.Execute(folder.Inputs(baseFolder, ledger, "mod_a", world: Names("load_a")));

        Assert.NotNull(result.BaseShipBuilds);
        Assert.Equal(CompletenessState.Missing, Parts(result).State);
    }

    [Fact]
    public void TheConversationAndTheLinksHaveTheStepsOfTheRuleData()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Text);
        var analysis = MissionRun.Execute(folder.Inputs()).Of(Script)!;
        var start = analysis.Index.Trigger("t_start")!;

        var conversation = MissionCompleteness.OfConversation(analysis,
            analysis.Document.OfKind(MissionSectionKind.Conversation).Single());
        var cutscene = MissionCompleteness.OfCutsceneLink(analysis, start.ActionsOf("Act_CallThorn").Single());
        var scene = MissionCompleteness.OfBaseSceneLink(analysis, start.ActionsOf("Act_AddRTC").Single());

        Assert.Equal(MissionSchema.Retail.Completeness["conversation"].Steps.Select(s => s.Name),
            conversation.Steps.Select(s => s.Name));
        Assert.Equal(["Cutscene file", "Anchor", "Camera back"], cutscene.Steps.Select(s => s.Name));
        Assert.Equal(["Scene file", "Path length"], scene.Steps.Select(s => s.Name));
        Assert.Equal(("Conversation", "Cutscene link", "Base scene link"), (conversation.Title, cutscene.Title, scene.Title));
    }
}
