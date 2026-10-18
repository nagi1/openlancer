using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>One row of the Details table of a game folder check: what the check read, and what it found.</summary>
/// <param name="Check">The file or the rule, for example <c>EXE\Common.dll</c>. Empty when the line names no
/// file or rule before a colon.</param>
/// <param name="Result">What the check found. A SHA-256 value shows as its short form.</param>
/// <param name="Hash">The full SHA-256 value in the row, or null.</param>
internal sealed record DetailRow(string Check, string Result, string? Hash);

/// <summary>
/// What the first-run screen and the Game link window show of a report (sections 7.3 and 8 of the M11a design): the
/// notes, each one once, and the rows of the Details table. It needs no ImGui, so the tests can check it.
/// </summary>
internal static partial class GameDetails
{
    /// <summary>The number of notes above Details. Details shows the other notes.</summary>
    public const int MaxNotes = 5;

    // An evidence line starts with its file or rule, then ": ". A longer start, or a start with the end of a sentence,
    // is part of the result.
    private const int MaxCheckLength = 48;

    // The short form of a SHA-256 value keeps this number of digits at its start and at its end.
    private const int HashStartDigits = 8;
    private const int HashEndDigits = 4;

    /// <summary>
    /// The notes of a report: its findings, each one once. A folder without a game shows its error in the state line,
    /// so the notes do not show that error again. A missing folder shows "Not found" there, so its error stays a note.
    /// </summary>
    public static IReadOnlyList<GameFinding> Notes(GameReport report) =>
        report.Findings
            .Where(f => report.IsGame || report.FolderMissing || f.Text != report.Error)
            .Distinct()
            .ToList();

    /// <summary>Splits the notes into the notes above Details and the notes in Details, so that each note shows
    /// once.</summary>
    public static (IReadOnlyList<GameFinding> Shown, IReadOnlyList<GameFinding> More) Split(
        IReadOnlyList<GameFinding> notes, int max = MaxNotes) =>
        (notes.Take(max).ToList(), notes.Skip(max).ToList());

    /// <summary>The rows of the Details table: one row for each evidence line, then one row for each changed, missing
    /// or extra INI file.</summary>
    public static IReadOnlyList<DetailRow> Rows(GameReport report) =>
        report.Evidence.Select(EvidenceRow).Concat(report.DataFiles.Select(FileRow)).ToList();

    /// <summary>The row of an evidence line, for example "EXE\Common.dll: file version 1.0.1223.11.".</summary>
    internal static DetailRow EvidenceRow(string line)
    {
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        var labelled = colon > 0 && colon <= MaxCheckLength &&
                       !line.AsSpan(0, colon).Contains(". ", StringComparison.Ordinal);
        var (check, result) = labelled ? (line[..colon], line[(colon + 2)..]) : ("", line);
        var hash = Sha256().Match(result);
        return hash.Success
            ? new DetailRow(check, result.Replace(hash.Value, ShortHash(hash.Value), StringComparison.Ordinal),
                hash.Value)
            : new DetailRow(check, result, null);
    }

    /// <summary>The row of an INI file of the data check, for example "data/equipment/goods.ini (changed)".</summary>
    internal static DetailRow FileRow(string file)
    {
        var open = file.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && file.EndsWith(')')
            ? new DetailRow(file[..open], file[(open + 2)..^1], null)
            : new DetailRow(file, "", null);
    }

    /// <summary>The short form of a SHA-256 value: its first 8 and its last 4 digits, for example
    /// "5b4ee64c...faeb".</summary>
    public static string ShortHash(string hash) =>
        hash.Length <= HashStartDigits + TextFit.Ellipsis.Length + HashEndDigits
            ? hash
            : string.Concat(hash.AsSpan(0, HashStartDigits), TextFit.Ellipsis,
                hash.AsSpan(hash.Length - HashEndDigits));

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])")]
    private static partial Regex Sha256();
}
