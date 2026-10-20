using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Agent;

/// <summary>The result of <c>check</c>: the problems, their counts, and if the save gate stops a save.</summary>
/// <param name="Scope"><c>all</c>, <c>item</c>, <c>mission</c>, <c>cutscene</c> or <c>target</c>.</param>
/// <param name="SaveBlocked">True when an error of the result stops a save of the active mod.</param>
/// <param name="Note">A note about what the check covers, or null.</param>
internal sealed record CheckView(string Scope, int Errors, int Warnings, bool SaveBlocked, IReadOnlyList<Problem> Problems)
{
    public string? Note { get; init; }
}

/// <summary>
/// The tools <c>check</c> and <c>get_console</c> (section 7.1 of the MCP1 design). A data problem is a result, not a tool
/// error.
/// </summary>
internal static class CheckQueries
{
    public static readonly IReadOnlyList<string> Scopes = ["all", "item", "mission", "cutscene"];

    public static readonly IReadOnlyList<string> Severities = ["info", "warning", "error"];

    public static ToolStep Check(ToolCall call)
    {
        var session = call.Session;
        var args = call.Args;
        var baseGame = args.Flag("include_base_game");
        if (args.Text("target") is { } target)
        {
            if (args.Has("scope") && args.Text("scope") != "all" || args.Has("ref"))
                throw Bad("target", AgentTexts.Error("check_target_scope"));
            return call.Host.CheckTarget(target);
        }

        var scope = args.Text("scope")!;
        var reference = args.Text("ref");
        if (scope == "all" ? reference != null : reference == null)
            throw Bad("ref", AgentTexts.Error(scope == "all" ? "check_all_ref" : "check_needs_ref", ("scope", scope)));
        IReadOnlyList<Problem> problems = scope switch
        {
            "all" => session.SaveProblems().Select(d => AgentRows.ProblemOf(session, d)).ToList(),
            "item" => AgentRows.ProblemsOf(session, ItemRefs.ResolveSection(session, ItemRefs.Parse(reference!))),
            "mission" => session.Missions.Queries.GetProblems(ScriptRef.Parse(reference!), baseGame),
            _ => Cutscene(session, reference!),
        };
        return ToolResult.Of(Of(scope, problems.Where(p => AgentRows.Shows(p, baseGame)).ToList(),
            scope == "all" ? AgentTexts.Error("check_all_note") : null));
    }

    /// <summary>The result of a list of problems.</summary>
    internal static CheckView Of(string scope, IReadOnlyList<Problem> problems, string? note) => new(scope,
        problems.Count(p => p.Severity == DiagnosticSeverity.Error),
        problems.Count(p => p.Severity == DiagnosticSeverity.Warning),
        problems.Any(p => p.Severity == DiagnosticSeverity.Error && p.Origin != OriginKind.BaseGame),
        problems)
    {
        Note = note,
    };

    public static ToolStep Console(ToolCall call)
    {
        var args = call.Args;
        var limit = args.Whole("limit") ?? call.Limits.ListRows;
        if (limit > call.Limits.MaxListRows)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, "ListRows",
                AgentTexts.Error("limit_rows", ("max", call.Limits.MaxListRows))) { Field = "limit" });
        }
        var entries = call.Host.Console(new ConsoleFilter(args.Text("source"), args.Text("severity")!,
            args.Flag("include_base_game"), args.Whole("since"), (int)limit));
        return entries == null
            ? ToolResult.Failure(EditErrorKind.NotAvailable, "EditorOnly", AgentTexts.Error("editor_only", ("tool", "get_console")))
            : ToolResult.Of(new ConsoleView(entries));
    }

    private static IReadOnlyList<Problem> Cutscene(EditSession session, string reference)
    {
        var path = ItemRefs.ResolveCutscene(session, ItemRefs.Parse(reference));
        var cutscene = ItemRefs.Format(new ItemReference(ItemRefKind.Cutscene, "") { Path = path });
        return session.CutsceneErrors(path).Select(d => AgentRows.ProblemOf(session, d) with { Ref = cutscene }).ToList();
    }

    private static EditException Bad(string field, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, ToolInput.BadArgument, message) { Field = field });
}

/// <summary>The result of <c>get_console</c>: the entries, newest last. The <c>sequence</c> of the last entry is the
/// <c>since</c> of the next call.</summary>
internal sealed record ConsoleView(IReadOnlyList<AgentConsoleEntry> Entries);
