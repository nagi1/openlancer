using System.Linq;
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

    [Fact]
    public void AnArgumentOfAToolWithoutArgumentsIsRefused()
    {
        var none = Error(new TestAgentHost(null), "studio_status", """{ "x": 1 }""");

        Assert.Equal(("invalid_argument", "BadArgument"), (none.Code, none.Detail));
        Assert.Equal("The tool studio_status takes no arguments. Remove the argument 'x'.", none.Message);
    }
}
