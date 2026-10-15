using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenLancer.Data.Thn;

/// <summary>The key of an element in its list, with its place among the elements with the same key.</summary>
/// <param name="Signature">Equal for two elements with the same key (see <see cref="ThnKeys"/>).</param>
/// <param name="Nth">The place of the element among the elements with the same key, from 1.</param>
/// <param name="Count">The number of elements with the same key.</param>
internal readonly record struct ThnKey(string Signature, int Nth, int Count);

/// <summary>
/// The keys that a THN patch uses to find an element. A key does not change when a lower mod deletes or adds another
/// element, as an index does.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An entity key is the entity name, without regard to letter case, as the engine compares names
/// (ThnScript.cs:107). 33 retail files repeat a name. An entity without a text name has the empty name.</item>
/// <item>An event key is the time, the type and the targets, as the file has them. The time compares by its float bits,
/// the targets compare by their characters, and the type compares by its number: <c>SET_CAMERA</c> and its number are
/// the same type (ThnTypes.cs:41-78). 678 retail files repeat an event key.</item>
/// </list>
/// When a key repeats, the place (<c>nth</c>) and the number (<c>count</c>) of the elements with the key name one of
/// them.
/// </remarks>
internal static class ThnKeys
{
    public const string EntityName = ThnScriptModel.EntityNameKey;

    /// <summary>The key value of an entity without a text name.</summary>
    private static readonly ThnText NoName = new("");

    /// <summary>The key value of an element, as a patch writes it in <c>ol_match</c>: the name text of an entity, or
    /// the list <c>{ time, type, { targets } }</c> of an event. Null when the event has no key.</summary>
    /// <remarks>An entity without a text name has the empty name, so a patch can still name it. A patch can remove the
    /// name, and a later patch must then find the entity. The engine cannot load such an entity.</remarks>
    public static ThnValue? Match(ThnElementKind kind, ThnTable element)
    {
        if (kind == ThnElementKind.Entity)
            return element[EntityName] as ThnText ?? NoName;
        return element.List.Length >= 3 ? ThnTable.FromList(element.List[0], element.List[1], element.List[2]) : null;
    }

    /// <summary>The signature of a key value: the same text for two key values that name the same elements. Null when
    /// the value is not a key, or when the type is a name that <paramref name="symbols"/> does not have.</summary>
    public static string? Signature(ThnElementKind kind, ThnValue? match, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        if (kind == ThnElementKind.Entity)
            return match is ThnText name ? name.Value.ToUpperInvariant() : null;
        if (match is not ThnTable { Record.IsEmpty: true, List: [ThnNumber time, var type, ThnTable { Record.IsEmpty: true } targets] } ||
            TypeNumber(type, symbols) is not { } number || targets.List.Any(t => t is not ThnText))
            return null;
        var text = new StringBuilder()
            .Append(BitConverter.SingleToInt32Bits(time.Value).ToString("X8", CultureInfo.InvariantCulture))
            .Append(' ').Append(number.ToString("R", CultureInfo.InvariantCulture));
        // A length before each target keeps two lists of targets apart.
        foreach (ThnText target in targets.List)
            text.Append(' ').Append(target.Value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(target.Value);
        return text.ToString();
    }

    /// <summary>The key of each element, in list order. Null for an element without a key.</summary>
    public static ImmutableArray<ThnKey?> Of(ThnElementKind kind, IEnumerable<ThnTable> elements,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var signatures = elements.Select(e => Signature(kind, Match(kind, e), symbols)).ToList();
        var counts = signatures.OfType<string>().CountBy(s => s, StringComparer.Ordinal)
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = ImmutableArray.CreateBuilder<ThnKey?>(signatures.Count);
        foreach (var signature in signatures)
        {
            if (signature == null)
            {
                keys.Add(null);
                continue;
            }
            var nth = seen[signature] = seen.GetValueOrDefault(signature) + 1;
            keys.Add(new ThnKey(signature, nth, counts[signature]));
        }
        return keys.MoveToImmutable();
    }

    /// <summary>The key value as text for a message, for example <c>"Cam_02"</c> or
    /// <c>{ 25.75, SET_CAMERA, { "Cam_Monitor", "Cam_03" } }</c>.</summary>
    public static string Text(ThnValue match) => ThnWriter.Value(match);

    /// <summary>The number of an event type: a number, or the number of a THN name. Null for another value.</summary>
    private static double? TypeNumber(ThnValue type, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) => type switch
    {
        ThnNumber number => number.Value,
        ThnSymbol symbol when symbols.TryGetValue(symbol.Name, out var info) => info.Value,
        _ => null,
    };
}
