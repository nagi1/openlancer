using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenLancer.Studio.Agent;

/// <summary>One line of an audit log, or of a file of calls for <c>oltool mcp replay</c>.</summary>
/// <param name="Line">The line number in the file, from 1.</param>
/// <param name="Expected">The recorded result, or null when the line has none.</param>
public sealed record AuditEntry(int Line, string Tool, JsonObject? Arguments, ToolOutcome? Expected);

/// <summary>
/// The audit log of the agent calls (decision D728 of the MCP1 design): one JSON line for each call, with the tool, the
/// arguments and the short result. The editor writes one file for each run into the log folder of OpenLancer. The file
/// holds no token, because the token is a header of the request and never an argument. <c>oltool mcp replay</c> reads
/// the same format, so a user can replay an agent run on a copy of the profile.
/// </summary>
/// <remarks>The format of a line: <c>{"time": "...", "tool": "list_items", "arguments": {...}, "result": {"status":
/// "ok"}}</c>. A tool error has <c>"status": "error"</c> with <c>code</c> and <c>detail</c>.</remarks>
public sealed class AuditLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;

    /// <summary>Opens a log file for append.</summary>
    /// <exception cref="IOException">The file does not open.</exception>
    /// <exception cref="UnauthorizedAccessException">The file does not open.</exception>
    public AuditLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FilePath = path;
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>The path of the file.</summary>
    public string FilePath { get; }

    /// <summary>The file name of the log of a run that starts at a time: <c>agent-20261020-142233.jsonl</c>.</summary>
    public static string FileName(DateTime startUtc) =>
        "agent-" + startUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>Adds the line of one call. It can run on any thread.</summary>
    public void Write(DateTimeOffset time, string tool, JsonObject? arguments, ToolOutcome outcome)
    {
        var line = new JsonObject
        {
            ["time"] = time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["tool"] = tool,
            ["arguments"] = arguments?.DeepClone() ?? new JsonObject(),
            ["result"] = Outcome(outcome),
        };
        lock (gate)
        {
            try
            {
                writer.WriteLine(line.ToJsonString());
            }
            catch (IOException)
            {
                // A full disk stops the log, not the call.
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
            writer.Dispose();
    }

    /// <summary>The short result in its JSON form.</summary>
    public static JsonObject Outcome(ToolOutcome outcome)
    {
        var node = new JsonObject { ["status"] = outcome.Status };
        if (outcome.Code != null)
            node["code"] = outcome.Code;
        if (outcome.Detail != null)
            node["detail"] = outcome.Detail;
        return node;
    }

    /// <summary>
    /// Reads the lines of a log or of a file of calls. An empty line is skipped. A line has <c>tool</c>, optional
    /// <c>arguments</c> and an optional recorded <c>result</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">A line is not such a JSON object. The message names the line.</exception>
    public static IEnumerable<AuditEntry> Read(TextReader reader)
    {
        var number = 0;
        while (reader.ReadLine() is { } text)
        {
            number++;
            if (string.IsNullOrWhiteSpace(text))
                continue;
            JsonObject line;
            try
            {
                line = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException(
                    $"Line {number} is not a JSON object.");
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"Line {number} is not valid JSON: {e.Message}");
            }
            if (line["tool"] is not JsonValue tool || tool.GetValueKind() != JsonValueKind.String)
                throw new InvalidDataException($"Line {number} has no tool name.");
            if (line["arguments"] is { } arguments && arguments is not JsonObject)
                throw new InvalidDataException($"The arguments of line {number} are not a JSON object.");
            ToolOutcome? expected = null;
            if (line["result"] is JsonObject result)
            {
                expected = (string?)result["status"] is { } status
                    ? new ToolOutcome(status, (string?)result["code"], (string?)result["detail"])
                    : throw new InvalidDataException($"The result of line {number} has no status.");
            }
            yield return new AuditEntry(number, tool.GetValue<string>(), (JsonObject?)line["arguments"]?.DeepClone(),
                expected);
        }
    }
}
