using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    public void TheToolsHaveTheFixedOrderOfTheQueryTools()
    {
        Assert.Equal(
        [
            "studio_status", "list_systems", "get_system", "list_items", "get_item", "find_references", "list_values",
            "search_assets", "list_prop_groups", "list_missions", "get_mission", "get_trigger", "describe_mission_name",
            "list_cutscenes", "get_cutscene", "get_raw_ini", "check", "get_console",
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

    [Fact]
    public void EachToolOfThisBuildIsAQueryAndNoToolReachesOutside()
    {
        Assert.All(AgentTools.All, t => Assert.Equal(new ToolHints(ReadOnly: true, Destructive: false, Idempotent: true), t.Hints));
        Assert.Equal(ToolHosts.Editor, AgentTools.Find("get_console")!.Hosts);
        Assert.All(AgentTools.All.Where(t => t.Name != "get_console"), t => Assert.Equal(ToolHosts.Both, t.Hosts));
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
            .Concat(AgentTexts.Errors.SelectMany(e => Problems($"error {e.Key}", e.Value)))
            .Concat(AgentTexts.Resources.SelectMany(r => Problems($"{r.Key} title", r.Value.Title)
                .Concat(Problems($"{r.Key} description", r.Value.Description))
                .Concat(r.Value.Text.SelectMany(line => Problems(r.Key, line)))))
            .ToList();

        Assert.Empty(problems);
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
}
