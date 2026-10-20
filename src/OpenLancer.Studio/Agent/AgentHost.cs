using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The limits of one agent call (section 8.4 of the MCP1 design). They are settings of the editor, with these defaults
/// (decision D724). The limits of the edits come with the edit tools.
/// </summary>
public sealed record AgentLimits
{
    /// <summary>The rows of a list result when the call gives no <c>limit</c>.</summary>
    public int ListRows { get; init; } = 50;

    /// <summary>The most rows of one list result. A larger <c>limit</c> gives the error <c>limit</c>.</summary>
    public int MaxListRows { get; init; } = 200;

    /// <summary>The most text of one result, in kilobytes. A larger result gives the error <c>limit</c>.</summary>
    public int ResultKilobytes { get; init; } = 256;

    /// <summary>The most calls that the host takes in one second. The context of the model and the frame time of the
    /// editor need it.</summary>
    public int CallsPerSecond { get; init; } = 10;

    /// <summary>How long a call waits for the thread of the session, in seconds. Then it gives <c>busy</c>.</summary>
    public int WaitSeconds { get; init; } = 30;

    /// <summary>How long the tail of a call can run, in seconds. Then it gives <c>limit</c>.</summary>
    public int TailSeconds { get; init; } = 60;

    /// <summary>The largest body of an HTTP request, in megabytes. A larger body gets 413.</summary>
    public int RequestMegabytes { get; init; } = 4;

    /// <summary>The limits with each value in its allowed range: from 1 to the largest value that keeps the editor
    /// responsive and the result small enough for a model.</summary>
    public AgentLimits Clamped() => new()
    {
        ListRows = Math.Clamp(ListRows, 1, Math.Clamp(MaxListRows, 1, 1000)),
        MaxListRows = Math.Clamp(MaxListRows, 1, 1000),
        ResultKilobytes = Math.Clamp(ResultKilobytes, 16, 4096),
        CallsPerSecond = Math.Clamp(CallsPerSecond, 1, 100),
        WaitSeconds = Math.Clamp(WaitSeconds, 1, 600),
        TailSeconds = Math.Clamp(TailSeconds, 1, 3600),
        RequestMegabytes = Math.Clamp(RequestMegabytes, 1, 64),
    };
}

/// <summary>What the editor shows now: the open system, mission and cutscene, as item references.</summary>
public sealed record AgentView(string? System, string? Mission, string? Cutscene);

/// <summary>One entry of the Console of the editor, for the tool <c>get_console</c>.</summary>
/// <param name="Sequence">The number of the entry in the order of arrival. <c>since</c> takes it.</param>
/// <param name="Ref">The item reference of the target of the entry, or null.</param>
public sealed record AgentConsoleEntry(long Sequence, DateTime Time, string Severity, string Source, string Message,
    string? Ref, OpenLancer.Data.OriginKind Origin);

/// <summary>The filter of <see cref="AgentHost.Console"/>.</summary>
/// <param name="Severity">The lowest severity: <c>info</c>, <c>warning</c> or <c>error</c>.</param>
/// <param name="Since">Only entries after this sequence number, or null.</param>
public sealed record ConsoleFilter(string? Source, string Severity, bool BaseGame, long? Since, int Limit);

/// <summary>
/// The program that serves the tools: the editor or <c>oltool mcp serve</c> (section 5.4 of the MCP1 design). The tool
/// handlers read the session and the host state through it, so a handler runs the same way in both hosts. Each member
/// is read on the thread that owns the session.
/// </summary>
public abstract class AgentHost
{
    public abstract AgentHostKind Kind { get; }

    /// <summary>The session of the open profile, or null while no profile is open.</summary>
    public abstract EditSession? Session { get; }

    /// <summary>The text of a string id of the game data, or an empty text when the id is unknown. The names of the
    /// items come from it (see <see cref="ItemTexts"/>).</summary>
    public abstract string NameText(int ids);

    /// <summary>The plain text of an infocard id of the game data, or an empty text when the id is unknown.</summary>
    public abstract string InfoText(int ids);

    /// <summary>Why the host takes no edit now, or null: the reason of the session, and the rule of the mods in the
    /// OpenLancer repo (decision D738).</summary>
    public abstract string? NoEditReason { get; }

    /// <summary>The limits of the calls.</summary>
    public abstract AgentLimits Limits { get; }

    /// <summary>The groups that the user hides (decision D725).</summary>
    public virtual IReadOnlySet<ToolGroup> HiddenGroups => new HashSet<ToolGroup>();

    /// <summary>True while the user pauses the agent edits (decision D734).</summary>
    public virtual bool Paused => false;

    /// <summary>What the editor shows now, or null in a host without a window.</summary>
    public virtual AgentView? View => null;

    /// <summary>The entries of the Console, newest last, or null in a host without a Console.</summary>
    public virtual IReadOnlyList<AgentConsoleEntry>? Console(ConsoleFilter filter) => null;

    /// <summary>
    /// The target checks of <c>oltool validate --target</c>: all mods of the profile merged on the files of the target
    /// game, as the saved files give them. The headless host runs them in its process, and the editor host runs them
    /// in an oltool process as the tail of the call (section 7.1 of the MCP1 design).
    /// </summary>
    /// <param name="target">The id of a retail target, for example <c>vanilla-1.1</c>.</param>
    public abstract ToolStep CheckTarget(string target);

    /// <summary>The groups that the tool table has and the user does not hide, in table order.</summary>
    public IReadOnlyList<ToolGroup> ShownGroups =>
        AgentTools.All.Select(t => t.Group).Distinct().Where(g => !HiddenGroups.Contains(g)).ToList();
}
