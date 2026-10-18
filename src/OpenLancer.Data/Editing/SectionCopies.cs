using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The texts of a mod that the copies of sections get, and the commands that add them to the text of the active mod.
/// New from template (<see cref="ItemTemplates"/>) and Duplicate in the Scene (milestone M3) use it, so a copy of a
/// section gets its text in one way.
/// </summary>
/// <param name="problem">Gets why a line of a copy cannot get its text.</param>
internal sealed class SectionCopies(EditSession session, Action<string> problem)
{
    private readonly ILookup<StringPlaceholder, SectionRef?> uses = session.TextUses();

    /// <summary>The new placeholder of each copied text: <c>$str:key</c> of a placeholder, or <c>#id</c> of an id of
    /// a text of a mod.</summary>
    private readonly Dictionary<string, StringPlaceholder> copied = [];

    public List<EditCommand> Commands { get; } = [];

    /// <summary>
    /// Value i of a line of a copy. The first value of a text key that holds a text placeholder of the active
    /// mod, or the id of a text of a mod, becomes the placeholder of a copy of the text. Each other value stays.
    /// </summary>
    /// <param name="copy">The copy, for the key of the text.</param>
    public ValueBase Copy(Entry entry, int i, SectionIdentity copy)
    {
        var value = entry[i];
        if (i != 0 || SchemaCatalog.TextKind(entry.Section.Name, entry.Name) == null)
            return value;
        string source;
        StringKind kind;
        string? text;
        if (value is StringValue && StringPlaceholder.TryParse(value.ToString(), out var placeholder))
        {
            // The save reports a placeholder without text, so the copy keeps it.
            (source, kind, text) = (placeholder.ToString(), placeholder.Kind, session.Text(placeholder));
            if (string.IsNullOrEmpty(text))
                return value;
        }
        else if (value is Int32Value && value.TryToInt32(out var id) && session.IsModTextId(id))
        {
            if (session.ModTextOf(id) is not { Text.Length: > 0 } found)
            {
                problem($"the '{entry.Name}' line of [{entry.Section.Name}] holds {id}, which is in the id range of " +
                        "the text of the mods, but no mod has a text with this id.");
                return value;
            }
            (source, kind, text) = ("#" + id, found.Kind, found.Text);
        }
        else
            return value;

        if (!copied.TryGetValue(source, out var result))
        {
            string key;
            try
            {
                key = ItemEdits.NewTextKeys(copy, entry.Name).First(k => Free(new StringPlaceholder(kind, k)));
            }
            catch (ArgumentException e)
            {
                problem(e.Message);
                return value;
            }
            result = new StringPlaceholder(kind, key);
            copied[source] = result;
            Commands.Add(EditCommand.SetText(kind, key, text));
        }
        return new StringValue(result.ToString());
    }

    /// <summary>True when no section uses the key, the mod has no text with it, and no other copy has it.</summary>
    private bool Free(StringPlaceholder placeholder) =>
        !uses.Contains(placeholder) && session.Text(placeholder) == null && !copied.ContainsValue(placeholder);
}
