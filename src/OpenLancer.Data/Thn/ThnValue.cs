using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace OpenLancer.Data.Thn;

/// <summary>
/// A value of a cutscene script (<c>.thn</c>), as the Thorn VM of the engine gives it: a number, a text, <c>Y</c> or
/// <c>N</c>, a THN name, or a table. <see cref="IThnEvaluator"/> makes the tree, and <see cref="ThnWriter"/> writes it.
/// </summary>
/// <remarks>
/// <para>The tree is immutable. An edit makes a new path from the root to the changed value and shares the rest.</para>
/// <para>Equality is exact. Numbers compare by their bits, texts and names compare by their characters, and tables
/// compare their list part and their record part in order, keys included. A number and a name are different values,
/// also when the engine reads them as the same value, for example <c>type = 3</c> and <c>type = CAMERA</c>.</para>
/// <para>The five kinds below are the only kinds. Other assemblies cannot add a kind.</para>
/// </remarks>
public abstract record ThnValue
{
    private protected ThnValue()
    {
    }
}

/// <summary>A number. It is always finite, and it is never -0.</summary>
/// <remarks>
/// The Lua 3.2 compiler writes the text <c>-0</c> as +0 (lparser.c:284-291), so a -0 cannot survive a save. The VM has
/// no text for NaN or infinity. Because the value is never -0 or NaN, the record equality of the float is the equality
/// of its bits.
/// </remarks>
public sealed record ThnNumber : ThnValue
{
    /// <summary>The message for a value that is NaN or infinite.</summary>
    public const string NotFiniteMessage = "The value must be a finite number.";

    /// <param name="value">The number. -0 becomes +0.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or infinite.</exception>
    public ThnNumber(float value)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, NotFiniteMessage);
        // The comparison is also true for -0.
        Value = value == 0 ? 0f : value;
    }

    public float Value { get; }
}

/// <summary>A text. <see cref="ThnWriter"/> writes only ASCII text without the character U+0000.</summary>
public sealed record ThnText : ThnValue
{
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public ThnText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public string Value { get; }
}

/// <summary><c>Y</c> (true) or <c>N</c> (false).</summary>
public sealed record ThnBool(bool Value) : ThnValue;

/// <summary>A name of the THN environment, for example <c>CAMERA</c>. <see cref="IThnEvaluator.Symbols"/> gives its
/// group and its number.</summary>
public sealed record ThnSymbol : ThnValue
{
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    public ThnSymbol(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    public string Name { get; }
}

/// <summary>A field of the record part of a table.</summary>
public readonly record struct ThnField(string Key, ThnValue Value);

/// <summary>
/// A table: a list part (the keys 1 to n in Lua) and a record part (text keys, in the order of the script).
/// </summary>
/// <remarks>The VM gives the record part in insertion order (ThornTable.cs:352-363), and a table constructor fills it
/// in source order (ThornRuntime.cs:364-399). The writer keeps this order.</remarks>
public sealed record ThnTable : ThnValue
{
    /// <summary>Above this many fields, the duplicate key check uses a hash set.</summary>
    private const int LinearKeyCheckLimit = 16;

    public static readonly ThnTable Empty = new([], []);

    /// <exception cref="ArgumentException">An array is default, an item or a key is null, or two fields have the same
    /// key. A Lua table constructor with a key twice does not run.</exception>
    public ThnTable(ImmutableArray<ThnValue> list, ImmutableArray<ThnField> record)
    {
        if (list.IsDefault || record.IsDefault)
            throw new ArgumentException("The list part and the record part must be arrays.");
        foreach (var item in list)
        {
            if (item is null)
                throw new ArgumentException("A list item is null.", nameof(list));
        }
        HashSet<string>? keys = record.Length > LinearKeyCheckLimit ? new HashSet<string>(StringComparer.Ordinal) : null;
        for (var i = 0; i < record.Length; i++)
        {
            var (key, value) = record[i];
            if (key is null || value is null)
                throw new ArgumentException("A field has no key or no value.", nameof(record));
            if (keys != null ? !keys.Add(key) : IndexOf(record, key, i) >= 0)
                throw new ArgumentException($"The key '{key}' occurs two times.", nameof(record));
        }
        List = list;
        Record = record;
    }

    /// <summary>The list part: the values of the keys 1 to n.</summary>
    public ImmutableArray<ThnValue> List { get; }

    /// <summary>The record part, in order.</summary>
    public ImmutableArray<ThnField> Record { get; }

    /// <summary>True when the table has no list part. An empty table is a record, so a path can go into it.</summary>
    public bool IsRecord => List.IsEmpty;

    /// <summary>The value of a record key, or null when the record has no such key. Keys are case-sensitive, as in the
    /// VM.</summary>
    public ThnValue? this[string key]
    {
        get
        {
            var index = IndexOf(Record, key, Record.Length);
            return index < 0 ? null : Record[index].Value;
        }
    }

    public static ThnTable FromList(params ReadOnlySpan<ThnValue> items) => new([.. items], []);

    public static ThnTable FromRecord(params ReadOnlySpan<ThnField> fields) => new([], [.. fields]);

    public bool Equals(ThnTable? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || List.Length != other.List.Length || Record.Length != other.Record.Length)
            return false;
        for (var i = 0; i < List.Length; i++)
        {
            if (!List[i].Equals(other.List[i]))
                return false;
        }
        for (var i = 0; i < Record.Length; i++)
        {
            if (!Record[i].Equals(other.Record[i]))
                return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in List)
            hash.Add(item);
        foreach (var field in Record)
            hash.Add(field);
        return hash.ToHashCode();
    }

    /// <summary>The index of <paramref name="key"/> in the first <paramref name="count"/> fields, or -1.</summary>
    private static int IndexOf(ImmutableArray<ThnField> record, string key, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (string.Equals(record[i].Key, key, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}

/// <summary>
/// Paths of values in a tree, for messages. A path is a Lua index text from the root, for example
/// <c>entities[3].spatialprops.pos[1]</c>. List items count from 1, as in Lua.
/// </summary>
public static class ThnValues
{
    /// <summary>The path of a list item.</summary>
    /// <param name="index">The Lua index, from 1.</param>
    public static string ItemPath(string path, int index) => $"{path}[{index}]";

    /// <summary>The path of a record field. At the root, the path is the key.</summary>
    public static string FieldPath(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    /// <summary>
    /// The path of the first place where two trees differ, or null when they are equal. For a value, the path is the
    /// path of the value. When two tables differ in their list length, their keys or the order of their keys, the
    /// path is the path of the table. The root has the empty path.
    /// </summary>
    public static string? FirstDifference(ThnValue a, ThnValue b) => a.Equals(b) ? null : Difference(a, b, "");

    /// <summary>The path of the first difference of two values that are not equal. The equality check comes first, so
    /// equal trees make no path text.</summary>
    private static string Difference(ThnValue a, ThnValue b, string path)
    {
        if (a is not ThnTable ta || b is not ThnTable tb || ta.List.Length != tb.List.Length ||
            ta.Record.Length != tb.Record.Length)
            return path;
        for (var i = 0; i < ta.List.Length; i++)
        {
            if (!ta.List[i].Equals(tb.List[i]))
                return Difference(ta.List[i], tb.List[i], ItemPath(path, i + 1));
        }
        for (var i = 0; i < ta.Record.Length; i++)
        {
            if (!string.Equals(ta.Record[i].Key, tb.Record[i].Key, StringComparison.Ordinal))
                return path;
        }
        for (var i = 0; i < ta.Record.Length; i++)
        {
            if (!ta.Record[i].Value.Equals(tb.Record[i].Value))
                return Difference(ta.Record[i].Value, tb.Record[i].Value, FieldPath(path, ta.Record[i].Key));
        }
        // Not reached: the tables are not equal, so a part above differs.
        return path;
    }
}
