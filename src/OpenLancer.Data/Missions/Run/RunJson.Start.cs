using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Missions.Run;

/// <summary>A request of the start plan query (section 7.3 of the M6c design).</summary>
/// <param name="Script">The install path of the script.</param>
public sealed record StartPlanRequest(string Script, MissionStartPoint Start)
{
    /// <summary>The place choice: the suggestion, a base, or the Scene camera.</summary>
    public StartPlaceChoice Place { get; init; }

    /// <summary>The base of <see cref="StartPlaceChoice.Base"/>, else null.</summary>
    public string? PlaceBase { get; init; }
}

/// <summary>The status of a start plan in JSON: it can start, or the plan refuses.</summary>
public enum StartPlanStatus
{
    Ready,
    Refused,
}

/// <summary>The start place of a plan in JSON.</summary>
/// <param name="Text">The place in the words of the Play start page.</param>
/// <param name="Source">Where the place comes from.</param>
public sealed record StartPlaceView(StartPlaceKind Kind, string Text, string Source)
{
    /// <summary>The <c>system:</c> reference of a space start, or null.</summary>
    public string? System { get; init; }

    /// <summary>The <c>base:</c> reference of a base start, or null.</summary>
    public string? Base { get; init; }

    /// <summary>The point of a space start, or null.</summary>
    public IReadOnlyList<float>? Position { get; init; }

    /// <summary>The heading of a space start as Euler angles in degrees, or null.</summary>
    public IReadOnlyList<float>? Rotation { get; init; }
}

/// <summary>A setup trigger of a plan in JSON: its nickname and its lines as INI text.</summary>
public sealed record SetupTriggerView(string Nickname, IReadOnlyList<string> Lines);

/// <summary>The result of the start plan query (section 7.2 of the M6c design). A plan that cannot start is data: the
/// status is <c>refused</c>, and the reason says why.</summary>
/// <param name="Script">The <c>msn:</c> reference of the script.</param>
/// <param name="Version">The version of the script that the plan read.</param>
/// <param name="Start"><c>mission_start</c>, or the <c>trg:</c> reference of the trigger of the start.</param>
/// <param name="Target">The game of the play: <c>openlancer</c>.</param>
/// <param name="StartName">The name of the start in the UI, for example "Checkpoint t_restore".</param>
public sealed record StartPlanView(string Script, long Version, string Start, string Target, StartPlanStatus Status,
    StartKind Kind, string StartName)
{
    /// <summary>Why the plan cannot start, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>The story slot, for example <c>M02</c>, or null.</summary>
    public string? Slot { get; init; }

    /// <summary>The story stage of the start save.</summary>
    public int Stage { get; init; }

    /// <summary>The name of the story stage.</summary>
    public string? StageName { get; init; }

    /// <summary>True when the start comes after the accept of the mission.</summary>
    public bool Accepted { get; init; }

    /// <summary>The <c>trg:</c> references of the triggers that wait at the start.</summary>
    public IReadOnlyList<string> ActiveSet { get; init; } = [];

    /// <summary>The names of the triggers of the start save.</summary>
    public IReadOnlyList<string> SavedTriggers { get; init; } = [];

    /// <summary>The setup triggers of the test layer of the play.</summary>
    public IReadOnlyList<SetupTriggerView> Setup { get; init; } = [];

    /// <summary>The text id of the mission title of the start save, or null.</summary>
    public int? Title { get; init; }

    /// <summary>The text id of the mission offer of the start save, or null.</summary>
    public int? Offer { get; init; }

    /// <summary>The base scenes of the start save.</summary>
    public IReadOnlyList<string> Scenes { get; init; } = [];

    public StartPlaceView? Place { get; init; }

    /// <summary>The <c>trg:</c> references of the replay path, from the root to the trigger of the start.</summary>
    public IReadOnlyList<string> Path { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public static partial class RunJson
{
    /// <summary>The <c>start</c> of the mission start.</summary>
    public const string MissionStartName = "mission_start";

    /// <summary>The one target of a play from a trigger in this version. The linked game comes with milestone
    /// M6c-3.</summary>
    public const string OpenLancerTarget = "openlancer";

    /// <summary>The <c>start_place</c> of the suggested place.</summary>
    public const string SuggestedPlace = "suggested";

    /// <summary>The <c>start_place</c> of the Scene camera.</summary>
    public const string CameraPlace = "camera";

    /// <summary>The prefix of <c>start_place</c> for a base.</summary>
    public const string BasePlacePrefix = "base:";

    /// <summary>The text of a place choice: <c>suggested</c>, <c>camera</c> or <c>base:&lt;nickname&gt;</c>. The editor
    /// settings keep the play start in this form too. A base choice without a base gives the suggestion.</summary>
    public static string PlaceText(StartPlaceChoice place, string? dock) => place switch
    {
        StartPlaceChoice.Camera => CameraPlace,
        StartPlaceChoice.Base when dock != null => BasePlacePrefix + dock.Trim(),
        _ => SuggestedPlace,
    };

    /// <summary>The place choice of a text of <see cref="PlaceText"/>, or null when the text is not one.</summary>
    public static (StartPlaceChoice Place, string? Base)? ParsePlace(string text) => text switch
    {
        SuggestedPlace => (StartPlaceChoice.Suggested, null),
        CameraPlace => (StartPlaceChoice.Camera, null),
        _ when text.StartsWith(BasePlacePrefix, StringComparison.Ordinal) &&
               text[BasePlacePrefix.Length..].Trim() is { Length: > 0 } dock => (StartPlaceChoice.Base, dock),
        _ => null,
    };

    /// <summary>
    /// Reads a request of the start plan query: <c>script</c>, <c>start</c> (<c>mission_start</c> or a <c>trg:</c>
    /// reference), <c>target</c> (<c>openlancer</c>) and <c>start_place</c> (<c>suggested</c>, <c>base:&lt;nickname&gt;</c>
    /// or <c>camera</c>).
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the request does not fit the
    /// schema.</exception>
    public static StartPlanRequest ReadStartRequest(JsonObject request)
    {
        if (request["script"] is not JsonValue scriptValue || !scriptValue.TryGetValue(out string? scriptText))
            throw BadField("script", "The request has no script. Give the msn: reference of a script.");
        var script = ScriptRef.Parse(scriptText).Path;
        var start = ReadStart(request["start"], script);
        if (request["target"] is { } target &&
            (target is not JsonValue targetValue || !targetValue.TryGetValue(out string? word) || word != OpenLancerTarget))
            throw BadField("target", "This version plays from a trigger only in OpenLancer. Use the target openlancer.");
        var (place, dock) = ReadPlace(request["start_place"]);
        return new StartPlanRequest(script, start) { Place = place, PlaceBase = dock };
    }

    /// <summary>Reads <c>start</c>: <c>mission_start</c>, or the <c>trg:</c> reference of a trigger of the
    /// script.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the value is not one of
    /// them.</exception>
    public static MissionStartPoint ReadStart(JsonNode? node, string script)
    {
        if (node == null)
            return MissionStartPoint.MissionStart;
        if (node is not JsonValue value || !value.TryGetValue(out string? text))
            throw BadField("start", "The start is mission_start or the trg: reference of a trigger of the script.");
        if (text == MissionStartName)
            return MissionStartPoint.MissionStart;
        var trigger = TriggerRef.Parse(text);
        if (!VfsPath.Comparer.Equals(trigger.Script, script))
            throw BadField("start", $"The trigger {text} is not in the script of the request. Use a trigger of {script}.");
        return MissionStartPoint.At(trigger.Name);
    }

    /// <summary>Reads <c>start_place</c>: <c>suggested</c> (the default), <c>base:&lt;nickname&gt;</c> or
    /// <c>camera</c>.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the value is not one of
    /// them.</exception>
    public static (StartPlaceChoice Place, string? Base) ReadPlace(JsonNode? node)
    {
        if (node == null)
            return (StartPlaceChoice.Suggested, null);
        var text = node is JsonValue value && value.TryGetValue(out string? word) ? word : null;
        return (text == null ? null : ParsePlace(text)) ??
               throw BadField("start_place", "The start place is suggested, camera, or base: with the nickname of a " +
                                             "base, for example base:my_base.");
    }

    /// <summary>The text of a start for JSON: <c>mission_start</c>, or the <c>trg:</c> reference of the
    /// trigger.</summary>
    public static string StartText(string script, MissionStartPoint start) =>
        start.Trigger is { } trigger ? new TriggerRef(script, trigger).ToString() : MissionStartName;

    /// <summary>The JSON form of a plan.</summary>
    /// <param name="version">The version of the script that the plan read.</param>
    public static StartPlanView View(StartPlan plan, long version)
    {
        var script = plan.Script;
        string Ref(string trigger) => new TriggerRef(script, trigger).ToString();
        var view = new StartPlanView(new ScriptRef(script).ToString(), version, StartText(script, plan.Start),
            OpenLancerTarget, plan.CanStart ? StartPlanStatus.Ready : StartPlanStatus.Refused, plan.Kind, plan.StartName)
        {
            Reason = plan.Reason,
            Slot = plan.Slot?.Slot,
            Path = plan.Path.Select(Ref).ToList(),
            ActiveSet = plan.ActiveSet.Select(Ref).ToList(),
            Warnings = plan.Warnings,
        };
        if (!plan.CanStart)
            return view;
        var place = plan.Place;
        return view with
        {
            Stage = plan.Stage,
            StageName = StoryStages.NameOf(plan.Stage),
            Accepted = plan.Accepted,
            SavedTriggers = plan.SavedTriggers,
            Setup = plan.Setup.Select(t => new SetupTriggerView(t.Nickname,
                t.Lines.Select(l => l.Values.Count == 0 ? l.Key : $"{l.Key} = {string.Join(", ", l.Values)}").ToList())).ToList(),
            Title = plan.State.Title == 0 ? null : plan.State.Title,
            Offer = plan.State.Offer == 0 ? null : plan.State.Offer,
            Scenes = plan.State.Scenes.Select(s => s.File).ToList(),
            Place = new StartPlaceView(place.Kind, place.Text, place.Source)
            {
                System = place.System == null ? null : ItemRefs.Format(new ItemReference(ItemRefKind.System, place.System)),
                Base = place.Base == null ? null : ItemRefs.Format(new ItemReference(ItemRefKind.Base, place.Base)),
                Position = place.Kind == StartPlaceKind.Space ? [place.Position.X, place.Position.Y, place.Position.Z] : null,
                Rotation = place.Kind == StartPlaceKind.Space && place.Orientation.GetEulerDegrees() is var angles
                    ? [angles.X, angles.Y, angles.Z]
                    : null,
            },
        };
    }

    private static EditException BadField(string field, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, MissionErrors.BadJson, message) { Field = field });
}
