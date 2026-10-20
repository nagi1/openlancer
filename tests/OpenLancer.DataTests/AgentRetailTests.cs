using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Studio.Agent;

using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The query tools of the agent server on the vanilla data (section 13.3, retail variant, and section 13.4 of the MCP1
/// design): each tool runs without an exception, each result fits the output schema of its tool, and the session part
/// of each list and get tool is fast enough for one frame of the editor. The tests count and time. They hold no retail
/// text: the names of the calls come from the data at run time.
/// </summary>
public sealed class AgentRetailTests(ITestOutputHelper output)
{
    /// <summary>The time of one frame at 60 frames each second (section 13.4 of the MCP1 design).</summary>
    private const double FrameMilliseconds = 16;

    /// <summary>The calls of each query tool, with arguments from the data: the largest system, its first station,
    /// the first story script and its first trigger, and the first cutscene.</summary>
    private static List<(string Tool, string Arguments)> Calls(AgentHost host)
    {
        static string Json(string text) => JsonSerializer.Serialize(text);
        JsonObject Ok(string tool, string arguments = "{}")
        {
            var step = AgentTools.Run(host, tool, JsonNode.Parse(arguments)!.AsObject());
            Assert.NotNull(step.Result);
            Assert.False(step.Result!.IsError, step.Result.Text);
            return step.Result.Content;
        }

        var session = host.Session!;
        var systems = Ok("list_systems", """{ "limit": 200 }""")["rows"]!.AsArray()
            .Select(r => ((string)r!["nickname"]!, (string?)r["file"])).Where(r => r.Item2 != null).ToList();
        var largest = systems.MaxBy(s => session.Sections(s.Item2!).Count).Item1;
        var station = (string)Ok("list_items", $$"""{ "system": {{Json(largest)}}, "object_kinds": ["station"] }""")
            ["rows"]![0]!["ref"]!;
        var script = (string)Ok("list_missions")["scripts"]!.AsArray().First(s => s!["slot"] != null)!["ref"]!;
        var trigger = (string)Ok("get_mission", $$"""{ "script": {{Json(script)}} }""")["summary"]!["start_triggers"]![0]!;
        var cutscene = (string)Ok("list_cutscenes", """{ "limit": 1 }""")["rows"]![0]!["ref"]!;
        return
        [
            ("studio_status", "{}"),
            ("list_systems", "{}"),
            ("get_system", $$"""{ "system": {{Json(largest)}} }"""),
            ("list_items", $$"""{ "system": {{Json(largest)}} }"""),
            ("get_item", $$"""{ "ref": {{Json(station)}}, "include_raw": true }"""),
            ("find_references", $$"""{ "ref": {{Json(station)}} }"""),
            ("list_values", """{ "list": "solar_archetype", "text": "MISSION_SATELLITE" }"""),
            ("list_values", $$"""{ "list": "object_of_system", "system": {{Json(largest)}} }"""),
            ("search_assets", """{ "kind": "ship" }"""),
            ("list_prop_groups", "{}"),
            ("list_missions", "{}"),
            ("get_mission", $$"""{ "script": {{Json(script)}} }"""),
            ("get_trigger", $$"""{ "ref": {{Json(trigger)}} }"""),
            ("describe_mission_name", """{ "key": "Cnd_Timer" }"""),
            ("list_cutscenes", "{}"),
            ("get_cutscene", $$"""{ "ref": {{Json(cutscene)}} }"""),
            ("get_raw_ini", $$"""{ "ref": {{Json(station)}} }"""),
            ("check", $$"""{ "scope": "item", "ref": {{Json(station)}} }"""),
            ("check", "{}"),
        ];
    }

    [Fact]
    public void EachQueryToolRunsOnTheVanillaDataAndFitsItsSchema()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1agent");
        using var host = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId);

        var results = Calls(host).Select(c =>
        {
            var step = AgentTools.Run(host, c.Tool, JsonNode.Parse(c.Arguments)!.AsObject());
            return (c.Tool, Result: step.Result!);
        }).ToList();

        foreach (var (tool, result) in results)
        {
            Assert.False(result.IsError, $"{tool}: {result.Text}");
            Assert.Empty(OpenLancer.Tests.SchemaCheck.Problems(AgentTools.Find(tool)!.OutputSchema, result.Content));
            output.WriteLine($"{tool}: {result.Bytes} bytes");
        }
        Assert.Contains(results, r => r.Tool == "list_values" && r.Result.Content["total"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void TheSessionPartOfEachListAndGetToolFitsInOneFrame()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1bench");
        using var host = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId);

        var times = Calls(host).Where(c => c.Tool.StartsWith("list_") || c.Tool.StartsWith("get_"))
            .Select(c =>
            {
                var arguments = JsonNode.Parse(c.Arguments)!.AsObject();
                // The first call fills the caches of the session, as the first read of the editor does.
                AgentTools.Run(host, c.Tool, (JsonObject)arguments.DeepClone());
                var runs = Enumerable.Range(0, 5).Select(_ =>
                {
                    var start = Stopwatch.GetTimestamp();
                    AgentTools.Run(host, c.Tool, (JsonObject)arguments.DeepClone());
                    return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }).Order().ToList();
                return (Call: $"{c.Tool} {c.Arguments}", Median: runs[2]);
            }).ToList();

        foreach (var (call, median) in times)
            output.WriteLine($"{median,8:0.00} ms  {call}");
        Assert.All(times, t => Assert.True(t.Median < FrameMilliseconds, $"{t.Call}: {t.Median:0.00} ms"));
    }
}
