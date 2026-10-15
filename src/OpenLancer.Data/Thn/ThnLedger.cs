using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Thn;

/// <summary>
/// Records, in load order, which mod changes which element and path of each cutscene, which mod sets its duration, and
/// which mod changes the elements of a key. The merge uses it to report two conflicts between mods (see
/// <see cref="ThnPatch"/>):
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Rule 4: two mods change the same path of an element, or a path in the value of the other, or two mods set the
/// duration. The later mod wins.</item>
/// <item>Rule 7: a lower mod deleted or added an element with a key, or changed the key of an element, and a later
/// entry names an element of that key by its place (<c>ol_nth</c>). The place can then name another element, also when
/// the number of elements stays the same. The patch still applies.</item>
/// </list>
/// <para>A whole-file override discards the earlier changes of the file, as in <c>PatchLedger</c>. Elements compare
/// by their <see cref="ThnElementId"/> in the merge of one file.</para>
/// </remarks>
internal sealed class ThnLedger
{
    private readonly Dictionary<string, FileLog> files = new(VfsPath.Comparer);

    /// <summary>Records that a mod replaces the whole file, which discards the earlier changes.</summary>
    public void RecordFile(string path) => files[path] = new FileLog();

    /// <summary>
    /// Records the changes of a patch that applied, and returns the warnings of rules 4 and 7 that they give.
    /// </summary>
    /// <param name="applied">The result of <see cref="ThnPatch.Apply"/>, with a model.</param>
    public IReadOnlyList<string> Record(string path, string modId, ThnPatchResult applied)
    {
        if (!files.TryGetValue(path, out var log))
            files[path] = log = new FileLog();
        var warnings = new List<string>();
        foreach (var place in applied.Places)
        {
            var others = log.Groups
                .Where(g => g.Change == new ThnGroupChange(place.Kind, place.Signature) && g.ModId != modId)
                .Select(g => g.ModId).Distinct().ToList();
            if (others.Count > 0)
                warnings.Add($"{Mods(others)} changed the {ThnPatch.Plural(place.Kind)} with the key {place.Key}. " +
                             $"The {place.Entry} of mod '{modId}' uses place {place.Nth} of {place.Count}. Check the result.");
        }
        if (applied.SetsDuration && log.DurationMod is { } durationMod && durationMod != modId)
            warnings.Add($"'{ThnScriptModel.DurationGlobal}' was also changed by mod '{durationMod}'. {LaterWins(modId)}");
        foreach (var change in applied.Changes)
        {
            if (change.Path is not { } changed)
                continue;
            var previous = log.Paths.FindLast(p =>
                p.ModId != modId && p.Kind == change.Kind && p.Element == change.Element && Overlap(p.Path, changed));
            if (previous.ModId == null)
                continue;
            var what = previous.Path == changed
                ? $"'{changed}' was also changed by mod '{previous.ModId}'."
                : $"'{changed}' overlaps '{previous.Path}', which mod '{previous.ModId}' also changed.";
            warnings.Add($"{change.Label}: {what} {LaterWins(modId)}");
        }

        foreach (var change in applied.Changes)
        {
            if (change.Path is { } changed)
                log.Paths.Add((modId, change.Kind, change.Element, changed));
        }
        log.Groups.AddRange(applied.Groups.Select(g => (modId, g)));
        if (applied.SetsDuration)
            log.DurationMod = modId;
        return warnings;
    }

    /// <summary>True when the paths are equal, or one of them is in the value of the other.</summary>
    private static bool Overlap(ThnPath a, ThnPath b) => a.IsWithin(b) || b.IsWithin(a);

    private static string LaterWins(string modId) => $"Mod '{modId}' loads later and wins.";

    private static string Mods(IReadOnlyList<string> ids) =>
        (ids.Count == 1 ? "Mod " : "Mods ") + string.Join(", ", ids.Select(id => $"'{id}'"));

    /// <summary>The changes of one file after its last whole-file override, in load order.</summary>
    private sealed class FileLog
    {
        public List<(string ModId, ThnElementKind Kind, ThnElementId Element, ThnPath Path)> Paths { get; } = [];

        public List<(string ModId, ThnGroupChange Change)> Groups { get; } = [];

        /// <summary>The last mod that set the duration, or null.</summary>
        public string? DurationMod { get; set; }
    }
}
