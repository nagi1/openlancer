using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>
/// Identifies one section of an INI file, so that two versions of the file can be compared section by section.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A section with a key (see <see cref="SectionKeys"/>) is (type, key), for example <c>[Good] le_hull</c>.
/// The section must have exactly one key entry with one value.</item>
/// <item>A section of a singleton type is (type). So is a section without a line of an optional key (see
/// <see cref="SectionMatch.KeyOptional"/>), for example <c>[LootableZone] without zone</c>.</item>
/// <item>Any other section without a key is (type, ordinal), for example the n-th <c>[BaseGood]</c> of a file.</item>
/// <item>A child section also has its parent: (parent, type, key), or (parent, type, ordinal) without a key.</item>
/// </list>
/// When two sections give the same identity, the later one gets the next <see cref="Ordinal"/>. Type and key compare
/// without letter case, as the merge compares them.
/// <para>A child section has its parent because retail files repeat child keys under different parents, for example
/// <c>[MRoom] nickname = bar</c> in mbases.ini. An ordinal in the whole file would then change when another parent
/// goes with its children. A patch still finds a child section by its key in the whole file. So a patch can change
/// a child section only when no other section of its type has that key.</para>
/// </remarks>
/// <param name="Type">The section name, for example <c>Good</c>.</param>
/// <param name="Key">The value of the key entry, or null for a section without a key.</param>
/// <param name="Parent">The parent of a child section: the closest section before it that is not a child section.</param>
/// <param name="Ordinal">0 for the first section with this identity, 1 for the second, and so on.</param>
public sealed record SectionIdentity(string Type, string? Key, SectionIdentity? Parent = null, int Ordinal = 0)
{
    public bool Equals(SectionIdentity? other) =>
        other is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(Type, other.Type) &&
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) &&
        Equals(Parent, other.Parent) &&
        Ordinal == other.Ordinal;

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Type),
        Key == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Key),
        Parent,
        Ordinal);

    /// <summary>For example <c>[Good] nickname = le_hull</c>, <c>[SystemInfo]</c> or
    /// <c>[CollisionGroup] #2 of [Ship] nickname = li_elite</c>.</summary>
    public override string ToString()
    {
        var match = SectionKeys.For(Type);
        var parent = Parent != null ? $" of {Parent}" : "";
        if (Key != null)
            return $"[{Type}] {match.KeyEntry} = {Key}" + (Ordinal > 0 ? $" (#{Ordinal + 1})" : "") + parent;
        if (match.Kind == SectionMatchKind.Singleton && Parent == null)
            return $"[{Type}]" + (Ordinal > 0 ? $" #{Ordinal + 1}" : "");
        if (match.KeyOptional && Parent == null)
            return $"[{Type}] without {match.KeyEntry}" + (Ordinal > 0 ? $" #{Ordinal + 1}" : "");
        return $"[{Type}] #{Ordinal + 1}" + parent;
    }

    /// <summary>Gives the identity of each section of a file, in file order.</summary>
    internal static SectionIdentity[] Identify(IReadOnlyList<Section> sections)
    {
        var result = new SectionIdentity[sections.Count];
        var seen = new Dictionary<SectionIdentity, int>();
        SectionIdentity? parent = null;
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var isChild = SectionKeys.IsChild(section.Name);
            var key = KeyOf(section);
            var first = new SectionIdentity(section.Name, key, isChild ? parent : null);
            var count = seen.GetValueOrDefault(first);
            seen[first] = count + 1;
            result[i] = count == 0 ? first : first with { Ordinal = count };
            if (!isChild)
                parent = result[i];
        }
        return result;
    }

    /// <summary>
    /// The identity of one section of a file, as <see cref="Identify"/> gives it. It does not make the identities of
    /// the other sections, so the merge can use it for each section that it adds.
    /// </summary>
    internal static SectionIdentity Of(IReadOnlyList<Section> sections, int index)
    {
        var section = sections[index];
        SectionIdentity? parent = null;
        // The sections that can have the same identity: all sections before it, or the children of its parent.
        var first = 0;
        if (SectionKeys.IsChild(section.Name))
        {
            var p = index - 1;
            while (p >= 0 && SectionKeys.IsChild(sections[p].Name))
                p--;
            if (p >= 0)
            {
                parent = Of(sections, p);
                first = p + 1;
            }
        }
        var key = KeyOf(section);
        var ordinal = 0;
        for (var i = first; i < index; i++)
        {
            if (sections[i].Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(KeyOf(sections[i]), key, StringComparison.OrdinalIgnoreCase))
                ordinal++;
        }
        return new SectionIdentity(section.Name, key, parent, ordinal);
    }

    /// <summary>
    /// The key of a section whose type has a key entry, when the section has exactly one key entry with one value.
    /// </summary>
    internal static string? KeyOf(Section section)
    {
        var match = SectionKeys.For(section.Name);
        if (match.Kind != SectionMatchKind.Keyed)
            return null;
        var keys = section.Where(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return keys is [{ Count: 1 } key] ? key[0].ToString() : null;
    }
}

/// <summary>A section of one file of the game data.</summary>
/// <param name="Path">The install path of the file, in <see cref="VfsPath.Normalize"/> form. It compares without
/// letter case.</param>
public sealed record SectionRef(string Path, SectionIdentity Id)
{
    public bool Equals(SectionRef? other) =>
        other is not null && VfsPath.Comparer.Equals(Path, other.Path) && Id.Equals(other.Id);

    public override int GetHashCode() => HashCode.Combine(VfsPath.Comparer.GetHashCode(Path), Id);

    public override string ToString() => $"{Path} {Id}";
}
