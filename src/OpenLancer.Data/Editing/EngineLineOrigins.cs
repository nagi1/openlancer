using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Editing;

/// <summary>The origin of an engine line, and the section that it is about, or null.</summary>
public readonly record struct ResolvedLine(MessageOrigin Origin, SectionRef? Target);

/// <summary>
/// Gives each engine line of a load its origin, by a comparison with the base run: the count of each line that the base
/// folder gives without mods (sections 3.3 to 3.5 of the console filter design). A line is a base-game line only when
/// the base game without mods gives it as often. The section of a line only names the mod and gives the line its
/// section (decision D501).
/// </summary>
/// <remarks>
/// <para>The resolver holds the state of the base run of one profile open: not needed, waiting, known or failed. The
/// edit view of a profile without a mod file is the base folder, so it needs no base run (decision D516). Each line of
/// such a profile is a base-game line at once. The first save of a mod gives the edit view a mod file. From then on,
/// the lines wait for the base run.</para>
/// <para>The resolver parses a merged file only when it needs a section: for a key that the load gives more often
/// than the base run, and for a line of a mod.</para>
/// </remarks>
public sealed class EngineLineOrigins(EditSession session)
{
    private static readonly ResolvedLine InBase = new(MessageOrigin.BaseGame, null);
    private static readonly ResolvedLine NotCompared = new(MessageOrigin.Unknown, null);

    /// <summary>True when the lines wait for the base run: the edit view of the session has a mod file now. A save can
    /// change the value.</summary>
    public bool NeedsBaseRun => session.EditView.Files.Count > 0;

    /// <summary>The count of each key (<see cref="EngineLine.Key"/>) that the base run gave, or null before it
    /// ends.</summary>
    public IReadOnlyDictionary<string, int>? BaseCounts { get; private set; }

    /// <summary>Why the base run failed, or null.</summary>
    public string? Failure { get; private set; }

    /// <summary>True when a line can get its origin now: the base run ended, failed, or is not needed.</summary>
    public bool CanResolve => !NeedsBaseRun || BaseCounts != null || Failure != null;

    /// <summary>Takes the result of the base run.</summary>
    public void BaseRunEnded(IReadOnlyDictionary<string, int> counts) => BaseCounts = counts;

    /// <summary>Takes the failure of the base run. The lines that wait then get the origin unknown (decision
    /// D517).</summary>
    public void BaseRunFailed(string reason) => Failure = reason;

    /// <summary>
    /// The origins of the lines of a load that ended, in the order of the lines (section 3.4): for each key, the lines in
    /// sections that no mod changes come first, then the lines without a known section, then the lines in changed
    /// sections, each group in arrival order. Of these, as many lines as the base run gives are base-game lines. The
    /// other lines get the mod of the section rule.
    /// </summary>
    /// <remarks>Call it only when <see cref="CanResolve"/> is true.</remarks>
    public IReadOnlyList<ResolvedLine> Resolve(IReadOnlyList<EngineLine> lines)
    {
        var result = new ResolvedLine[lines.Count];
        foreach (var group in Enumerable.Range(0, lines.Count).GroupBy(i => lines[i].Key))
        {
            var indexes = group.ToList();
            var known = Known(group.Key);
            if (known is not { } count || indexes.Count <= count)
            {
                foreach (var i in indexes)
                    result[i] = known == null ? NotCompared : InBase;
                continue;
            }

            var ordered = indexes.Select(i => (Index: i, Section: SectionOf(lines[i])))
                .OrderBy(p => p.Section == null ? 1 : session.ModChanges(p.Section) ? 2 : 0)
                .ToList();
            for (var n = 0; n < ordered.Count; n++)
            {
                var (i, section) = ordered[n];
                result[i] = n < count ? InBase : new ResolvedLine(session.ModOfLine(section, lines[i].File), section);
            }
        }
        return result;
    }

    /// <summary>
    /// The origin of a line of a Play run, which resolves when it arrives: the n-th line of a key in the run is a
    /// base-game line when the base run gives the key n times or more. The count is right, but the sort of
    /// <see cref="Resolve"/> is not possible while the game runs.
    /// </summary>
    /// <param name="occurrence">The number of the line among the lines of its key in the run, from 1.</param>
    /// <remarks>Call it only when <see cref="CanResolve"/> is true.</remarks>
    public ResolvedLine ResolveRunning(EngineLine line, int occurrence)
    {
        var known = Known(line.Key);
        if (known == null)
            return NotCompared;
        if (occurrence <= known)
            return InBase;
        var section = SectionOf(line);
        return new ResolvedLine(session.ModOfLine(section, line.File), section);
    }

    /// <summary>The count of a key in the base run: the count, 0 when the base run does not give it, or null when no
    /// comparison is possible. A profile without a mod file gives each line as often as its base run.</summary>
    private int? Known(string key) =>
        !NeedsBaseRun ? int.MaxValue
        : BaseCounts is { } counts ? counts.GetValueOrDefault(key)
        : null;

    /// <summary>The section of the edit view that a line is about: the section that holds its line in its file, or the
    /// section of the item that a line of a component names. Null when the line names no section that the edit view
    /// has.</summary>
    private SectionRef? SectionOf(EngineLine line)
    {
        if (line is { File: { } file, Line: { } number })
            return session.SectionAtLine(file, number);
        return EngineLineRules.Subject(line) is var (kind, nickname) ? session.SubjectSection(kind, nickname) : null;
    }
}
