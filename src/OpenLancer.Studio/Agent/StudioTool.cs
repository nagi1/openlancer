using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The program that serves the agent tools (section 5.4 of the MCP1 design).</summary>
public enum AgentHostKind
{
    /// <summary>The agent server of the editor. It shares the session of the open profile with the user.</summary>
    Editor,

    /// <summary><c>oltool mcp serve</c>. It has its own session and no window.</summary>
    Headless,
}

/// <summary>The hosts that have a tool. "E" in the tool catalog of the MCP1 design marks an editor tool.</summary>
[Flags]
public enum ToolHosts
{
    Editor = 1,
    Headless = 2,
    Both = Editor | Headless,
}

/// <summary>
/// The groups of the tools (decision D725 of the MCP1 design). The user can hide each group except
/// <see cref="Studio"/>. A tool of a hidden group stays in the tool list and gives <c>not_available</c>.
/// </summary>
public enum ToolGroup
{
    /// <summary>The tools of the session, the checks and the reads of each kind of item. The user cannot hide
    /// them.</summary>
    Studio,

    /// <summary>The star systems, their items, the assets and the prop groups.</summary>
    Scene,

    /// <summary>The mission scripts.</summary>
    Missions,

    /// <summary>The cutscene scripts.</summary>
    Cutscenes,
}

/// <summary>The hints of a tool for the client (section 7.0 of the MCP1 design). No tool reaches outside the profile,
/// so the hint "open world" is always false.</summary>
/// <param name="ReadOnly">True when the tool changes nothing.</param>
/// <param name="Destructive">True when the tool can remove data or write files.</param>
/// <param name="Idempotent">True when a second call with the same arguments changes nothing more.</param>
public sealed record ToolHints(bool ReadOnly, bool Destructive, bool Idempotent)
{
    /// <summary>The hints of a query tool.</summary>
    public static readonly ToolHints Query = new(true, false, true);
}

/// <summary>
/// One row of the agent tool table (section 5.3 of the MCP1 design). The row has no type of the MCP SDK, so a change
/// of the protocol touches only the SDK binding (risk R1). The title, the description and the help of each argument
/// come from <c>agent-tools.json</c> (decision D726).
/// </summary>
public sealed class StudioTool
{
    private JsonObject? inputSchema;
    private JsonObject? outputSchema;

    /// <param name="output">The type of the structured result. The output schema comes from it.</param>
    /// <param name="run">The session part of a call. It runs on the thread that owns the session.</param>
    internal StudioTool(string name, ToolGroup group, ToolHosts hosts, ToolHints hints, ToolInput input, Type output,
        Func<ToolCall, ToolStep> run)
    {
        Name = name;
        Group = group;
        Hosts = hosts;
        Hints = hints;
        Input = input;
        Output = output;
        Run = run;
    }

    /// <summary>The name, for example <c>list_items</c>. The client adds its own prefix.</summary>
    public string Name { get; }

    public ToolGroup Group { get; }

    public ToolHosts Hosts { get; }

    public ToolHints Hints { get; }

    /// <summary>The title for a person, for example "List the items of a system".</summary>
    public string Title => AgentTexts.Tool(Name).Title;

    /// <summary>What the tool does, in strict STE (section 9.1 of the MCP1 design).</summary>
    public string Description => AgentTexts.Tool(Name).Description;

    /// <summary>The JSON Schema of the arguments.</summary>
    public JsonObject InputSchema => (JsonObject)(inputSchema ??= Input.Schema(Name)).DeepClone();

    /// <summary>The JSON Schema of the structured result. A tool error has its own form (see
    /// <see cref="ToolResult.Failure"/>), and the client does not check it against this schema.</summary>
    public JsonObject OutputSchema => (JsonObject)(outputSchema ??= SchemaOf(Output)).DeepClone();

    internal ToolInput Input { get; }

    internal Type Output { get; }

    internal Func<ToolCall, ToolStep> Run { get; }

    /// <summary>
    /// The JSON Schema of a result type, in the JSON form of the command layer (snake case, enums as text). The
    /// serializer and the schema use the same options, so the schema fits each result. The command layer leaves out a
    /// null value (<see cref="EditJson.Options"/>), so a property that can be null is not required. The schema has no
    /// <c>format</c>, because a client can refuse a format that its validator does not know. A time is text in the ISO
    /// 8601 form.
    /// </summary>
    internal static JsonObject SchemaOf(Type type)
    {
        var options = new JsonSerializerOptions(EditJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.MakeReadOnly();
        var schema = (JsonObject)JsonSchemaExporter.GetJsonSchemaAsNode(options, type,
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        OptionalNulls(schema);
        return schema;
    }

    /// <summary>Removes each property that can be null from the <c>required</c> list of its object, and each
    /// <c>format</c>, in the whole schema.</summary>
    private static void OptionalNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("format");
                if (obj["required"] is JsonArray required && obj["properties"] is JsonObject properties)
                {
                    foreach (var name in required.Select(r => (string)r!).ToList())
                    {
                        if (properties[name]?["type"] is JsonArray types && types.Any(t => (string?)t == "null"))
                            required.Remove(required.First(r => (string?)r == name));
                    }
                    if (required.Count == 0)
                        obj.Remove("required");
                }
                foreach (var (_, child) in obj.ToList())
                    OptionalNulls(child);
                break;
            case JsonArray array:
                foreach (var child in array)
                    OptionalNulls(child);
                break;
        }
    }
}
