using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using OpenLancer.Data.Editing;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>A host of the agent tools for the tests: a session, and the state of the host that each test sets.</summary>
internal sealed class TestAgentHost(EditSession? session, AgentHostKind kind = AgentHostKind.Headless) : AgentHost
{
    public override AgentHostKind Kind { get; } = kind;

    public override EditSession? Session { get; } = session;

    public override string? NoEditReason => Session?.ReadOnlyReason;

    public AgentLimits TestLimits { get; set; } = new();

    public override AgentLimits Limits => TestLimits;

    public HashSet<ToolGroup> Hidden { get; } = [];

    public override IReadOnlySet<ToolGroup> HiddenGroups => Hidden;

    public Dictionary<int, string> Names { get; } = [];

    public List<AgentConsoleEntry> ConsoleEntries { get; } = [];

    public AgentView? TestView { get; set; }

    public override AgentView? View => TestView;

    /// <summary>The result of <see cref="CheckTarget"/>, or null for a host without target checks.</summary>
    public Func<string, ToolStep>? Target { get; set; }

    public override string NameText(int ids) => Names.GetValueOrDefault(ids, "");

    public override string InfoText(int ids) => "";

    public override IReadOnlyList<AgentConsoleEntry>? Console(ConsoleFilter filter) =>
        Kind == AgentHostKind.Editor ? ConsoleEntries.Where(e => filter.Since == null || e.Sequence > filter.Since)
            .TakeLast(filter.Limit).ToList() : null;

    public override ToolStep CheckTarget(string target) => Target?.Invoke(target) ??
        throw new InvalidOperationException("The test host has no target checks.");
}

/// <summary>The calls of the agent tools for the tests, without a host thread.</summary>
internal static class AgentCalls
{
    /// <summary>Runs a call: the session part, then the tail when the call has one.</summary>
    public static ToolResult Call(AgentHost host, string tool, string arguments = "{}")
    {
        var step = AgentTools.Run(host, tool, JsonNode.Parse(arguments)!.AsObject());
        return step.Result ?? step.Tail!(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Runs a call that must give a result, not an error, and gives its structured content.</summary>
    public static JsonObject Ok(AgentHost host, string tool, string arguments = "{}")
    {
        var result = Call(host, tool, arguments);
        Assert.False(result.IsError, result.Text);
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find(tool)!.OutputSchema, result.Content));
        return result.Content;
    }

    /// <summary>Runs a call that must give a tool error, and gives its code and its detail.</summary>
    public static (string? Code, string? Detail, string? Message) Error(AgentHost host, string tool, string arguments = "{}")
    {
        var result = Call(host, tool, arguments);
        Assert.True(result.IsError, result.Text);
        return ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["message"]);
    }

    /// <summary>A text as a JSON string, for the arguments of a call.</summary>
    public static string Json(string text) => JsonSerializer.Serialize(text);
}
