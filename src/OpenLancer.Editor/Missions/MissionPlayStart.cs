using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Missions;

/// <summary>A request of a mission play (section 7.2 of the M6c design): the script, the start and the start
/// place.</summary>
/// <param name="Script">The install path of the script.</param>
internal sealed record MissionPlayRequest(string Script, MissionStartPoint Start)
{
    public StartPlaceChoice Place { get; init; }

    /// <summary>The base of <see cref="StartPlaceChoice.Base"/>, else null.</summary>
    public string? PlaceBase { get; init; }

    /// <summary>Who asks: the user (F5, the menus, the node menu) or an agent. A request of an agent never saves on its
    /// own, and it never changes the play start of the user (user decision of 2026-10-20 of the M6c design).</summary>
    public EditOrigin Origin { get; init; } = EditOrigin.User;
}

/// <summary>What a mission play start reads from the editor. <see cref="MissionPlay"/> gives it, and the tests give a
/// fake.</summary>
internal interface IMissionPlayHost
{
    /// <summary>The full path of the profile file, or null without an open profile. The play folder is next to
    /// it.</summary>
    string? ProfilePath { get; }

    ConsoleLog Log { get; }

    /// <summary>The index and the story slot of the model of a script now (section 7 of the M6b design). After the save
    /// of Play, the model is the model of the saved script. Null with the reason when the session has no such
    /// script.</summary>
    (MissionIndex Index, StorySlot? Slot)? ModelOf(string script, out string? problem);

    /// <summary>
    /// The place of the start save for the start place of a plan: a point in a system of the game data, or a base with
    /// its system. The Scene camera gets the start check of Play from camera. Null with the reason when the play cannot
    /// start there.
    /// </summary>
    StartSavePlace? PlaceOf(StartPlace place, out string? problem);

    /// <summary>Stores the play start of a script of the open profile (decision D762).</summary>
    void SetPlayStart(string script, MissionStartPoint start);
}

/// <summary>
/// The start of one mission play (sections 5.6 and 5.8 of the M6c design). Play calls <see cref="Prepare"/> after its
/// save and before each restart. It makes the start plan from the model of the saved script, and it writes the start save
/// and the test layer into the play folder. A play start whose trigger is gone becomes the mission start, and the
/// Console tells it (decision D762). A request of an agent keeps the play start of the user.
/// </summary>
internal sealed class MissionPlayStart(IMissionPlayHost host, MissionPlayRequest request) : IMissionStart
{
    public MissionPlayRequest Request { get; private set; } = request;

    public StartPlan? Plan { get; private set; }

    public string? Refusal { get; private set; }

    public string Script => Request.Script;

    /// <summary>The story slot of the plan, or the file name of a script without a slot.</summary>
    public string Slot => Plan?.Slot?.Slot ?? MissionFiles.Display(Request.Script);

    public string StartName => Plan?.StartName ??
                               StartPlan.NameOf(Request.Start.IsMissionStart ? StartKind.MissionStart : StartKind.Approximate,
                                   Request.Start);

    public bool Approximate => Plan?.Kind == StartKind.Approximate;

    public IReadOnlySet<string> SetupTriggers =>
        (Plan?.Setup ?? []).Select(t => t.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public PlayStart.FromSave? Prepare()
    {
        Refusal = null;
        if (host.ProfilePath is not { } profile)
        {
            return Refuse("No profile is open.");
        }

        if (host.ModelOf(Request.Script, out var missing) is not var (index, slot))
        {
            return Refuse(missing!);
        }

        if (Request.Start.Trigger is { } name && index.Trigger(name) is not { IsDuplicate: false })
        {
            host.Log.Add(ConsoleSeverity.Info, PlayController.Source, MissingStart(slot?.Slot ?? MissionFiles.Display(Script), name));
            Request = Request with { Start = MissionStartPoint.MissionStart };
            if (Request.Origin != EditOrigin.Agent)
            {
                host.SetPlayStart(Request.Script, MissionStartPoint.MissionStart);
            }
        }

        var plan = StartPlan.Make(index, slot, Request.Start, Request.Place, Request.PlaceBase);
        if (!plan.CanStart)
        {
            return Refuse(plan.Reason!);
        }

        if (host.PlaceOf(plan.Place, out var problem) is not { } place)
        {
            return Refuse(problem!);
        }

        var save = PlayFolder.StartSavePath(profile);
        var layer = PlayFolder.TestLayerPath(profile);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(save)!);
            File.WriteAllText(save, StartSaveText.OpenLancer(plan, place, DateTime.Now), new UTF8Encoding(false));
            // An exact start writes a layer without a patch, so a restart never reads the setup of an earlier start.
            TestLayer.Write(layer, plan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Refusal = $"Play cannot write the start save or the test layer of the mission: {e.Message}";
            host.Log.Add(ConsoleSeverity.Error, PlayController.Source, Refusal);
            return null;
        }

        Plan = plan;
        return new PlayStart.FromSave(save, layer);
    }

    /// <summary>The Console note of a play start whose trigger is gone (section 8.4 of the M6c design).</summary>
    public static string MissingStart(string slot, string trigger) =>
        $"The play start of {slot} named trigger '{trigger}'. The script does not have this trigger now. The play start " +
        "is the mission start again.";

    private PlayStart.FromSave? Refuse(string reason)
    {
        Refusal = reason;
        host.Log.Add(ConsoleSeverity.Warning, PlayController.Source, StartPlan.RefusalPrefix + reason);
        return null;
    }
}
