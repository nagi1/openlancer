using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Export;

namespace OpenLancer.Data.Publishing;

/// <summary>
/// An export with the publish history (decisions D565, D568 and D590 of the M11c design). It reads the history, runs
/// <see cref="ModExport.Prepare"/>, gives the same-version guard, writes the packages and records the export. The
/// Export dialog of the editor and <c>oltool export</c> run the same steps. The dialog asks the user between the guard
/// and the write.
/// </summary>
/// <remarks>The export does not prepare while the history does not load, because the guard needs the history. A
/// prepare that refuses the export, for example for an unsafe version, records nothing.</remarks>
public sealed class GuardedExport
{
    private const string Record = "export";

    private GuardedExport(HistoryLocation location, HistoryLoad history, PreparedExport? prepared, GuardResult guard,
        DateTime time)
    {
        Location = location;
        History = history;
        Prepared = prepared;
        Guard = guard;
        Time = time;
    }

    public HistoryLocation Location { get; }

    /// <summary>The read of the history before the prepare.</summary>
    public HistoryLoad History { get; }

    /// <summary>The prepared export, or null when the history does not load.</summary>
    public PreparedExport? Prepared { get; }

    /// <summary>The same-version guard of the prepared packages. It has no conflict when the export did not
    /// prepare or the prepare refused the export.</summary>
    public GuardResult Guard { get; }

    /// <summary>The time of the export in the history.</summary>
    public DateTime Time { get; }

    /// <summary>Why the export stops before its prepare, or null: the history does not load.</summary>
    public string? Refusal => History.GuardRefusal;

    /// <summary>True when the export goes into the history: it prepared, and the prepare did not refuse it.</summary>
    private bool Recorded => Prepared is { Refused: false };

    /// <summary>Reads the history of the location, and prepares the export when the history loads.</summary>
    /// <param name="time">The time of the export in the history.</param>
    public static GuardedExport Prepare(ExportRequest request, HistoryLocation location, DateTime time)
    {
        var history = PublishHistory.Load(location.Folder, location.ModId);
        var version = request.Mod.Manifest.Version;
        if (history.GuardRefusal != null)
            return new GuardedExport(location, history, null, GuardResult.None(version), time);
        var prepared = ModExport.Prepare(request);
        var guard = prepared.Refused ? GuardResult.None(version) : history.History.Conflict(prepared);
        return new GuardedExport(location, history, prepared, guard, time);
    }

    /// <summary>Records the checks of the prepare as the last check of each target. Run it when the export stops at
    /// the guard, so that the checklist still gets the checks.</summary>
    /// <returns>The warnings of the write of the history.</returns>
    public IReadOnlyList<string> RecordChecks()
    {
        if (!Recorded)
            return [];
        var prepared = Prepared!;
        return PublishHistory.Update(Location, Record, h => h.SetChecks(prepared.Checks, prepared.Content, Time)).Messages;
    }

    /// <summary>Writes the packages. The texts also list the valid packages of earlier exports of the version into the
    /// same folder (section 9.3 of the M11c design).</summary>
    /// <exception cref="InvalidOperationException">The export did not prepare.</exception>
    public ExportResult Write()
    {
        var prepared = Prepared ?? throw new InvalidOperationException(Refusal);
        return ModExport.Write(prepared,
            History.History.ValidPackages(prepared.Version, prepared.OutputFolder, prepared.Content));
    }

    /// <summary>Records the checks and the written export (decisions D568 and D590).</summary>
    /// <param name="replace">True after the answer "Replace version".</param>
    /// <returns>The record, or null when the export wrote no package or the history did not change. Also the warnings
    /// of the write of the history.</returns>
    public (RecordResult? Record, IReadOnlyList<string> Warnings) RecordExport(ExportResult result, bool replace)
    {
        if (!Recorded)
            return (null, []);
        RecordResult? recorded = null;
        var write = PublishHistory.Update(Location, Record, h =>
        {
            h.SetChecks(result.Checks, result.Content, Time);
            if (result.Packages.Any(p => p.Path != null))
                recorded = h.RecordExport(PublishHistory.EntryOf(result, Time), replace);
        });
        return (write.Written ? recorded : null, write.Messages);
    }
}
