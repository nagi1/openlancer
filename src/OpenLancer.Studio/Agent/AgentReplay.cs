using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// <c>oltool mcp replay</c> (section 6.2 of the MCP1 design): runs each line of an audit log, or of a file of calls,
/// through the tool table, on the thread of the caller. It writes one result line for each call. When a line has a
/// recorded result, it compares the status and the error code with it. It is the form of the agent calls for CI, and a
/// user replays an agent run on a copy of the profile with it.
/// </summary>
public static class AgentReplay
{
    /// <summary>Runs the calls until the first difference.</summary>
    /// <param name="host">The host, which owns its session on the thread of the caller.</param>
    /// <param name="calls">The JSON lines of the calls.</param>
    /// <param name="output">Gets one JSON line for each call: the line number, the tool, the short result and the
    /// structured content.</param>
    /// <returns>Null when each result is as recorded, else why the replay stopped.</returns>
    /// <exception cref="InvalidDataException">A line is not a call. The message names the line.</exception>
    public static string? Run(AgentHost host, TextReader calls, TextWriter output)
    {
        foreach (var call in AuditLog.Read(calls))
        {
            if (AgentTools.Find(call.Tool) == null)
                return $"Line {call.Line}: the tool table has no tool '{call.Tool}'.";
            var step = AgentTools.Run(host, call.Tool, call.Arguments);
            var result = step.Result ?? step.Tail!(CancellationToken.None).GetAwaiter().GetResult();
            var outcome = result.Outcome;
            output.WriteLine(new JsonObject
            {
                ["line"] = call.Line,
                ["tool"] = call.Tool,
                ["result"] = AuditLog.Outcome(outcome),
                ["content"] = result.Content.DeepClone(),
            }.ToJsonString());
            if (call.Expected is { } expected && (expected.Status != outcome.Status || expected.Code != outcome.Code))
            {
                return $"Line {call.Line}: {call.Tool} gave {Describe(outcome)}, and the log has {Describe(expected)}.";
            }
            if (call.Expected == null && result.IsError)
                return $"Line {call.Line}: {call.Tool} gave {Describe(outcome)}: {(string?)result.Content["message"]}";
        }
        return null;
    }

    private static string Describe(ToolOutcome outcome) =>
        outcome.Code == null ? outcome.Status : $"{outcome.Status} {outcome.Code}";
}
