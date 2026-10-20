using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>What a request did (section 4.0.6 of the M6b design).</summary>
public enum EditStatus
{
    /// <summary>The request changed data, and the history has one new entry.</summary>
    Applied,

    /// <summary>The request ran, but no state changed. The history has no new entry.</summary>
    NoChange,

    /// <summary>A preview: the request ran and gave its result, then the states before came back.</summary>
    Previewed,

    /// <summary>The request did not run. <see cref="EditResult.Errors"/> tells why. Nothing changed.</summary>
    Refused,
}

/// <summary>An item that a request removes.</summary>
/// <param name="Ref">The item reference.</param>
/// <param name="Why">A target of the request, or an item that goes with a target.</param>
/// <param name="Cause">For an item that goes with a target: the line that names the target. Null for a target.</param>
public sealed record ResultDelete(string Ref, DeleteReason Why, string? Cause);

/// <summary>An item that a request gives a new name, with the old and the new reference.</summary>
public sealed record ResultRename(string From, string To);

/// <summary>A safe fix of a delete (decision D81): the lines that go with the item that goes.</summary>
/// <param name="Ref">The reference of the section of the lines.</param>
/// <param name="Key">The key of the first line.</param>
/// <param name="Text">What the fix does.</param>
public sealed record ResultFix(string Ref, string Key, string Text);

/// <summary>A line that shows "Missing" after a request.</summary>
/// <param name="Ref">The reference of the section of the line.</param>
/// <param name="Key">The key of the line.</param>
/// <param name="Consequence">What the game does with the line.</param>
/// <param name="BlocksSave">True when the line stops the save until a fix.</param>
public sealed record ResultMissing(string Ref, string Key, string Consequence, bool BlocksSave);

/// <summary>
/// A problem that a check finds (section 4.0.6 of the M6b design). It is data, not an error. Its message is the
/// message of the check, so the agent and the user read the same text.
/// </summary>
/// <param name="Severity">An error or a warning.</param>
/// <param name="Message">The message of the check.</param>
public sealed record Problem(DiagnosticSeverity Severity, string Message)
{
    /// <summary>The reference of the item or the line, or null.</summary>
    public string? Ref { get; init; }

    /// <summary>The id of the rule, for example <c>M05</c>, or null.</summary>
    public string? Rule { get; init; }

    /// <summary>Who causes the problem (decision D551 of the M6a design).</summary>
    public OriginKind Origin { get; init; } = OriginKind.Unknown;

    /// <summary>The mod of the origin <see cref="OriginKind.Mod"/>, else null.</summary>
    public string? ModId { get; init; }

    /// <summary>The ids of the fixes of the problem.</summary>
    public IReadOnlyList<string> Fixes { get; init; } = [];

    /// <summary>The problem of a diagnostic of a check. An origin that waits for the comparison with the base game
    /// (<see cref="OriginKind.Pending"/>) is not known yet, so the problem gives <see cref="OriginKind.Unknown"/>.
    /// Section 4.0.6 of the M6b design has no pending origin.</summary>
    /// <param name="reference">The reference of the item or the line, or null.</param>
    public static Problem From(Diagnostic diagnostic, string? reference) => new(diagnostic.Severity, diagnostic.Message)
    {
        Ref = reference,
        Rule = diagnostic.Rule,
        Origin = diagnostic.Origin?.Kind is { } kind and not OriginKind.Pending ? kind : OriginKind.Unknown,
        ModId = diagnostic.Origin?.ModId,
    };
}

/// <summary>
/// The one result of a command or a batch of the command layer (section 4.0.6 of the M6b design). Items are item
/// references (see <see cref="ItemRefs"/>), so a caller can pass a result into its next request. Its JSON form uses
/// snake case (see <see cref="EditJson"/>).
/// </summary>
public record EditResult(EditStatus Status)
{
    /// <summary>The errors of a refusal.</summary>
    public IReadOnlyList<EditError> Errors { get; init; } = [];

    /// <summary>The label of the request.</summary>
    public string? Label { get; init; }

    /// <summary>The label of the history entry, or null when the request added no entry.</summary>
    public string? UndoLabel { get; init; }

    /// <summary>The revision of the session after the request (see <see cref="EditSession.Revision"/>). JSON writes it
    /// as text.</summary>
    [JsonPropertyName("rev")]
    [JsonConverter(typeof(EditJson.LongAsText))]
    public long Revision { get; init; }

    /// <summary>The versions of the touched mission scripts, by install path.</summary>
    public IReadOnlyDictionary<string, long> Versions { get; init; } = new Dictionary<string, long>();

    /// <summary>The install paths of the touched files.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The references of the new items.</summary>
    public IReadOnlyList<string> Adds { get; init; } = [];

    /// <summary>The references of the changed items.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>The removed items.</summary>
    public IReadOnlyList<ResultDelete> Deletes { get; init; } = [];

    /// <summary>The renamed items.</summary>
    public IReadOnlyList<ResultRename> Renames { get; init; } = [];

    /// <summary>The safe fixes of a delete.</summary>
    public IReadOnlyList<ResultFix> Fixes { get; init; } = [];

    /// <summary>The lines that show "Missing" after the request.</summary>
    public IReadOnlyList<ResultMissing> Missing { get; init; } = [];

    /// <summary>The warnings of the plan.</summary>
    public IReadOnlyList<Problem> Warnings { get; init; } = [];

    /// <summary>The parts of the plan that refused while the rest ran, for example one item of a delete.</summary>
    public IReadOnlyList<Problem> Refusals { get; init; } = [];

    /// <summary>The files that name a renamed or deleted item as text.</summary>
    public IReadOnlyList<NamedMention> Mentions { get; init; } = [];

    /// <summary>The problems that the request adds.</summary>
    public IReadOnlyList<Problem> NewProblems { get; init; } = [];

    /// <summary>The problems that the request removes.</summary>
    public IReadOnlyList<Problem> FixedProblems { get; init; } = [];

    /// <summary>True when the request needs <c>confirm: true</c>.</summary>
    public bool NeedsConfirmation { get; init; }

    /// <summary>The item that the editor selects after the request, or null.</summary>
    public string? Select { get; init; }

    /// <summary>Notes, for example a word whose letter case changed.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The patch text of each changed file, by install path. Only a preview that asks for it has it.</summary>
    public IReadOnlyDictionary<string, string> Patch { get; init; } = new Dictionary<string, string>();

    /// <summary>One sentence that tells what the request did, or null.</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// The result of the parts of a batch with a status: the lists of the parts in order. The confirmation is needed
    /// when a part needs it. The selection and the summary are the last that a part gives. A later version of a
    /// script replaces an earlier one.
    /// </summary>
    public static EditResult Merge(EditStatus status, IReadOnlyList<EditResult> parts) => new(status)
    {
        Errors = parts.SelectMany(p => p.Errors).ToList(),
        Versions = parts.SelectMany(p => p.Versions).GroupBy(v => v.Key, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, System.StringComparer.OrdinalIgnoreCase),
        Adds = parts.SelectMany(p => p.Adds).Distinct().ToList(),
        Changes = parts.SelectMany(p => p.Changes).Distinct().ToList(),
        Deletes = parts.SelectMany(p => p.Deletes).ToList(),
        Renames = parts.SelectMany(p => p.Renames).ToList(),
        Fixes = parts.SelectMany(p => p.Fixes).ToList(),
        Missing = parts.SelectMany(p => p.Missing).ToList(),
        Warnings = parts.SelectMany(p => p.Warnings).ToList(),
        Refusals = parts.SelectMany(p => p.Refusals).ToList(),
        Mentions = parts.SelectMany(p => p.Mentions).ToList(),
        NewProblems = parts.SelectMany(p => p.NewProblems).ToList(),
        FixedProblems = parts.SelectMany(p => p.FixedProblems).ToList(),
        NeedsConfirmation = parts.Any(p => p.NeedsConfirmation),
        Select = parts.Select(p => p.Select).LastOrDefault(s => s != null),
        Notes = parts.SelectMany(p => p.Notes).ToList(),
        Patch = parts.SelectMany(p => p.Patch).GroupBy(p => p.Key, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, System.StringComparer.OrdinalIgnoreCase),
        Summary = parts.Select(p => p.Summary).LastOrDefault(s => s != null),
    };
}
