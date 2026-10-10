using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>
/// An edit of the edit view, or of the text of the active mod. <see cref="EditSession.Execute"/> runs a command as one
/// entry of the edit history, and an <see cref="EditTransaction"/> runs its commands as one entry.
/// </summary>
/// <remarks>
/// <para>A command never changes a section or an entry in place. It builds new sections and puts them into W, so the
/// history can put the old ones back.</para>
/// <para>A command that cannot run throws, and changes nothing: an <see cref="ArgumentException"/> when an argument
/// is not valid, and an <see cref="InvalidOperationException"/> when the edit view does not allow the edit.</para>
/// </remarks>
public abstract class EditCommand
{
    private protected EditCommand(string label) => Label = label;

    /// <summary>The name of the edit, for the Undo and Redo menu items.</summary>
    internal string Label { get; }

    /// <summary>
    /// Sets the lines of one name. The new lines take the place of the first line of the name, or go at the end of the
    /// section, as the merge puts the lines of a patch. An empty list removes the name.
    /// </summary>
    /// <param name="lines">The values of each new line.</param>
    public static EditCommand SetLines(SectionRef target, string name, IReadOnlyList<IReadOnlyList<ValueBase>> lines)
    {
        CheckName(name);
        return new SetLinesCommand($"Set {name}", target, name, lines);
    }

    /// <summary>
    /// Sets the lines of one name from the text of their values, for example <c>1, 2.5, li_gun01</c> for one line.
    /// Each line gets the values that a patch line with that text gives through the merge (see
    /// <c>docs/MOD_FORMAT.md</c>, "Value types in binary files"). So the edit view shows the values that a save and a
    /// reload give. The lines go where <see cref="SetLines"/> puts them.
    /// </summary>
    /// <remarks>A line text that a patch line cannot hold makes the command throw an <see cref="ArgumentException"/>
    /// when it runs.</remarks>
    /// <param name="lines">The value text of each new line. An empty list removes the name. An empty text gives a
    /// line without values, for example <c>separable =</c>.</param>
    public static EditCommand SetLinesFromText(SectionRef target, string name, IReadOnlyList<string> lines)
    {
        CheckName(name);
        return new SetLineTextsCommand($"Set {name}", target, name, lines);
    }

    /// <summary>Removes all lines of one name.</summary>
    public static EditCommand RemoveName(SectionRef target, string name)
    {
        CheckName(name);
        return new SetLinesCommand($"Remove {name}", target, name, []);
    }

    /// <summary>
    /// Puts back the lines of one name, or of the whole section when <paramref name="name"/> is null, as the lower
    /// layer has them: the base data with the mods that load before the active mod. A save then writes no line for
    /// them. A section that is as the lower layer has it does not change.
    /// </summary>
    /// <remarks>The command throws an <see cref="InvalidOperationException"/> when it runs on a section that the
    /// lower layer does not have, because the active mod adds it.</remarks>
    public static EditCommand Revert(SectionRef target, string? name = null)
    {
        if (name != null)
            CheckName(name);
        return new RevertCommand(name == null ? $"Revert {target.Id}" : $"Revert {name}", target, name);
    }

    /// <summary>
    /// Adds a section to the file at <paramref name="path"/>. A section goes at the end of the file. A child section
    /// (see <c>docs/MOD_FORMAT.md</c>) goes after the child sections of its parent.
    /// </summary>
    /// <param name="parent">The parent of a child section. Only a child section has one.</param>
    public static EditCommand AddSection(string path, string type, IReadOnlyList<EntryView> entries,
        SectionRef? parent = null)
    {
        if (!PatchFormatter.TryFormatHeader(type, out _, out var error))
            throw new ArgumentException(error, nameof(type));
        foreach (var entry in entries)
            CheckName(entry.Name);
        return new AddSectionCommand($"Add [{type}]", path, type, entries, parent);
    }

    /// <summary>Deletes a section, and its child sections.</summary>
    public static EditCommand DeleteSection(SectionRef target) => new DeleteSectionCommand($"Delete {target.Id}", target);

    /// <summary>
    /// Gives a section a new key. In format version 1, only a section that the active mod adds can get a new key. The
    /// new key must differ from the old key in more than letter case, and no other section of the type can have it.
    /// </summary>
    public static EditCommand Rename(SectionRef target, string newKey)
    {
        if (string.IsNullOrWhiteSpace(newKey))
            throw new ArgumentException("The new key cannot be empty.", nameof(newKey));
        return new RenameCommand($"Rename {target.Id} to {newKey}", target, newKey);
    }

    /// <summary>Sets the text of a key in the active mod's strings.json, or removes it when the text is null.</summary>
    public static EditCommand SetText(StringKind kind, string key, string? text)
    {
        if (!ModStrings.IsValidKey(key))
            throw new ArgumentException($"The key '{key}' must use letters, digits, '_', '-' or '.'.", nameof(key));
        if (text is { Length: 0 })
            throw new ArgumentException("The text cannot be empty. Use null to remove it.", nameof(text));
        return new SetTextCommand($"Set text {key}", kind, key, text);
    }

    /// <summary>Runs commands in order, as one edit.</summary>
    public static EditCommand Group(string label, IReadOnlyList<EditCommand> commands) => new GroupCommand(label, commands, null);

    /// <summary>
    /// Runs commands in order, as one edit, only while <paramref name="current"/> is true. An edit whose commands hold
    /// values that it read from the edit view uses it, so that it never runs on a newer state and undoes the changes in
    /// between (see <see cref="EditSession.Snapshot"/>).
    /// </summary>
    internal static EditCommand Group(string label, IReadOnlyList<EditCommand> commands, Func<bool> current) =>
        new GroupCommand(label, commands, current);

    internal abstract void Run(EditSession.CommandScope scope);

    private static void CheckName(string name)
    {
        if (PatchFormatter.NameError(name) is { } error)
            throw new ArgumentException(error, nameof(name));
    }

    /// <summary>The key entry name of a section, or null when its type has no key.</summary>
    private static string? KeyName(Section section) =>
        SectionKeys.For(section.Name) is { Kind: SectionMatchKind.Keyed } match ? match.KeyEntry : null;

    private sealed class SetLinesCommand(string label, SectionRef target, string name,
        IReadOnlyList<IReadOnlyList<ValueBase>> lines) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var old = document.Working[index];
            if (string.Equals(KeyName(old), name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{name}' is the key of {target.Id}. Use Rename to change it.");
            document.Replace(index, SectionEdits.WithLines(old, name, lines), [name]);
            scope.Changed(target, structural: false);
        }
    }

    private sealed class SetLineTextsCommand(string label, SectionRef target, string name, IReadOnlyList<string> lines)
        : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            // The merge gives the n-th patch line of a name the type rules of the n-th line of that name in B.
            var replaced = document.BaseSection(document.IdentityAt(index))?.Where(e => Is(e, name)).ToList() ?? [];
            var values = new List<IReadOnlyList<ValueBase>>();
            for (var i = 0; i < lines.Count; i++)
            {
                if (!PatchFormatter.TryReadLine(name, lines[i], i < replaced.Count ? replaced[i] : null,
                        document.IsBinary, out var line, out var error))
                    throw new ArgumentException(error, nameof(lines));
                values.Add(line);
            }
            new SetLinesCommand(Label, target, name, values).Run(scope);
        }
    }

    private sealed class RevertCommand(string label, SectionRef target, string? name) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var id = document.IdentityAt(index);
            var lower = document.BaseSection(id) ??
                        throw new InvalidOperationException($"{target.Id}: the active mod adds this section, so the " +
                                                            "lower layer has no lines for it.");
            var old = document.Working[index];
            if (name != null)
            {
                // The lines of a group follow each other in the order of B. One name alone cannot keep that order.
                if (SectionKeys.LinkedGroups(old.Name).Any(g => g.Contains(name, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"{target.Id}: '{name}' belongs to a group of lines that " +
                                                        "belong together. Revert the whole section.");
                var lowerLines = lower.Where(e => Is(e, name)).ToList();
                if (document.Pins(id).Contains(name) ||
                    !SectionComparer.SameLines(lowerLines, old.Where(e => Is(e, name)).ToList(), document.IsBinary))
                {
                    var lines = lowerLines.Select(e => (IReadOnlyList<ValueBase>)e.ToList()).ToList();
                    document.Replace(index, SectionEdits.WithLines(old, name, lines), [name]);
                }
            }
            else if (!ReferenceEquals(old, lower))
            {
                // W shares the sections of B that no patch and no edit changed, so the section of B can go back into W.
                // The names lose their pins.
                document.Replace(index, lower, old.Concat(lower).Select(e => e.Name).ToList());
            }
            scope.Changed(target, structural: false);
        }
    }

    private static bool Is(Entry entry, string name) => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private sealed class AddSectionCommand(string label, string path, string type, IReadOnlyList<EntryView> entries,
        SectionRef? parent) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var document = scope.Document(path);
            int at;
            if (SectionKeys.IsChild(type))
            {
                if (parent == null)
                    throw new InvalidOperationException($"A [{type}] section needs a parent section.");
                var (parentDocument, p) = scope.Find(parent);
                if (!ReferenceEquals(parentDocument, document))
                    throw new InvalidOperationException("The parent section must be in the same file.");
                if (SectionKeys.IsChild(document.Working[p].Name))
                    throw new InvalidOperationException($"{parent.Id} is a child section. It cannot be a parent.");
                at = p + 1 + SectionKeys.ChildCount(document.Working, p);
            }
            else
            {
                if (parent != null)
                    throw new InvalidOperationException($"Only a child section has a parent. [{type}] is not a child section.");
                at = document.Working.Count;
            }
            document.Insert(at, SectionEdits.Build(type, entries));
            scope.Changed(new SectionRef(document.Path, document.IdentityAt(at)), structural: true);
        }
    }

    private sealed class DeleteSectionCommand(string label, SectionRef target) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var children = SectionKeys.IsChild(document.Working[index].Name)
                ? 0
                : SectionKeys.ChildCount(document.Working, index);
            document.Remove(index, 1 + children);
            scope.Changed(target, structural: true);
        }
    }

    private sealed class RenameCommand(string label, SectionRef target, string newKey) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var section = document.Working[index];
            var keyName = KeyName(section) ??
                          throw new InvalidOperationException($"A [{section.Name}] section has no key to change.");
            if (document.InBase(document.IdentityAt(index)))
                throw new InvalidOperationException(
                    $"{target.Id}: in format version 1, the editor can change the key only of a section that the active mod adds.");
            if (string.Equals(SectionIdentity.KeyOf(section), newKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{target.Id}: the new key must differ from the old key in more than letter case.");
            if (document.Working.Any(s => s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(SectionIdentity.KeyOf(s), newKey, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Another [{section.Name}] section has {keyName} = {newKey}.");
            document.Replace(index, SectionEdits.WithLines(section, keyName, [[new StringValue(newKey)]]), [keyName]);
            scope.Changed(target, structural: true);
            scope.Changed(new SectionRef(document.Path, document.IdentityAt(index)), structural: true);
        }
    }

    private sealed class SetTextCommand(string label, StringKind kind, string key, string? text) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope) => scope.Text = scope.Text.With(kind, key, text);
    }

    private sealed class GroupCommand(string label, IReadOnlyList<EditCommand> commands, Func<bool>? current)
        : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            if (current?.Invoke() == false)
                throw new InvalidOperationException($"{Label}: the edit view changed after the edit was made. Make the edit again.");
            foreach (var command in commands)
                command.Run(scope);
        }
    }
}

/// <summary>Builds the new sections of the edit commands.</summary>
internal static class SectionEdits
{
    /// <summary>
    /// A copy of <paramref name="old"/> in which <paramref name="lines"/> take the place of the lines of
    /// <paramref name="name"/>: at the first line of the name, or at the end. The lines keep the letter case of the
    /// name in the section.
    /// </summary>
    public static Section WithLines(Section old, string name, IReadOnlyList<IReadOnlyList<ValueBase>> lines)
    {
        var spelling = old.FirstOrDefault(e => Is(e, name))?.Name ?? name;
        var result = new Section(old.Name, old.Count) { File = old.File, Line = old.Line };
        var placed = false;
        foreach (var entry in old)
        {
            if (!Is(entry, name))
                result.Add(Line(result, entry.Name, entry, entry.Line));
            else if (!placed)
            {
                Add(result, spelling, lines);
                placed = true;
            }
        }
        if (!placed)
            Add(result, spelling, lines);
        return result;
    }

    public static Section Build(string type, IEnumerable<EntryView> entries)
    {
        var result = new Section(type);
        foreach (var entry in entries)
            result.Add(Line(result, entry.Name, entry.Values));
        return result;
    }

    private static void Add(Section section, string name, IReadOnlyList<IReadOnlyList<ValueBase>> lines)
    {
        foreach (var values in lines)
            section.Add(Line(section, name, values));
    }

    /// <summary>A new entry of <paramref name="section"/>. It shares the values, because a value cannot change.</summary>
    /// <param name="line">The line number in the file that the entry comes from, for messages, or -1.</param>
    private static Entry Line(Section section, string name, IEnumerable<ValueBase> values, int line = -1)
    {
        var entry = new Entry(section, name) { Line = line };
        foreach (var value in values)
            entry.Add(value);
        return entry;
    }

    private static bool Is(Entry entry, string name) => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
}
