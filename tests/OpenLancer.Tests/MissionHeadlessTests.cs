using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The headless test of section 11.3 of the M6b design: a session on a test profile runs a JSON batch through the host,
/// saves, opens the profile again and reads the patch. So the layer runs without ImGui, as MCP1 needs.
/// </summary>
public sealed class MissionHeadlessTests
{
    [Fact]
    public void AJsonBatchRunsSavesAndTheNextSessionReadsThePatch()
    {
        using var m = Create();
        var host = new MissionJsonHost(m.Session);
        var script = Script.Replace(@"\", @"\\");

        var reply = host.Edit($$"""
            {
              "label": "Wait longer",
              "expect_versions": { "{{script}}": 1 },
              "commands": [
                { "kind": "set_condition_value", "trigger": "trg:{{script}}#ol_wave", "name": "seconds", "value": 9 },
                { "kind": "add_trigger", "script": "msn:{{script}}", "nickname": "ol_agent",
                  "condition": { "key": "Cnd_Timer", "values": { "seconds": 2 } },
                  "actions": [ { "key": "Act_ActTrig", "values": { "trigger": "ol_end" } } ] }
              ]
            }
            """);
        var saved = m.Session.Save();
        m.Session.Dispose();

        Assert.False(reply.IsError, reply.Json);
        var result = JsonNode.Parse(reply.Json)!;
        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal("Agent: Wait longer", (string?)result["undo_label"]);
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        var patch = m.Ws.ReadModPatch(Script);
        Assert.Contains("Cnd_Timer = 9", patch);
        Assert.Contains("nickname = ol_agent", patch);
        var again = m.Ws.Session("act");
        var trigger = new MissionJsonHost(again).Get($"trg:{Script}#ol_agent");
        Assert.False(trigger.IsError, trigger.Json);
        Assert.Equal("Cnd_Timer", (string?)JsonNode.Parse(trigger.Json)!["lines"]![1]!["key"]);
        Assert.DoesNotContain(typeof(MissionEditor).Assembly.GetReferencedAssemblies(),
            a => a.Name!.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARefusalAndABadReferenceAreErrorsWithCodeAndDetail()
    {
        using var m = Create();
        var host = new MissionJsonHost(m.Session);
        var script = Script.Replace(@"\", @"\\");

        var stale = host.Edit($$"""{ "label": "x", "expect_versions": { "{{script}}": 7 }, "commands": [] }""");
        var unknown = host.Edit("""{ "label": "x", "commands": [ { "kind": "launch_rocket" } ] }""");
        var field = host.Edit($$"""{ "label": "x", "commands": [ { "kind": "remove_trigger", "trigger": "trg:{{script}}#ol_end", "nickame": "y" } ] }""");
        var missing = host.Get($"trg:{Script}#ol_none");

        Assert.All(new[] { stale, unknown, field, missing }, r => Assert.True(r.IsError));
        Assert.Equal(("stale_revision", "ScriptChanged"), Error(stale));
        Assert.Equal(("invalid_argument", "UnknownCommand"), Error(unknown));
        Assert.Equal(("invalid_argument", "UnknownField"), Error(field));
        Assert.Equal(("not_found", "TriggerNotFound"), Error(missing));
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void AnEditThatLeavesMissingLinesNeedsAConfirmationForAnAgent()
    {
        using var m = Create();
        var host = new MissionJsonHost(m.Session);
        var script = Script.Replace(@"\", @"\\");
        var remove = $$"""{ "kind": "remove_trigger", "trigger": "trg:{{script}}#ol_end" }""";

        var asked = host.Edit($$"""{ "label": "Remove the end", "commands": [ {{remove}} ] }""");
        var confirmed = host.Edit($$"""{ "label": "Remove the end", "confirm": true, "commands": [ {{remove}} ] }""");

        Assert.True(asked.IsError);
        Assert.Equal("needs_confirmation", Error(asked).Code);
        Assert.False(confirmed.IsError, confirmed.Json);
        Assert.Equal(3, JsonNode.Parse(confirmed.Json)!["missing"]!.AsArray().Count);
    }

    [Fact]
    public void TheQueriesAndTheSchemaGiveJson()
    {
        using var m = Create();
        var host = new MissionJsonHost(m.Session);

        Assert.Single(JsonNode.Parse(host.ListScripts().Json)!.AsArray());
        Assert.Equal($"msn:{Script}", (string?)JsonNode.Parse(host.Get($"msn:{Script}").Json)!["ref"], ignoreCase: true);
        Assert.NotEmpty(JsonNode.Parse(host.Palette().Json)!.AsArray());
        Assert.NotEmpty(JsonNode.Parse(host.Prefabs().Json)!.AsArray());
        Assert.NotNull(JsonNode.Parse(host.Problems($"msn:{Script}").Json)!.AsArray());
        Assert.Equal(2, JsonNode.Parse(host.FindReferences($"sec:{Script}#MsnShip/ol_ship_1").Json)!.AsArray().Count);
        Assert.Equal("Mission batch", (string?)JsonNode.Parse(MissionJsonHost.Schema())!["title"]);
    }

    private static (string Code, string Detail) Error(MissionHostReply reply)
    {
        var error = JsonNode.Parse(reply.Json)!["errors"]![0]!;
        return ((string)error["code"]!, (string)error["detail"]!);
    }
}
