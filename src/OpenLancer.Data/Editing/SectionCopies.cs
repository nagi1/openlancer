using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>A text of a mod that a line value names: its kind, its text, and the value that names it, for example
/// <c>$str:li01_dock</c> or <c>#458754</c>.</summary>
internal sealed record CopiedText(StringKind Kind, string Text, string Source);

/// <summary>
/// The texts of a mod that the copies of sections get, and the commands that add them to the text of the active mod.
/// New from template (<see cref="ItemTemplates"/>), Duplicate in the Scene (milestone M3), and the draft and the
/// placement of a prop group (milestone M23) use it, so a copy of a section gets its text in one way.
/// </summary>
/// <param name="problem">Gets why a line of a copy cannot get its text.</param>
internal sealed class SectionCopies(EditSession session, Action<string> problem)
{
    private readonly ILookup<StringPlaceholder, SectionRef?> uses = session.TextUses();

    /// <summary>The new placeholder of each copied text, by the value that named the text: <c>$str:key</c> of a
    /// placeholder, <c>#id</c> of an id of a text of a mod, or a text of a prop group.</summary>
    private readonly Dictionary<string, StringPlaceholder> copied = [];

    public List<EditCommand> Commands { get; } = [];

    /// <summary>
    /// Value i of a line of a copy. The first value of a text key that holds a text placeholder of the active
    /// mod, or the id of a text of a mod, becomes the placeholder of a copy of the text. Each other value stays.
    /// </summary>
    /// <param name="copy">The copy, for the key of the text.</param>
    public ValueBase Copy(Entry entry, int i, SectionIdentity copy) => Copy(entry.Section.Name, entry.Name, i, entry[i], copy);

    /// <summary>Value i of a line of a key of a section type (see <see cref="Copy(Entry, int, SectionIdentity)"/>).</summary>
    public ValueBase Copy(string type, string name, int i, ValueBase value, SectionIdentity copy) =>
        TextOf(session, type, name, i, value, problem) is { } text ? Add(text, copy, name) ?? value : value;

    /// <summary>
    /// The text of a mod that value i of a line names: a text placeholder of the active mod, or the id of a text of a mod.
    /// Null for another value, for the id of a text of the base data, and for a placeholder without text, which the save
    /// reports.
    /// </summary>
    /// <param name="problem">Gets why the value names no text, for an id of a mod without a text.</param>
    public static CopiedText? TextOf(EditSession session, string type, string name, int i, ValueBase value,
        Action<string> problem)
    {
        if (i != 0 || SchemaCatalog.TextKind(type, name) == null)
            return null;
        if (value is StringValue && StringPlaceholder.TryParse(value.ToString(), out var placeholder))
            return session.Text(placeholder) is { Length: > 0 } text ? new CopiedText(placeholder.Kind, text, placeholder.ToString())
                : null;
        if (value is not Int32Value || !value.TryToInt32(out var id) || !session.IsModTextId(id))
            return null;
        if (session.ModTextOf(id) is not { Text.Length: > 0 } found)
        {
            problem($"the '{name}' line of [{type}] holds {id}, which is in the id range of the text of the mods, but no " +
                    "mod has a text with this id.");
            return null;
        }
        return new CopiedText(found.Kind, found.Text, "#" + id);
    }

    /// <summary>
    /// Adds a copy of a text to the text of the active mod, with a key that the copy gives and that no section, text or
    /// other copy has. A text that this object copied before keeps its first copy. Null when the copy gives no valid
    /// key: the problem then tells why.
    /// </summary>
    /// <param name="copy">The copy, for the key of the text.</param>
    /// <param name="name">The key of the line, for example <c>ids_name</c>.</param>
    public ValueBase? Add(CopiedText text, SectionIdentity copy, string name)
    {
        if (!copied.TryGetValue(text.Source, out var result))
        {
            string key;
            try
            {
                key = ItemEdits.NewTextKeys(copy, name).First(k => Free(new StringPlaceholder(text.Kind, k)));
            }
            catch (ArgumentException e)
            {
                problem(e.Message);
                return null;
            }
            result = new StringPlaceholder(text.Kind, key);
            copied[text.Source] = result;
            Commands.Add(EditCommand.SetText(text.Kind, key, text.Text));
        }
        return new StringValue(result.ToString());
    }

    /// <summary>True when no section uses the key, the mod has no text with it, and no other copy has it.</summary>
    private bool Free(StringPlaceholder placeholder) =>
        !uses.Contains(placeholder) && session.Text(placeholder) == null && !copied.ContainsValue(placeholder);
}
