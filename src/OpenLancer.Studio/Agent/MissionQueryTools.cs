using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Agent;

/// <summary>The result of <c>list_missions</c>: the story slots and the other scripts.</summary>
internal sealed record MissionsView(IReadOnlyList<MissionScriptRow> Scripts);

/// <summary>The result of <c>get_mission</c>: the summary, and with the level <c>triggers</c> each trigger.</summary>
internal sealed record MissionView(MissionOverview Summary)
{
    /// <summary>Each trigger of the script, with the level <c>triggers</c>, else null.</summary>
    public IReadOnlyList<MissionTriggerView>? Triggers { get; init; }
}

/// <summary>
/// The mission query tools of section 7.1 of the MCP1 design. Each one calls a read query of the mission layer
/// (section 4.12 of the M6b design), so the agent and the mission studio read the same model.
/// </summary>
internal static class MissionQueryTools
{
    public static ToolStep ListMissions(ToolCall call) =>
        ToolResult.Of(new MissionsView(call.Session.Missions.Queries.ListScripts()));

    public static ToolStep GetMission(ToolCall call)
    {
        var queries = call.Session.Missions.Queries;
        var script = ScriptRef.Parse(call.Args.Text("script")!);
        var summary = queries.GetMission(script);
        if (call.Args.Text("level") != "triggers")
            return ToolResult.Of(new MissionView(summary));
        var lines = call.Args.Flag("include_lines");
        var model = call.Session.Missions.Model(script.Path);
        // A trigger reference names the first trigger with its nickname, as the game does.
        var triggers = model.Index.Triggers.Where(t => !t.IsDuplicate)
            .Select(t => Lines(queries.GetTrigger(new TriggerRef(model.Script, t.Nickname)), lines))
            .ToList();
        return ToolResult.Of(new MissionView(summary) { Triggers = triggers });
    }

    public static ToolStep GetTrigger(ToolCall call) => ToolResult.Of(
        Lines(call.Session.Missions.Queries.GetTrigger(TriggerRef.Parse(call.Args.Text("ref")!)), call.Args.Flag("include_lines")));

    public static ToolStep DescribeMissionName(ToolCall call)
    {
        var key = call.Args.Text("key")!.Trim();
        var palette = call.Session.Missions.Queries.ListPalette();
        return palette.FirstOrDefault(n => n.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } row
            ? ToolResult.Of(row)
            : throw new EditException(new EditError(EditErrorKind.NotFound, "NameNotFound",
                AgentTexts.Error("mission_name_none", ("key", key)) + ItemRefs.Hint(key, palette.Select(n => n.Name)))
            {
                Field = "key",
            });
    }

    /// <summary>A trigger with the raw texts of its lines only on request. The named values and the plain names stay,
    /// so the agent reads the actions without the raw lines (UI rule 5).</summary>
    private static MissionTriggerView Lines(MissionTriggerView trigger, bool raw) => raw
        ? trigger
        : trigger with { Lines = trigger.Lines.Select(l => l with { Texts = [] }).ToList() };
}
