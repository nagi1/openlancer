using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Strings;

public enum StringKind
{
    /// <summary>A string table entry, used by <c>ids_name</c> values.</summary>
    String,

    /// <summary>An infocard (RDL XML), used by <c>ids_info</c> values.</summary>
    Infocard,
}

/// <summary>
/// A reference from a patch value to a mod's text: <c>$str:key</c> or <c>$ifc:key</c>.
/// </summary>
public readonly record struct StringPlaceholder(StringKind Kind, string Key)
{
    private const string StringPrefix = "$str:";
    private const string InfocardPrefix = "$ifc:";

    public static bool TryParse(string text, out StringPlaceholder placeholder)
    {
        if (text.StartsWith(StringPrefix, StringComparison.Ordinal))
        {
            placeholder = new(StringKind.String, text[StringPrefix.Length..]);
            return true;
        }
        if (text.StartsWith(InfocardPrefix, StringComparison.Ordinal))
        {
            placeholder = new(StringKind.Infocard, text[InfocardPrefix.Length..]);
            return true;
        }
        placeholder = default;
        return false;
    }

    /// <summary>Reads a value as the merge reads it: a value whose text is a placeholder.</summary>
    public static bool TryParse(ValueBase value, out StringPlaceholder placeholder)
    {
        // The text of a number or a boolean is never a placeholder.
        if (value is Int32Value or SingleValue or BooleanValue)
        {
            placeholder = default;
            return false;
        }
        return TryParse(value.ToString(), out placeholder);
    }

    public override string ToString() => (Kind == StringKind.String ? StringPrefix : InfocardPrefix) + Key;
}

/// <summary>
/// Gives each text entry of the mods a resource id in the one generated string DLL.
/// </summary>
/// <remarks>
/// The ids depend only on the mods, their order and their keys, so every build of a profile gives the same ids.
/// Data must refer to mod text only through placeholders, never through the numbers.
/// Strings and infocards share one local id range, so no id is used for both.
/// </remarks>
public sealed class StringIds
{
    /// <summary>A resource DLL holds local ids 1 to 65535.</summary>
    public const int MaxEntries = ushort.MaxValue;

    private readonly Dictionary<(string ModId, StringKind Kind, string Key), int> ids;

    private StringIds(int dllIndex, Dictionary<(string, StringKind, string), int> ids,
        Dictionary<int, string> strings, Dictionary<int, string> infocards)
    {
        DllIndex = dllIndex;
        this.ids = ids;
        Strings = strings;
        Infocards = infocards;
    }

    /// <summary>The position of the generated DLL in the <c>[Resources]</c> list. resources.dll is 0.</summary>
    public int DllIndex { get; }

    /// <summary>Local id to text, for the DLL string table.</summary>
    public IReadOnlyDictionary<int, string> Strings { get; }

    /// <summary>Local id to RDL XML, for the DLL infocard table.</summary>
    public IReadOnlyDictionary<int, string> Infocards { get; }

    public bool IsEmpty => ids.Count == 0;

    public static StringIds Allocate(IReadOnlyList<Mod> modsInOrder, int dllIndex)
    {
        var ids = new Dictionary<(string, StringKind, string), int>();
        var strings = new Dictionary<int, string>();
        var infocards = new Dictionary<int, string>();
        var next = 1;
        foreach (var mod in modsInOrder)
        {
            foreach (var (kind, table, output) in new[]
                     {
                         (StringKind.String, mod.Strings.Strings, strings),
                         (StringKind.Infocard, mod.Strings.Infocards, infocards),
                     })
            {
                foreach (var key in table.Keys.Order(StringComparer.Ordinal))
                {
                    if (next > MaxEntries)
                        throw new ModLoadException(mod.Folder, $"The mods have more than {MaxEntries} text entries.");
                    ids[(mod.Id, kind, key)] = dllIndex * 65536 + next;
                    output[next] = table[key];
                    next++;
                }
            }
        }
        return new StringIds(dllIndex, ids, strings, infocards);
    }

    public bool TryGet(string modId, StringPlaceholder placeholder, [NotNullWhen(true)] out int? id)
    {
        if (ids.TryGetValue((modId, placeholder.Kind, placeholder.Key), out var value))
        {
            id = value;
            return true;
        }
        id = null;
        return false;
    }
}

/// <summary>
/// Writes a Freelancer resource DLL. OpenLancer.Runtime implements this with Librelancer's DllWriter.
/// </summary>
public interface IStringResourceWriter
{
    /// <param name="strings">Local id to string table text.</param>
    /// <param name="infocards">Local id to infocard XML.</param>
    byte[] Write(IReadOnlyDictionary<int, string> strings, IReadOnlyDictionary<int, string> infocards);
}
