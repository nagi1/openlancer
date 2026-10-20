using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>What a call gives, in short, for the audit log and for <c>oltool mcp replay</c> (decision D728 of the
/// MCP1 design).</summary>
/// <param name="Status"><c>ok</c> for a result, <c>error</c> for a tool error.</param>
/// <param name="Code">The error kind of a tool error, for example <c>not_found</c>, else null.</param>
/// <param name="Detail">The detail code of a tool error, else null.</param>
public sealed record ToolOutcome(string Status, string? Code = null, string? Detail = null)
{
    public const string Ok = "ok";
    public const string Error = "error";
}

/// <summary>
/// The result of a call (section 5.7 of the MCP1 design): the JSON object in the snake case of the command layer, which
/// the SDK binding gives as <c>structuredContent</c> and as one text block. A tool error has <see cref="IsError"/>,
/// and its object has the error kind as <c>code</c>, the detail code as <c>detail</c> and the message.
/// </summary>
public sealed class ToolResult
{
    private static readonly JsonSerializerOptions Compact = new(EditJson.Options) { WriteIndented = false };

    private ToolResult(JsonObject content, bool isError)
    {
        Content = content;
        IsError = isError;
        Text = content.ToJsonString(Compact);
    }

    /// <summary>The structured result.</summary>
    public JsonObject Content { get; }

    /// <summary>True for a tool error. The model reads it and can correct its call.</summary>
    public bool IsError { get; }

    /// <summary>The JSON text of <see cref="Content"/>, without white space.</summary>
    public string Text { get; }

    /// <summary>The size of <see cref="Text"/> in bytes of UTF-8.</summary>
    public int Bytes => Encoding.UTF8.GetByteCount(Text);

    /// <summary>The short form of the result for the audit log.</summary>
    public ToolOutcome Outcome => IsError
        ? new ToolOutcome(ToolOutcome.Error, (string?)Content["code"], (string?)Content["detail"])
        : new ToolOutcome(ToolOutcome.Ok);

    /// <summary>The result of a query: the value in the JSON form of the command layer.</summary>
    public static ToolResult Of<T>(T value) where T : notnull =>
        new((JsonObject)JsonSerializer.SerializeToNode(value, EditJson.Options)!, false);

    /// <summary>A tool error: the error kind as <c>code</c>, the detail code as <c>detail</c>, the message, and the
    /// target and the argument when the error names them.</summary>
    public static ToolResult Failure(EditError error) =>
        new((JsonObject)JsonSerializer.SerializeToNode(error, EditJson.Options)!, true);

    /// <summary>A tool error of the host, with a text of <c>agent-tools.json</c>.</summary>
    internal static ToolResult Failure(EditErrorKind kind, string detail, string message) =>
        Failure(new EditError(kind, detail, message));
}

/// <summary>
/// What the session part of a call gives (decision D765 of the MCP1 design): the result, or a tail. A tail runs on a
/// worker, does not touch the session, and gives the result later. The next call can start while a tail runs.
/// </summary>
public sealed class ToolStep
{
    private ToolStep(ToolResult? result, Func<CancellationToken, Task<ToolResult>>? tail)
    {
        Result = result;
        Tail = tail;
    }

    /// <summary>The result, or null when the call has a tail.</summary>
    public ToolResult? Result { get; }

    /// <summary>The tail, or null. It gets a token that the time limit of the tail and the client cancel.</summary>
    public Func<CancellationToken, Task<ToolResult>>? Tail { get; }

    public static ToolStep Then(Func<CancellationToken, Task<ToolResult>> tail) => new(null, tail);

    public static implicit operator ToolStep(ToolResult result) => new(result, null);
}
