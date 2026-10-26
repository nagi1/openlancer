using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The mission play request (section 7.2 of the M6c design, build step 2.4): the play start of each script, the start
/// of a request, the agent play rule of the user decision of 2026-10-20, and the start that Play prepares after its save.
/// The editor is a fake, and the script is the synthetic script of the start plan tests.
/// </summary>
public sealed class MissionPlayTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly TestWorkspace ws = new();
    private readonly FakeEditor editor;
    private readonly MissionPlay plays;

    public MissionPlayTests()
    {
        editor = new FakeEditor(Path.Combine(ws.Root, "profiles", "p.json"));
        plays = new MissionPlay(editor);
    }

    private static string ScriptPath => MissionTestData.Path;

    public void Dispose() => ws.Dispose();

    private IReadOnlyList<string> Console => editor.Log.Entries.Select(e => e.Message).ToList();

    [Fact]
    public void AScriptPlaysFromTheMissionStartUntilAPlayStartIsSet()
    {
        Assert.Equal("Mission start", plays.StartNameOf(ScriptPath));

        plays.SetPlayStart(ScriptPath, plays.PlayStartOf(ScriptPath) with { Trigger = "t_kill" });

        Assert.Equal("t_kill (approximate)", plays.StartNameOf(ScriptPath));
        plays.SetPlayStart(ScriptPath, plays.PlayStartOf(ScriptPath) with { Trigger = "t_restore" });
        Assert.Equal("Checkpoint t_restore", plays.StartNameOf(ScriptPath));
    }

    /// <summary>F5 plays the play start. A request of the user does what F5 does: no question, and the play start does
    /// not change.</summary>
    [Fact]
    public void F5RequestsAPlayOfThePlayStart()
    {
        editor.UnsavedMod = "act";
        plays.SetPlayStart(ScriptPath, new PlayStartSetting("t_kill", "base:ol_base_a"));

        var result = plays.PlayFromPlayStart(ScriptPath);

        Assert.Null(editor.Asked);
        var start = Assert.IsType<MissionPlayStart>(Assert.Single(editor.Requests));
        Assert.Equal(MissionStartPoint.At("t_kill"), start.Request.Start);
        Assert.Equal((StartPlaceChoice.Base, "ol_base_a"), (start.Request.Place, start.Request.PlaceBase));
        Assert.Equal(EditOrigin.User, start.Request.Origin);
        Assert.True(result.Wait(Wait));
        Assert.True(result.Result.Started);
    }

    /// <summary>"Play from here" sets the play start and plays. The place choice of the play start stays.</summary>
    [Fact]
    public void PlayFromHereSetsThePlayStartAndPlays()
    {
        plays.SetPlayStart(ScriptPath, new PlayStartSetting("", "camera"));

        plays.PlayFromHere(ScriptPath, "t_kill");

        Assert.Equal(new PlayStartSetting("t_kill", "camera"), plays.PlayStartOf(ScriptPath));
        var start = Assert.IsType<MissionPlayStart>(Assert.Single(editor.Requests));
        Assert.Equal((MissionStartPoint.At("t_kill"), StartPlaceChoice.Camera), (start.Request.Start, start.Request.Place));
    }

    /// <summary>The agent play rule: with unsaved edits, the editor asks. Cancel gives refused with the reason, and no
    /// play starts. "Save and continue" asks Play for the play, and Play saves first.</summary>
    [Fact]
    public void AnAgentRequestWithUnsavedEditsAsksTheUserFirst()
    {
        editor.UnsavedMod = "act";

        var cancelled = plays.Request(AgentRequest("t_kill"));
        editor.Asked!.Value.Choices.Single(c => c.Label == "Cancel").Action();

        Assert.True(cancelled.Wait(Wait));
        Assert.Equal("The user did not save. Play did not start.", cancelled.Result.Error!.Message);
        Assert.Empty(editor.Requests);

        var saved = plays.Request(AgentRequest("t_kill"));
        editor.Asked!.Value.Choices.Single(c => c.Label == "Save and continue").Action();

        Assert.True(saved.Wait(Wait));
        Assert.True(saved.Result.Started);
        Assert.Single(editor.Requests);
    }

    /// <summary>The setting skips the question, and a request without unsaved edits starts at once.</summary>
    [Theory]
    [InlineData("act", true)]
    [InlineData(null, false)]
    public void AnAgentRequestStartsAtOnceWithTheSettingOrWithoutUnsavedEdits(string? unsaved, bool autoSave)
    {
        editor.UnsavedMod = unsaved;
        editor.AutoSaveAgentPlay = autoSave;

        var result = plays.Request(AgentRequest("t_kill"));

        Assert.Null(editor.Asked);
        Assert.Single(editor.Requests);
        Assert.True(result.Wait(Wait));
        Assert.True(result.Result.Started);
    }

    /// <summary>A request of an agent gives refused while another play runs: a play from the camera, or the game of a
    /// play copy. While a mission play runs, the game starts again at the new start, so the request goes on.</summary>
    [Fact]
    public void AnAgentRequestWhileAnotherPlayRunsIsRefused()
    {
        editor.GameRuns = true;
        Assert.Equal(MissionPlay.OtherPlayDetail, plays.Request(AgentRequest(null)).Result.Error!.Detail);

        editor.GameRuns = false;
        editor.PlayStatus = PlayStatus.Running;
        Assert.Equal(MissionPlay.OtherPlayDetail, plays.Request(AgentRequest(null)).Result.Error!.Detail);

        editor.RunningMission = new MissionPlayStart(plays, new MissionPlayRequest(ScriptPath, MissionStartPoint.MissionStart));
        Assert.Null(plays.Request(AgentRequest(null)).Result.Error);
        Assert.Single(editor.Requests);
    }

    /// <summary>A plan that cannot start refuses the request of an agent with the reason of the plan, before the
    /// question. A trigger that the script does not have gives not_found.</summary>
    [Fact]
    public void AnAgentRequestWithAPlanThatCannotStartIsRefusedBeforeTheQuestion()
    {
        editor.UnsavedMod = "act";
        editor.Slot = null;

        var noSlot = plays.Request(AgentRequest("t_kill")).Result;

        Assert.Equal(EditErrorKind.Refused, noSlot.Error!.Kind);
        Assert.Equal(StartPlan.NoSlotReason, noSlot.Error.Message);
        Assert.Equal(StartRefusal.NoSlot, noSlot.Plan!.Refusal);
        editor.Slot = StartPlanTests.Slot;
        Assert.Equal(EditErrorKind.NotFound, plays.Request(AgentRequest("t_gone")).Result.Error!.Kind);
        Assert.Null(editor.Asked);
        Assert.Empty(editor.Requests);
    }

    /// <summary>The start makes its plan from the model when Play prepares it, after the save. So the plan reads the
    /// script that the game reads, not the model of the request.</summary>
    [Fact]
    public void ThePlanUsesTheModelOfTheSavedScript()
    {
        plays.PlayFromHere(ScriptPath, "t_kill");
        var start = (MissionPlayStart)editor.Requests[0];
        // The save writes an edit after the request: t_launch also starts a timer beside t_kill.
        editor.Index = MissionTestData.Index(StartPlanTests.Script.Replace("Act_ActTrig = t_fail",
            "Act_ActTrig = t_fail\nAct_ActTrig = t_extra") + "\n[Trigger]\nnickname = t_extra\nCnd_Timer = 30\n");

        var fromSave = start.Prepare();

        Assert.NotNull(fromSave);
        Assert.Contains("t_extra", start.Plan!.ActiveSet);
        Assert.Equal(PlayFolder.StartSavePath(editor.ProfilePath!), fromSave.Save);
        Assert.Equal(PlayFolder.TestLayerPath(editor.ProfilePath!), fromSave.TestLayer);
        Assert.True(File.Exists(fromSave.Save));
        Assert.True(Directory.Exists(fromSave.TestLayer));
        Assert.Equal("t_kill (approximate)", start.StartName);
        Assert.Equal("M99", start.Slot);
        Assert.True(start.Approximate);
        Assert.Equal(start.Plan.Setup.Select(t => t.Nickname), start.SetupTriggers);
    }

    /// <summary>A play start whose trigger is gone becomes the mission start, and the Console tells it (decision D762).
    /// A request of an agent plays the mission start too, but it does not change the play start of the user.</summary>
    [Theory]
    [InlineData(EditOrigin.User, "")]
    [InlineData(EditOrigin.Agent, "t_gone")]
    public void AMissingStartFallsBackToTheMissionStart(EditOrigin origin, string playStartAfter)
    {
        plays.SetPlayStart(ScriptPath, new PlayStartSetting("t_gone", "suggested"));
        var start = new MissionPlayStart(plays, new MissionPlayRequest(ScriptPath, MissionStartPoint.At("t_gone"))
        {
            Origin = origin,
        });

        Assert.NotNull(start.Prepare());

        Assert.Equal(StartKind.MissionStart, start.Plan!.Kind);
        Assert.Contains("The play start of M99 named trigger 't_gone'. The script does not have this trigger now. The play " +
                        "start is the mission start again.", Console);
        Assert.Equal(playStartAfter, plays.PlayStartOf(ScriptPath).Trigger);
    }

    /// <summary>A start that cannot start gives its reason to the Console with the prefix of section 8.4, and keeps it
    /// for the result of the request.</summary>
    [Fact]
    public void AStartThatCannotStartTellsWhy()
    {
        editor.Slot = null;
        var start = new MissionPlayStart(plays, new MissionPlayRequest(ScriptPath, MissionStartPoint.MissionStart));

        Assert.Null(start.Prepare());

        Assert.Equal(StartPlan.NoSlotReason, start.Refusal);
        Assert.Contains(StartPlan.RefusalPrefix + StartPlan.NoSlotReason, Console);
        Assert.Null(start.Plan);
    }

    /// <summary>The play start follows a rename of its trigger, also the Undo and the Redo of the rename (section 5.2 of
    /// the M6c design). The renames come from the event of each change of the history.</summary>
    [Fact]
    public void ThePlayStartFollowsARenameItsUndoAndItsRedo()
    {
        using var m = Create();
        var settings = EditorSettings.Transient();
        var profile = m.Session.Profile.FilePath;
        settings.SetPlayStart(profile, Script, new PlayStartSetting("ol_end", "suggested"));
        m.Missions.ScriptsChanged += change => MissionPlay.Rename(settings, profile, change);

        m.Apply(new RenameTrigger(T("ol_end"), "ol_finish"));
        Assert.Equal("ol_finish", settings.PlayStartOf(profile, Script).Trigger);

        m.Session.Undo();
        Assert.Equal("ol_end", settings.PlayStartOf(profile, Script).Trigger);

        m.Session.Redo();
        Assert.Equal("ol_finish", settings.PlayStartOf(profile, Script).Trigger);

        // A rename of another trigger does not move the play start.
        m.Apply(new RenameTrigger(T("ol_wave"), "ol_wave_2"));
        Assert.Equal("ol_finish", settings.PlayStartOf(profile, Script).Trigger);
    }

    /// <summary>The settings keep the play start of each profile and script. The mission start with the suggested place
    /// keeps no entry, and a read of the settings file gives the same play starts.</summary>
    [Fact]
    public void TheSettingsKeepThePlayStarts()
    {
        var settings = EditorSettings.Transient();
        const string profile = @"C:\p\sample.json";
        settings.SetPlayStart(profile, ScriptPath, new PlayStartSetting("t_kill", "base:ol_base_a"));

        var read = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        var start = read.PlayStartOf(profile, ScriptPath.ToLowerInvariant());
        Assert.Equal(MissionStartPoint.At("t_kill"), start.Start);
        Assert.Equal((StartPlaceChoice.Base, "ol_base_a"), start.PlaceChoice);
        read.SetPlayStart(profile, ScriptPath, new PlayStartSetting("", "suggested"));
        Assert.Empty(read.PlayStarts[profile]);
        Assert.Equal((StartPlaceChoice.Suggested, (string?)null), new PlayStartSetting("", "unknown").PlaceChoice);
        Assert.Equal(new PlayStartSetting("", "camera"),
            PlayStartSetting.Of(MissionStartPoint.MissionStart, StartPlaceChoice.Camera, null));
    }

    /// <summary>The play start of a mission is an item of the Mission tab: the toolbar chip selects it, and the
    /// Inspector shows the Play start page. A run of the checks keeps the selection while the run has the
    /// script.</summary>
    [Fact]
    public void ThePlayStartIsAnItemThatARunKeeps()
    {
        var item = MissionRef.PlayStartOf("DATA/MISSIONS/TEST/test.ini");
        using var folder = new MissionFolder();
        folder.Write(MissionTestData.Path, StartPlanTests.Script);
        var result = MissionRun.Execute(folder.Inputs());

        Assert.Equal((MissionTestData.Path, MissionItemKind.PlayStart), (item.Script, item.Kind));
        Assert.Null(item.Section);
        Assert.Equal(item, MissionWorkspace.Valid(result, item));
        Assert.NotEqual(MissionRef.Of(MissionTestData.Path), item);
    }

    private static MissionPlayRequest AgentRequest(string? trigger) =>
        new(ScriptPath, trigger == null ? MissionStartPoint.MissionStart : MissionStartPoint.At(trigger))
        {
            Origin = EditOrigin.Agent,
        };

    /// <summary>The editor side of the mission play. It gives the synthetic script of the start plan tests and keeps
    /// each request and the last question.</summary>
    private sealed class FakeEditor(string profile) : IMissionPlayEditor
    {
        public string? ProfilePath { get; } = profile;

        public EditorSettings Settings { get; } = EditorSettings.Transient();

        public ConsoleLog Log { get; } = new();

        public MissionIndex Index { get; set; } = MissionTestData.Index(StartPlanTests.Script);

        public StorySlot? Slot { get; set; } = StartPlanTests.Slot;

        public string? PlayPackage => null;

        public PlayStatus PlayStatus { get; set; } = PlayStatus.Idle;

        public IMissionStart? RunningMission { get; set; }

        public bool GameRuns { get; set; }

        public string? UnsavedMod { get; set; }

        public bool AutoSaveAgentPlay { get; set; }

        public List<IMissionStart> Requests { get; } = [];

        public (string Title, string Message, IReadOnlyList<Choice> Choices, Action Cancel)? Asked { get; private set; }

        public MissionIndex? IndexOf(string script) => Index;

        public (MissionIndex Index, StorySlot? Slot)? ModelOf(string script, out string? problem)
        {
            problem = null;
            return (Index, Slot);
        }

        public StartSavePlace? PlaceOf(StartPlace place, out string? problem)
        {
            problem = null;
            return place.Kind == StartPlaceKind.Base
                ? StartSavePlace.AtBase("ol_sys_a", place.Base!)
                : new StartSavePlace(place.System ?? "ol_sys_a", null, place.Position, place.Orientation);
        }

        public Task<PlayRequestResult> RequestMission(IMissionStart start, string? package)
        {
            Requests.Add(start);
            return Task.FromResult(new PlayRequestResult(PlayStatus.Starting));
        }

        public void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel) =>
            Asked = (title, message, choices, cancel);
    }
}
