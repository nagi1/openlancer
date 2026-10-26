using System;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Missions;

/// <summary>What the mission play request reads of the editor. <see cref="EditorWindow"/> implements it, and the tests
/// give a fake.</summary>
/// <remarks>Each member runs on the UI thread.</remarks>
internal interface IMissionPlayEditor : IAgentPlayHost
{
    /// <summary>The full path of the profile file, or null without an open profile.</summary>
    string? ProfilePath { get; }

    /// <summary>The settings of the editor. They keep the play start of each script.</summary>
    EditorSettings Settings { get; }

    ConsoleLog Log { get; }

    /// <summary>The index of a script in the last run of the mission checks, or null.</summary>
    MissionIndex? IndexOf(string script);

    /// <summary>The index and the story slot of the model of a script now, or null with the reason (section 7 of the M6b
    /// design).</summary>
    (MissionIndex Index, StorySlot? Slot)? ModelOf(string script, out string? problem);

    /// <summary>The place of the start save for the start place of a plan, or null with the reason.</summary>
    StartSavePlace? PlaceOf(StartPlace place, out string? problem);

    /// <summary>The ship package of the Play menu, or null for the default ship.</summary>
    string? PlayPackage { get; }

    /// <summary>The Play status of OpenLancer.</summary>
    PlayStatus PlayStatus { get; }

    /// <summary>The mission start of the running OpenLancer game, or null.</summary>
    IMissionStart? RunningMission { get; }

    /// <summary>True while the game of a play copy runs (Play in the game).</summary>
    bool GameRuns { get; }

    /// <summary>Asks Play for a mission play (<see cref="PlayController.RequestMission"/>).</summary>
    Task<PlayRequestResult> RequestMission(IMissionStart start, string? package);
}

/// <summary>
/// The mission play request of the shared command layer (section 7.2 of the M6c design, decision D758): the one entry of
/// F5 and Play from here in the Mission tab, the items of the Play menu and the agent tool <c>mission_play</c>. It reads
/// and stores the play start of each script (section 5.2, decision D744). An agent request plays the start of the
/// request and never changes the play start of the user. It never saves on its own: the agent play rule asks the user
/// first (user decision of 2026-10-20).
/// </summary>
internal sealed class MissionPlay(IMissionPlayEditor editor) : IMissionPlayHost
{
    /// <summary>The detail code of an agent request while another play runs.</summary>
    public const string OtherPlayDetail = "OtherPlay";

    /// <summary>The refusal of an agent request while another play runs (section 7.2 of the M6c design).</summary>
    public const string OtherPlayReason = "Another play runs, so the mission play did not start. Stop the other play " +
                                          "first.";

    /// <summary>The play start of a script of the open profile, or the mission start without a profile.</summary>
    public PlayStartSetting PlayStartOf(string script) => editor.ProfilePath is { } profile
        ? editor.Settings.PlayStartOf(profile, script)
        : new PlayStartSetting("", "suggested");

    /// <summary>Sets the play start of a script ("Set as play start" and the Play start page). It has no Undo entry,
    /// because it is not data of the mod.</summary>
    public void SetPlayStart(string script, PlayStartSetting start)
    {
        if (editor.ProfilePath is not { } profile)
        {
            return;
        }

        editor.Settings.SetPlayStart(profile, script, start);
        editor.Settings.Save();
    }

    void IMissionPlayHost.SetPlayStart(string script, MissionStartPoint start) =>
        SetPlayStart(script, PlayStartOf(script) with { Trigger = start.Trigger ?? "" });

    /// <summary>Moves the play starts of the open profile with the trigger renames of a change of the history, also an
    /// Undo or a Redo of a rename (section 5.2 of the M6c design).</summary>
    public void Renamed(MissionScriptsChange change)
    {
        if (editor.ProfilePath is { } profile && Rename(editor.Settings, profile, change))
        {
            editor.Settings.Save();
        }
    }

    /// <summary>Moves the play starts of a profile with the trigger renames of a change. Returns true when a play start
    /// changed.</summary>
    internal static bool Rename(EditorSettings settings, string profile, MissionScriptsChange change)
    {
        var changed = false;
        foreach (var (from, to) in change.Renames)
        {
            if (string.Equals(from.Script, to.Script, StringComparison.OrdinalIgnoreCase))
            {
                changed |= settings.RenamePlayStart(profile, from.Script, from.Name, to.Name);
            }
        }

        return changed;
    }

    /// <summary>The name of the play start of a script in the UI texts (section 8.1 of the M6c design): "Mission start",
    /// "Checkpoint {trigger}" or "{trigger} (approximate)".</summary>
    public string StartNameOf(string script) => NameOf(editor.IndexOf(script), PlayStartOf(script).Start);

    /// <summary>The name of a start of a script with an index, or of a trigger that the index does not have.</summary>
    internal static string NameOf(MissionIndex? index, MissionStartPoint start)
    {
        if (start.Trigger is not { } name)
        {
            return StartPlan.NameOf(StartKind.MissionStart, start);
        }

        var kind = index?.Trigger(name) is not { } trigger ? StartKind.Approximate
            : index.StartTriggers.Contains(trigger) ? StartKind.MissionStart
            : DryRun.Checkpoints(index).Contains(trigger) ? StartKind.Checkpoint
            : StartKind.Approximate;
        return StartPlan.NameOf(kind, start);
    }

    /// <summary>F5 in the Mission tab: plays a script from its play start in OpenLancer (decision D744).</summary>
    public Task<PlayRequestResult> PlayFromPlayStart(string script)
    {
        var start = PlayStartOf(script);
        var (place, dock) = start.PlaceChoice;
        return Request(new MissionPlayRequest(script, start.Start) { Place = place, PlaceBase = dock });
    }

    /// <summary>"Play from here": sets the play start to a trigger and plays it. The place choice of the play start
    /// stays.</summary>
    public Task<PlayRequestResult> PlayFromHere(string script, string trigger)
    {
        SetPlayStart(script, PlayStartOf(script) with { Trigger = trigger });
        return PlayFromPlayStart(script);
    }

    /// <summary>
    /// The mission play request (section 7.2 of the M6c design). Play saves the active mod, then the start makes its
    /// plan from the model of the saved script and writes the start save and the test layer. While a mission play runs,
    /// the game starts again at the new start. A request of the user does what F5 does. A request of an agent also gives
    /// <c>refused</c> while another play runs and when its plan cannot start, and it follows the agent play rule (see
    /// <see cref="PlayRequests.WithAgentRule"/>).
    /// </summary>
    /// <returns>The result, when the play started or did not start.</returns>
    public Task<PlayRequestResult> Request(MissionPlayRequest request)
    {
        if (editor.ProfilePath == null)
        {
            const string noProfile = "Play needs an open profile whose game data loaded.";
            editor.Log.Add(ConsoleSeverity.Warning, PlayController.Source, noProfile);
            return Task.FromResult(PlayRequestResult.Refused(editor.PlayStatus, "NoProfile", noProfile));
        }

        if (request.Origin == EditOrigin.Agent && AgentRefusal(request) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return PlayRequests.WithAgentRule(editor, request.Origin, () => editor.PlayStatus,
            () => editor.RequestMission(new MissionPlayStart(this, request), editor.PlayPackage));
    }

    /// <summary>
    /// Why a request of an agent cannot start before it asks the user, or null: another play runs (section 7.2 of the
    /// M6c design), the script or the trigger does not resolve, or the plan of the model now refuses (section 7.4). The
    /// plan of the model now is the plan of the saved script, because the save writes the model.
    /// </summary>
    private PlayRequestResult? AgentRefusal(MissionPlayRequest request)
    {
        var status = editor.PlayStatus;
        if (editor.GameRuns || status != PlayStatus.Idle && editor.RunningMission == null)
        {
            return PlayRequestResult.Refused(status, OtherPlayDetail, OtherPlayReason);
        }

        if (editor.ModelOf(request.Script, out var problem) is not var (index, slot))
        {
            return PlayRequestResult.Refused(status, MissionErrors.ScriptNotFound, problem!, EditErrorKind.NotFound);
        }

        if (request.Start.Trigger is { } name && index.Trigger(name) == null)
        {
            return PlayRequestResult.Refused(status, MissionErrors.TriggerNotFound,
                $"The script {request.Script} has no trigger '{name}'. Give a trigger of the script.", EditErrorKind.NotFound);
        }

        var plan = StartPlan.Make(index, slot, request.Start, request.Place, request.PlaceBase);
        return plan.CanStart ? null : PlayRequestResult.Refused(status, "StartRefused", plan.Reason!) with { Plan = plan };
    }

    string? IMissionPlayHost.ProfilePath => editor.ProfilePath;

    ConsoleLog IMissionPlayHost.Log => editor.Log;

    (MissionIndex Index, StorySlot? Slot)? IMissionPlayHost.ModelOf(string script, out string? problem) =>
        editor.ModelOf(script, out problem);

    StartSavePlace? IMissionPlayHost.PlaceOf(StartPlace place, out string? problem) => editor.PlaceOf(place, out problem);
}
