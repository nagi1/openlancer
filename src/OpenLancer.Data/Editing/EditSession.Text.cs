using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    /// <summary>
    /// The sections of the active mod that hold each text placeholder in a value. A section of a file that the
    /// session opened comes from W, so it shows the edits. A null section is a line of a file of the active mod that
    /// the session did not open, or that the active mod replaces with a whole file.
    /// </summary>
    internal ILookup<StringPlaceholder, SectionRef?> TextUses()
    {
        var uses = new List<(StringPlaceholder Placeholder, SectionRef? Section)>();
        foreach (var document in documents.Values.Where(d => d.ReadOnlyReason == null))
        {
            for (var i = 0; i < document.Working.Count; i++)
            {
                var section = new SectionRef(document.Path, document.IdentityAt(i));
                uses.AddRange(document.Working[i].SelectMany(Placeholders).Select(p => (p, (SectionRef?)section)));
            }
        }
        if (ActiveMod != null)
        {
            var read = ResolvedLines(ActiveMod, path => documents.TryGetValue(path, out var d) && d.ReadOnlyReason == null);
            uses.AddRange(read.SelectMany(line => Placeholders(line.Entry)).Select(p => (p, (SectionRef?)null)));
        }
        return uses.ToLookup(u => u.Placeholder, u => u.Section);
    }

    /// <summary>
    /// True when a number is in the id range of the generated text DLL of the lower layer or of the edit view (rule
    /// S8). Such a number is the id of a text of a mod. The id comes from the position of the text in the DLL, so it
    /// changes when a mod adds text, and only a placeholder refers to the text for good.
    /// </summary>
    internal bool IsModTextId(int id) =>
        Local(lowerView.StringIds, id) != null || Local(EditView.StringIds, id) != null;

    /// <summary>The text of a mod that an id of the generated text DLL gives in the edit view, with its kind, or
    /// null.</summary>
    internal (StringKind Kind, string Text)? ModTextOf(int id) =>
        Lookup(lowerView.StringIds, id) ?? Lookup(EditView.StringIds, id);

    /// <summary>
    /// A test that stays true until an edit, an undo or a redo changes W of a file or the text. An edit that was made
    /// from the edit view uses it to refuse to run on a newer state, because it would undo the changes in between. A
    /// save keeps the test true, because W after a save has the same lines.
    /// </summary>
    internal Func<bool> Snapshot()
    {
        var states = documents.ToDictionary(d => d.Value, d => d.Value.State);
        var textNow = text;
        return () => ReferenceEquals(text, textNow) && documents.Values.All(d =>
            ReferenceEquals(d.State, states.TryGetValue(d, out var state) ? state : d.Initial));
    }

    /// <summary>The text placeholders in the values of a line, as the merge finds them.</summary>
    private static IEnumerable<StringPlaceholder> Placeholders(Entry entry)
    {
        foreach (var value in entry)
        {
            if (value is not (Int32Value or SingleValue or BooleanValue) &&
                StringPlaceholder.TryParse(value.ToString(), out var placeholder))
                yield return placeholder;
        }
    }

    /// <summary>
    /// The lines of the patches and the whole INI files of a mod on disk, in which the merge resolves each text
    /// placeholder: each line of a whole file, and each line of a patch except its directives. A file that cannot be
    /// read or parsed has no lines here, because the merge reports it.
    /// </summary>
    /// <param name="skipPatch">True for an install path whose patch the caller does not want.</param>
    private static IEnumerable<(string File, Section Section, Entry Entry)> ResolvedLines(Mod mod,
        Func<string, bool> skipPatch)
    {
        var files = mod.Patches.Where(p => !skipPatch(p.Key)).Select(p => (File: p.Value, Patch: true))
            .Concat(mod.Files.Where(f => f.Key.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                .Select(f => (File: f.Value, Patch: false)));
        foreach (var (file, isPatch) in files)
        {
            IReadOnlyList<Section> sections;
            try
            {
                sections = IniDocument.Parse(file, File.ReadAllBytes(file)).Sections;
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var section in sections)
            {
                foreach (var entry in section)
                {
                    if (!isPatch || !entry.Name.StartsWith(IniPatcher.DirectivePrefix, StringComparison.OrdinalIgnoreCase))
                        yield return (file, section, entry);
                }
            }
        }
    }

    /// <summary>The local id of an id in the range of the generated text DLL, or null.</summary>
    private static int? Local(StringIds ids, int id) =>
        id - ids.DllIndex * 65536 is var local and >= 1 and <= StringIds.MaxEntries ? local : null;

    private static (StringKind Kind, string Text)? Lookup(StringIds ids, int id) =>
        Local(ids, id) is not { } local ? null
        : ids.Strings.TryGetValue(local, out var text) ? (StringKind.String, text)
        : ids.Infocards.TryGetValue(local, out text) ? (StringKind.Infocard, text)
        : null;
}
