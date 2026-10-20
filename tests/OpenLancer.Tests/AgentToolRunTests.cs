using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The run of a call of the tool table (section 5.3 of the MCP1 design): the tool <c>studio_status</c> with and without
/// a profile, and the check of the arguments before the handler.
/// </summary>
public sealed class AgentToolRunTests
{
    [Fact]
    public void StudioStatusTellsTheSessionAndWorksWithoutAProfile()
    {
        using var ws = SceneWorkspace.Create();
        var host = new TestAgentHost(ws.Session("act"));

        var status = Ok(host, "studio_status");
        var none = Ok(new TestAgentHost(null, AgentHostKind.Editor), "studio_status");

        Assert.Equal("headless", (string?)status["host"]);
        Assert.Equal("test", (string?)status["profile"]);
        Assert.Equal(["lower", "act"], status["mods"]!.AsArray().Select(m => (string)m!));
        Assert.Equal("act", (string?)status["active_mod"]);
        Assert.Equal(1, (int)status["tier"]!);
        Assert.True((bool)status["can_edit"]!);
        Assert.False((bool)status["dirty"]!);
        Assert.Equal("0", (string?)status["rev"]);
        Assert.Null(status["undo"]);
        Assert.Equal(host.ShownGroups.Select(g => g.ToString().ToLowerInvariant()),
            status["tool_groups"]!.AsArray().Select(g => (string)g!));
        Assert.Equal(200, (int)status["limits"]!["max_list_rows"]!);
        Assert.Equal("editor", (string?)none["host"]);
        Assert.Null(none["profile"]);
        Assert.False((bool)none["can_edit"]!);
        Assert.Equal("No profile is open. Open a profile in the editor, then try again.", (string?)none["reason"]);
    }

    [Fact]
    public void StudioStatusTellsTheUndoStepAndWhoMadeIt()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        session.Execute(OpenLancer.Data.Editing.EditCommand.SetLinesFromText(ListWorkspace.Li01("Li01_01"), "loadout",
            ["lo_other"]));

        var status = Ok(host, "studio_status");

        Assert.True((bool)status["dirty"]!);
        Assert.Equal("1", (string?)status["rev"]);
        Assert.Equal("user", (string?)status["undo"]!["origin"]);
        Assert.Equal("Set loadout", (string?)status["undo"]!["label"]);
    }

    /// <summary>A result that the host of the log gave, not the data, does not stop the replay (decision D728): the
    /// replay skips a call that did not run or that only the editor host has, and it does not compare a result of the
    /// limits of a host.</summary>
    [Fact]
    public void TheReplaySkipsTheResultsOfTheHostOfTheLog()
    {
        using var ws = SceneWorkspace.Create();
        var host = new TestAgentHost(ws.Session("act"));
        var log = string.Join("\n",
            """{"tool": "get_console", "arguments": {}, "result": {"status": "ok"}}""",
            """{"tool": "list_systems", "arguments": {}, "result": {"status": "error", "code": "busy", "detail": "Waited"}}""",
            """{"tool": "list_systems", "arguments_omitted": true, "result": {"status": "error", "code": "limit", "detail": "CallRate"}}""",
            """{"tool": "list_systems", "arguments": {}, "result": {"status": "error", "code": "limit", "detail": "ResultSize"}}""",
            """{"tool": "list_systems", "arguments": {"limit": 500}, "result": {"status": "ok"}}""");
        var output = new StringWriter();

        Assert.Null(AgentReplay.Run(host, new StringReader(log), output));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(["skipped", "skipped", "skipped", "ok", "error"], lines.Select(l => (string?)l["result"]!["status"]));
        Assert.Equal([null, null, null, false, false], lines.Select(l => (bool?)l["compared"]));
        Assert.Equal("Only the editor host has the tool get_console.", (string?)lines[0]["reason"]);
        Assert.Equal("Line 1: get_item gave error not_found, and the log has ok.", AgentReplay.Run(host,
            new StringReader("""{"tool": "get_item", "arguments": {"ref": "obj:none"}, "result": {"status": "ok"}}"""),
            new StringWriter()));
    }

    [Fact]
    public void AnArgumentOfAToolWithoutArgumentsIsRefused()
    {
        var none = Error(new TestAgentHost(null), "studio_status", """{ "x": 1 }""");

        Assert.Equal(("invalid_argument", "BadArgument"), (none.Code, none.Detail));
        Assert.Equal("The tool studio_status takes no arguments. Remove the argument 'x'.", none.Message);
    }
}
