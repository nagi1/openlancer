using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Editing.Fields;

/// <summary>The state of one step of a completeness panel (section 11.4 of the M6a design).</summary>
public enum CompletenessState
{
    Done,

    Missing,

    /// <summary>The step is missing, and the base game without mods misses it too (decision D551). It counts as done,
    /// and the panel hides it while the base-game messages are hidden.</summary>
    Hidden,
}

/// <summary>One step of a multi-part item, for example "Camera back" of a space cutscene (AGENTS.md UI rule 12).</summary>
/// <param name="Name">The name of the step, for example "Camera back".</param>
/// <param name="Reason">Why the step is necessary, in one sentence.</param>
/// <param name="Detail">What is missing, for example the name of a trigger, or null.</param>
/// <param name="GoTo">The item that "Go to" selects, or null.</param>
/// <param name="Fix">The id of a fix command, or null. M6a has no fixes (decision D538).</param>
/// <param name="IsError">True when a missing step is an error of the checks. The panel then shows the error glyph and opens
/// the list of missing steps.</param>
public sealed record CompletenessStep(string Name, CompletenessState State, string Reason, string? Detail = null,
    object? GoTo = null, string? Fix = null, bool IsError = false);

/// <summary>
/// The completeness of a multi-part item (AGENTS.md UI rule 12, section 11.4 of the M6a design): its steps and their
/// states. Providers in the data layer make the reports, and the completeness panel draws them.
/// </summary>
/// <param name="Title">The item, for example "Spawn wave".</param>
public sealed record CompletenessReport(string Title, IReadOnlyList<CompletenessStep> Steps)
{
    public int Total => Steps.Count;

    /// <summary>The steps that count as done: the done steps and the hidden steps.</summary>
    public int Done => Steps.Count(s => s.State != CompletenessState.Missing);

    /// <summary>The missing steps that show.</summary>
    public IEnumerable<CompletenessStep> Missing => Steps.Where(s => s.State == CompletenessState.Missing);

    /// <summary>The number of hidden base-game steps.</summary>
    public int HiddenCount => Steps.Count(s => s.State == CompletenessState.Hidden);

    /// <summary>True when each step counts as done.</summary>
    public bool Complete => Done == Total;

    /// <summary>True when a missing step is an error. The list of missing steps then starts open.</summary>
    public bool HasError => Missing.Any(s => s.IsError);

    /// <summary>The summary line: "{done} of {total} steps complete", or "All {total} steps complete" when no step is
    /// missing. One step uses the singular form.</summary>
    public string Summary => Complete
        ? Total == 1 ? "The step is complete." : $"All {Total} steps complete."
        : $"{Done} of {Total} {(Total == 1 ? "step" : "steps")} complete";

    /// <summary>The number of base-game messages of the item. While the base-game messages are hidden, the panel hides
    /// them, and its hidden steps count as done.</summary>
    public int HiddenMessages { get; init; }

    /// <summary>The dim line of the hidden base-game messages of the item, or null when it has none (section 11.4 of the
    /// M6a design). The Problems section of a page uses the same line, so both say the same number.</summary>
    public string? HiddenLine => BaseGameMessages.HiddenLine(HiddenMessages);
}
