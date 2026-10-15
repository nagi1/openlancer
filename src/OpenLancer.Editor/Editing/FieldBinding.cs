using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// One key of one section of the edit view, as a bound Inspector row shows it: its lines, the layer that sets them,
/// the later mods that change them, and the edits of the row.
/// </summary>
internal sealed class FieldBinding
{
    /// <summary>The key of a <c>[system]</c> of universe.ini that names the file of the system.</summary>
    private const string SystemFileKey = "file";

    private const string SystemFileReason =
        "Changing the file of a system moves all its contents. A later milestone does this.";

    private FieldBinding(SectionRef section, string name, KeySchema? key, IReadOnlyList<EntryView> lines,
        ValueOrigin origin, string? originMod, IReadOnlyList<string> laterMods, string? readOnlyReason, bool canRevert)
    {
        Section = section;
        Name = name;
        Key = key;
        Lines = lines;
        Origin = origin;
        OriginMod = originMod;
        LaterMods = laterMods;
        ReadOnlyReason = readOnlyReason;
        CanRevert = canRevert;
        Text = FieldEdit.Format(lines);
    }

    public SectionRef Section { get; }

    /// <summary>The INI key, for example <c>hit_pts</c>.</summary>
    public string Name { get; }

    /// <summary>The key of a field that edits all lines of the key.</summary>
    public FieldKey FieldKey => new(Section, Name, FieldKey.AllLines);

    /// <summary>What the engine reads from the key, or null when the schema does not have the key.</summary>
    public KeySchema? Key { get; }

    /// <summary>The lines of the key in the edit view, in file order.</summary>
    public IReadOnlyList<EntryView> Lines { get; }

    /// <summary>The text of the field (see <see cref="FieldEdit.Format"/>).</summary>
    public string Text { get; }

    public ValueOrigin Origin { get; }

    /// <summary>The mod that sets the lines, or null for the base data.</summary>
    public string? OriginMod { get; }

    /// <summary>The mods that load after the active mod and change the key. The game uses their value.</summary>
    public IReadOnlyList<string> LaterMods { get; }

    /// <summary>Why the row cannot change the key, or null.</summary>
    public string? ReadOnlyReason { get; }

    /// <summary>True when the active mod changes the key and the lower layer has the section, so a revert puts back
    /// the lower lines.</summary>
    public bool CanRevert { get; }

    /// <summary>One line that says which layer sets the key.</summary>
    public string OriginText => Origin switch
    {
        ValueOrigin.ActiveMod => $"The active mod '{OriginMod}' sets this value.",
        ValueOrigin.LowerMod => $"Mod '{OriginMod}' sets this value. It loads before the active mod.",
        _ => Lines.Count == 0 ? "No layer sets this value. The engine uses its default." : "The base data sets this value.",
    };

    /// <summary>The binding of a key of a section of the edit view.</summary>
    /// <param name="view">The section, as <see cref="EditSession.Get"/> gives it.</param>
    public static FieldBinding Create(EditSession session, SectionRef section, SectionView view, string name)
    {
        var key = session.Schema(section, name);
        var (origin, mod) = session.Origin(section, name);
        var reason = view.ReadOnlyReason ?? KeyLock(session, section, view, key, name);
        return new FieldBinding(section, key?.Name ?? name, key, view.Lines(name), origin, mod,
            session.LaterModsThatChange(section, name), reason,
            reason == null && origin == ValueOrigin.ActiveMod && !view.IsNew);
    }

    /// <summary>
    /// Why a row cannot change one key of a section that takes edits, or null. A row cannot change the key entry that a
    /// patch finds the section by, a key of a group of lines that belong together, or the file of a system.
    /// </summary>
    private static string? KeyLock(EditSession session, SectionRef section, SectionView view, KeySchema? key, string name)
    {
        if (key?.IsKeyEntry == true)
            return $"'{name}' is the key that finds the section. The editor does not change it in format version 1.";
        // For example, a change of the encounter lines of a zone gives its faction lines to another encounter.
        if (view.IsLinked(name))
            return "This key belongs to a group of lines that belong together. A later milestone edits the group.";
        return name.Equals(SystemFileKey, StringComparison.OrdinalIgnoreCase) && IsUniverseSystem(session, section)
            ? SystemFileReason
            : null;
    }

    /// <summary>
    /// Why a revert of a whole section cannot run, or null. A revert puts back all the lines of the lower layer, also
    /// the lines of the keys that no row can change. Of those keys, only the file of a system moves contents. So a
    /// revert cannot run when the active mod sets the file of a system. A revert of the key entry keeps the section,
    /// and a revert of a group of lines that belong together puts back the whole group.
    /// </summary>
    public static string? RevertLock(EditSession session, SectionRef section) =>
        IsUniverseSystem(session, section) && session.Get(section) != null &&
        session.Origin(section, SystemFileKey).Origin == ValueOrigin.ActiveMod
            ? SystemFileReason
            : null;

    /// <summary>True for a <c>[system]</c> section of universe.ini.</summary>
    private static bool IsUniverseSystem(EditSession session, SectionRef section) =>
        session.UniversePath is { } universe && VfsPath.Comparer.Equals(section.Path, universe) &&
        section.Id.Type.Equals("system", StringComparison.OrdinalIgnoreCase);

    /// <summary>The edit that sets the key from the text of a field. Empty text removes the key.</summary>
    /// <exception cref="ArgumentException">The text is not valid for the key.</exception>
    public EditCommand Edit(string text)
    {
        var parse = FieldEdit.Parse(Key, text);
        if (parse.Lines == null)
            throw new ArgumentException(parse.Error);
        return EditLines(parse.Lines);
    }

    /// <summary>The edit that sets the lines of the key from their value texts.</summary>
    public EditCommand EditLines(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? EditCommand.RemoveName(Section, Name) : EditCommand.SetLinesFromText(Section, Name, lines);

    /// <summary>True when the value texts are the lines that the key has now.</summary>
    public bool Holds(IReadOnlyList<string> lines) =>
        lines.SequenceEqual(Lines.Select(l => FieldEdit.FormatLine(l.Values)), StringComparer.Ordinal);

    public EditCommand Revert() => EditCommand.Revert(Section, Name);
}
