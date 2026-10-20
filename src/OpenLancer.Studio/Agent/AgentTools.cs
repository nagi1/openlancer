using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The agent tool table (section 5.3 of the MCP1 design): one row for each tool, in the fixed order of
/// <c>tools/list</c>. Both hosts serve this table, so a tool behaves the same in both (decision D700). A tool that waits
/// for another milestone is not in the table of this build (section 7.9).
/// </summary>
/// <remarks>This build has the query tools of section 7.1 (milestone MCP1-1a). Each of them changes nothing.</remarks>
public static class AgentTools
{
    private static readonly ToolParam Cursor = new TextParam("cursor", false, null, null);
    private static readonly ToolParam Limit = new WholeParam("limit", 1);
    private static readonly ToolParam TextFilter = new TextParam("text", false, null, null);

    /// <summary>The tools in the order of <c>tools/list</c>.</summary>
    public static IReadOnlyList<StudioTool> All { get; } =
    [
        Query("studio_status", ToolGroup.Studio, ToolInput.None, typeof(StudioStatus), StatusQueries.Status),
        Query("list_systems", ToolGroup.Scene, new ToolInput(TextFilter, Cursor, Limit), typeof(Page<SystemRow>),
            SceneQueries.ListSystems),
        Query("get_system", ToolGroup.Scene, new ToolInput(Text("system", true)), typeof(SystemView), SceneQueries.GetSystem),
        Query("list_items", ToolGroup.Scene, new ToolInput(
                Text("system", true),
                new ChoicesParam("kinds", SceneQueries.ItemKinds),
                new ChoicesParam("object_kinds", SceneQueries.ObjectKindNames),
                TextFilter,
                new GroupParam("near", [new PointParam("point", true), new NumberParam("radius", true, 0)]),
                new TextParam("origin", false, ["base", "lower_mod", "active_mod"], null),
                Cursor, Limit),
            typeof(Page<SceneItemRow>), SceneQueries.ListItems),
        Query("get_item", ToolGroup.Studio,
            new ToolInput(Text("ref", true), new FlagParam("include_raw"), new FlagParam("include_used_by")),
            typeof(ItemView), ItemQueries.GetItem),
        Query("find_references", ToolGroup.Studio, new ToolInput(Text("ref"), Text("name")), typeof(ReferencesView),
            ItemQueries.FindReferences),
        Query("list_values", ToolGroup.Studio, new ToolInput(Text("list", true), TextFilter, Text("system"), Cursor, Limit),
            typeof(Page<ValueRow>), ItemQueries.ListValues),
        Query("search_assets", ToolGroup.Scene, new ToolInput(
                new TextParam("kind", false, Enum.GetValues<AssetKind>().Select(k => Snake(k.ToString())).ToList(),
                    null),
                Text("category"), TextFilter, Cursor, Limit),
            typeof(Page<AssetItemRow>), ItemQueries.SearchAssets),
        Query("list_prop_groups", ToolGroup.Scene, new ToolInput(TextFilter), typeof(PropGroupsView),
            ItemQueries.ListPropGroups),
        Query("list_missions", ToolGroup.Missions, ToolInput.None, typeof(MissionsView), MissionQueryTools.ListMissions),
        Query("get_mission", ToolGroup.Missions, new ToolInput(
                Text("script", true),
                new TextParam("level", false, ["summary", "triggers"], "summary"),
                new FlagParam("include_lines")),
            typeof(MissionView), MissionQueryTools.GetMission),
        Query("get_trigger", ToolGroup.Missions, new ToolInput(Text("ref", true), new FlagParam("include_lines")),
            typeof(OpenLancer.Data.Missions.Editing.MissionTriggerView), MissionQueryTools.GetTrigger),
        Query("describe_mission_name", ToolGroup.Missions, new ToolInput(Text("key", true)),
            typeof(OpenLancer.Data.Missions.Editing.MissionPaletteName), MissionQueryTools.DescribeMissionName),
        Query("list_cutscenes", ToolGroup.Cutscenes,
            new ToolInput(new TextParam("category", false, CutsceneQueries.Categories, null), TextFilter, Cursor, Limit),
            typeof(Page<CutsceneRow>), CutsceneQueries.ListCutscenes),
        Query("get_cutscene", ToolGroup.Cutscenes, new ToolInput(Text("ref", true)), typeof(CutsceneView),
            CutsceneQueries.GetCutscene),
        Query("get_raw_ini", ToolGroup.Studio, new ToolInput(Text("ref"), Text("file")), typeof(RawIniView),
            ItemQueries.GetRawIni),
        Query("check", ToolGroup.Studio, new ToolInput(
                new TextParam("scope", false, CheckQueries.Scopes, "all"),
                Text("ref"),
                new TextParam("target", false, ["vanilla-1.1", "hde-0.7.1"], null),
                new FlagParam("include_base_game")),
            typeof(CheckView), CheckQueries.Check),
        Query("get_console", ToolGroup.Studio, new ToolInput(
                Text("source"),
                new TextParam("severity", false, CheckQueries.Severities, "info"),
                new FlagParam("include_base_game"),
                new WholeParam("since", 0),
                Limit),
            typeof(ConsoleView), CheckQueries.Console, ToolHosts.Editor),
    ];

    /// <summary>The tool with a name, or null.</summary>
    public static StudioTool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>
    /// Runs the session part of a call on the thread that owns the session: the host and the group checks, the check
    /// of the arguments, and the handler. A refusal is a tool error with its kind and its detail code.
    /// </summary>
    /// <exception cref="ArgumentException">The table has no tool with the name. The SDK binding refuses an unknown
    /// tool before it queues the call.</exception>
    public static ToolStep Run(AgentHost host, string name, JsonObject? arguments)
    {
        var tool = Find(name) ?? throw new ArgumentException($"The tool table has no tool {name}.", nameof(name));
        var hosts = host.Kind == AgentHostKind.Editor ? ToolHosts.Editor : ToolHosts.Headless;
        if ((tool.Hosts & hosts) == 0)
            return ToolResult.Failure(EditErrorKind.NotAvailable, "EditorOnly", AgentTexts.Error("editor_only", ("tool", name)));
        if (host.HiddenGroups.Contains(tool.Group))
        {
            return ToolResult.Failure(EditErrorKind.NotAvailable, "GroupHidden",
                AgentTexts.Error("group_hidden", ("group", Snake(tool.Group.ToString()))));
        }
        try
        {
            return tool.Run(new ToolCall(host, tool.Input.Read(name, arguments)));
        }
        catch (EditException e)
        {
            return ToolResult.Failure(e.Error);
        }
    }

    /// <summary>The snake case of an enum name, as the JSON of the results writes it, for example
    /// <c>lower_mod</c>.</summary>
    internal static string Snake(string name) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    private static TextParam Text(string name, bool required = false) => new(name, required, null, null);

    private static StudioTool Query(string name, ToolGroup group, ToolInput input, Type output, Func<ToolCall, ToolStep> run,
        ToolHosts hosts = ToolHosts.Both) => new(name, group, hosts, ToolHints.Query, input, output, run);
}
