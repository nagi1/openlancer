using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
    /// <summary>The tools in the order of <c>tools/list</c>.</summary>
    public static IReadOnlyList<StudioTool> All { get; } =
    [
        Query("studio_status", ToolGroup.Studio, ToolInput.None, typeof(StudioStatus), StatusQueries.Status),
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

    private static StudioTool Query(string name, ToolGroup group, ToolInput input, Type output, Func<ToolCall, ToolStep> run,
        ToolHosts hosts = ToolHosts.Both) => new(name, group, hosts, ToolHints.Query, input, output, run);
}
