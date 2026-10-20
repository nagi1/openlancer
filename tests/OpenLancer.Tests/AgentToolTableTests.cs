using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The tool table of the agent server (section 13.1, items 1 to 3 of the MCP1 design): the names, the texts, the
/// schemas and the hints of each tool, and the STE lint of each text of <c>agent-tools.json</c>.
/// </summary>
public sealed class AgentToolTableTests
{
    [Fact]
    public void TheToolsHaveTheFixedOrderOfTheQueryToolsThenTheViewTools()
    {
        Assert.Equal(
        [
            "studio_status", "list_systems", "get_system", "list_items", "get_item", "find_references", "list_values",
            "search_assets", "list_prop_groups", "list_missions", "get_mission", "get_trigger", "describe_mission_name",
            "mission_dry_run", "list_cutscenes", "get_cutscene", "get_raw_ini", "check", "get_console",
            "open_view", "select_items", "render_scene", "render_cutscene", "capture_panel",
        ], AgentTools.All.Select(t => t.Name));
    }

    [Fact]
    public void EachToolHasAValidNameATitleADescriptionAndObjectSchemas()
    {
        foreach (var tool in AgentTools.All)
        {
            Assert.Matches(new Regex("^[a-z][a-z0-9_]{0,127}$"), tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Title), tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);

            var input = tool.InputSchema;
            Assert.Empty(SchemaCheck.SchemaProblems(input));
            Assert.Equal("object", (string?)input["type"]);
            Assert.False((bool)input["additionalProperties"]!);
            foreach (var (name, property) in input["properties"]!.AsObject())
                Assert.False(string.IsNullOrWhiteSpace((string?)property!["description"]), $"{tool.Name}.{name}");

            var output = tool.OutputSchema;
            Assert.Empty(SchemaCheck.SchemaProblems(output));
            Assert.Contains("object", output["type"] is JsonArray types ? types.Select(t => (string?)t) : [(string?)output["type"]]);
        }
    }

    /// <summary>No tool of this build changes the data (section 7.0 of the MCP1 design). <c>open_view</c> and
    /// <c>select_items</c> change what the user sees, so they are not read-only. The Console and the view tools need the
    /// window of the editor.</summary>
    [Fact]
    public void NoToolOfThisBuildChangesTheDataAndTheViewToolsNeedTheEditor()
    {
        string[] views = ["open_view", "select_items"];
        string[] editor = ["get_console", "open_view", "select_items", "render_scene", "render_cutscene", "capture_panel"];

        Assert.All(AgentTools.All.Where(t => !views.Contains(t.Name)),
            t => Assert.Equal(new ToolHints(ReadOnly: true, Destructive: false, Idempotent: true), t.Hints));
        Assert.All(views.Select(n => AgentTools.Find(n)!),
            t => Assert.Equal(new ToolHints(ReadOnly: false, Destructive: false, Idempotent: true), t.Hints));
        Assert.All(editor.Select(n => AgentTools.Find(n)!), t => Assert.Equal(ToolHosts.Editor, t.Hosts));
        Assert.All(AgentTools.All.Where(t => !editor.Contains(t.Name)), t => Assert.Equal(ToolHosts.Both, t.Hosts));
    }

    [Fact]
    public void TheTextFileHasTheTextsOfEachToolAndNoOtherTool()
    {
        Assert.Equal(AgentTools.All.Select(t => t.Name).Order(), AgentTexts.Tools.Keys.Order());
    }

    [Fact]
    public void EachTextPassesTheSteLint()
    {
        var problems = AgentTexts.Instructions.SelectMany(t => Problems("instruction", t, SteLint.InstructionWords))
            .Concat(AgentTexts.SharedFields.SelectMany(f => Problems($"field {f.Key}", f.Value)))
            .Concat(AgentTexts.Tools.SelectMany(t => Problems($"{t.Key} title", t.Value.Title)
                .Concat(Problems($"{t.Key} description", t.Value.Description))
                .Concat(t.Value.Fields.SelectMany(f => Problems($"{t.Key}.{f.Key}", f.Value)))))
            .Concat(AgentTexts.Errors.SelectMany(e => Problems($"error {e.Key}", e.Value, SteLint.InstructionWords)))
            .Concat(AgentTexts.Results.SelectMany(r => Problems($"result {r.Key}", r.Value)))
            .Concat(AgentTexts.Resources.SelectMany(r => Problems($"{r.Key} title", r.Value.Title)
                .Concat(Problems($"{r.Key} description", r.Value.Description))
                .Concat(r.Value.Text.SelectMany(line => Problems(r.Key, line)))))
            .ToList();

        Assert.Empty(problems);
    }

    /// <summary>Each property of each output schema has a help of <c>agent-tools.json</c> (section 9.1 of the MCP1
    /// design), so the agent can read what a field means. A help of the file that names no property is an error
    /// too.</summary>
    [Fact]
    public void EachPropertyOfEachResultHasAHelpAndEachHelpNamesAProperty()
    {
        var properties = new HashSet<(Type Owner, string Name)>();
        var options = new JsonSerializerOptions(EditJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.MakeReadOnly();
        foreach (var type in AgentTools.All.Select(t => t.Output).Append(typeof(EditError)).Distinct())
        {
            JsonSchemaExporter.GetJsonSchemaAsNode(options, type, new JsonSchemaExporterOptions
            {
                TransformSchemaNode = (context, node) =>
                {
                    if (context.PropertyInfo is { DeclaringType: var owner, Name: var name })
                        properties.Add((owner, name));
                    return node;
                },
            });
        }
        var typed = properties.Select(p => $"{AgentTexts.TypeName(p.Owner)}.{p.Name}").ToHashSet();
        var shared = properties.Where(p => !AgentTexts.Results.ContainsKey($"{AgentTexts.TypeName(p.Owner)}.{p.Name}"))
            .Select(p => p.Name).ToHashSet();

        var missing = properties.Where(p => AgentTexts.Result(p.Owner, p.Name) == null)
            .Select(p => $"{AgentTexts.TypeName(p.Owner)}.{p.Name}").Order().ToList();
        var unused = AgentTexts.Results.Keys.Where(k => k.Contains('.') ? !typed.Contains(k)
            : !shared.Contains(k) && k is not ("result" or "error" or StudioTool.RevisionName)).Order().ToList();
        Assert.True(missing.Count == 0, "No help: " + string.Join(", ", missing));
        Assert.True(unused.Count == 0, "Help of no property: " + string.Join(", ", unused));
        foreach (var tool in AgentTools.All)
        {
            Assert.Empty(Undescribed(tool.OutputSchema, tool.Name));
            Assert.Equal([AgentTexts.Result(null, "result"), AgentTexts.Result(null, "error")],
                tool.OutputSchema["anyOf"]!.AsArray().Select(o => (string?)o!["description"]));
        }
    }

    /// <summary>The structured content of a tool error fits the output schema of each tool (section 5.7 of the MCP1
    /// design). A client that checks the structured content of each call then gets the message of the error.</summary>
    [Fact]
    public void EachOutputSchemaTakesAToolErrorAndNotAnEmptyResult()
    {
        var error = ToolResult.Failure(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound, "The edit view has no x.")
        {
            Field = "ref",
        });

        foreach (var tool in AgentTools.All)
        {
            Assert.Empty(SchemaCheck.Problems(tool.OutputSchema, error.Content));
            Assert.Equal("string", (string?)tool.OutputSchema["properties"]![StudioTool.RevisionName]!["type"]);
            if (tool.OutputSchema["anyOf"]![0]!["required"] is JsonArray { Count: > 0 })
                Assert.NotEmpty(SchemaCheck.Problems(tool.OutputSchema, new JsonObject()));
        }
    }

    [Fact]
    public void TheServerInstructionsAreShortSteps()
    {
        Assert.InRange(AgentTexts.Instructions.Count, 3, 10);
        Assert.StartsWith("Call studio_status first.", AgentTexts.Instructions[0]);
        Assert.Equal(string.Join("\n", AgentTexts.Instructions), AgentResources.Instructions);
    }

    private static string[] Problems(string where, string text, int words = SteLint.DescriptionWords) =>
        SteLint.Problems(text, words).Select(p => $"{where}: {p}: {text}").ToArray();

    /// <summary>The paths of the properties of a schema without a help. A property that only refers to another schema
    /// (<c>$ref</c>) gets the help of that schema.</summary>
    private static IEnumerable<string> Undescribed(JsonNode? schema, string path)
    {
        if (schema is JsonArray array)
            return array.SelectMany((item, i) => Undescribed(item, $"{path}[{i}]"));
        if (schema is not JsonObject obj)
            return [];
        var own = obj["properties"] is JsonObject properties
            ? properties.Where(p => p.Value is JsonObject property && !property.ContainsKey("$ref") &&
                                    property["description"] == null)
                .Select(p => $"{path}.{p.Key}")
            : [];
        return own.Concat(obj.SelectMany(p => Undescribed(p.Value, $"{path}/{p.Key}")));
    }
}
