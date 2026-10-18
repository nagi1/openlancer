using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace OpenLancer.Data.Thn;

/// <summary>
/// The THN names that only Librelancer defines, and the pass that writes their numbers for the retail game (milestone
/// M11). The retail cutscene files never use these names. The retail game knows the same numbers, so a cutscene with the
/// numbers works also if the retail game does not define the names.
/// </summary>
public static class ThnRetailNames
{
    /// <summary>The nine names that no retail or HD Edition cutscene uses.</summary>
    public static readonly FrozenSet<string> LibrelancerOnly = FrozenSet.Create(StringComparer.Ordinal,
        "STREAM", "UNKNOWN_ENTITY", "SUB_SCENE", "DELETED", "SUBTITLE", "START_REVERB_PROP_ANIM", "USER_EVENT",
        "UNDEFINED_EVENT", "START_SUB_SCENE");

    /// <summary>The tree with the number of each Librelancer-only name in place of the name.</summary>
    /// <param name="symbols">The names of the THN environment with their numbers (see
    /// <see cref="IThnEvaluator.Symbols"/>).</param>
    /// <exception cref="ArgumentException">The symbols do not have a name of the tree.</exception>
    public static ThnTable Apply(ThnTable globals, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        (ThnTable)Replace(globals, symbols);

    /// <summary>The Librelancer-only names that the tree uses, in ordinal order.</summary>
    public static IReadOnlyList<string> Find(ThnTable globals)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        Collect(globals, found);
        return [.. found];
    }

    private static ThnValue Replace(ThnValue value, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        switch (value)
        {
            case ThnSymbol symbol when LibrelancerOnly.Contains(symbol.Name):
                if (!symbols.TryGetValue(symbol.Name, out var info))
                    throw new ArgumentException($"The THN environment does not define {symbol.Name}.", nameof(symbols));
                return new ThnNumber(info.Value);
            case ThnTable table:
                var list = table.List.Select(v => Replace(v, symbols)).ToImmutableArray();
                var record = table.Record.Select(f => f with { Value = Replace(f.Value, symbols) }).ToImmutableArray();
                return new ThnTable(list, record);
            default:
                return value;
        }
    }

    private static void Collect(ThnValue value, SortedSet<string> found)
    {
        switch (value)
        {
            case ThnSymbol symbol when LibrelancerOnly.Contains(symbol.Name):
                found.Add(symbol.Name);
                break;
            case ThnTable table:
                foreach (var item in table.List)
                    Collect(item, found);
                foreach (var field in table.Record)
                    Collect(field.Value, found);
                break;
        }
    }
}
