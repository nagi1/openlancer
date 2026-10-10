using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Proofs that the linked groups of <c>SectionKeys</c> hold each set of names whose lines the engine reads together
/// (review finding F6). A patch writes the changed names of a section as one block, so it can move the lines of
/// one name away from the lines of another name. That is safe only when the engine reads the two names
/// independently, when they are in one linked group, or when the section type is order-sensitive.
/// </summary>
public sealed class RepeatedEntryClassificationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Entry handlers whose method reads only its own line, so the order of their lines among the lines of other
    /// names does not count. Each item names the upstream method.
    /// </summary>
    private static readonly HashSet<(string Section, string Name)> IndependentHandlers = new(SectionAndName.Instance)
    {
        // Effects/Explosion.cs HandleEffect sets the effect from its line.
        ("explosion", "effect"),
        // Solar/Loadout.cs HandleCargo and HandleEquip add one item from their line.
        ("loadout", "cargo"),
        ("loadout", "equip"),
        // Missions/MissionShip.cs ParseArrivalObj sets the arrival object from its line.
        ("msnship", "arrival_obj"),
        // Archetype.cs HandleFuse adds one fuse from its line.
        ("solar", "fuse"),
    };

    /// <summary>
    /// Rule 1: a method with two or more <c>[EntryHandler]</c> names keeps state between the lines of these names.
    /// For example, <c>Zone.HandleFaction</c> adds each <c>faction</c> and <c>faction_weight</c> line to the last
    /// encounter. So all names of the method must be in one linked group.
    /// </summary>
    [Fact]
    public void TheNamesOfEachEntryHandlerMethodAreInOneLinkedGroup()
    {
        var checkedMethods = new SortedSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (_, section, schema) in SchemaCatalog.All)
        {
            foreach (var method in Hierarchy(schema.Class).SelectMany(t => t.GetMethods(Members | BindingFlags.DeclaredOnly)))
            {
                var names = method.GetCustomAttributes<EntryHandlerAttribute>().Select(a => a.Name).ToList();
                if (names.Count < 2)
                    continue;
                var label = $"[{section.ToLowerInvariant()}] {method.Name}({string.Join(", ", names)})";
                checkedMethods.Add(label);
                // The writer writes all lines of an order-sensitive section.
                if (!SectionKeys.IsOrderSensitive(section) &&
                    !SectionKeys.LinkedGroups(section).Any(g => names.All(n => g.Contains(n, StringComparer.OrdinalIgnoreCase))))
                    problems.Add(label);
            }
        }
        foreach (var method in checkedMethods)
            output.WriteLine("checked: " + method);
        // The rule finds the method of the review finding, so the test cannot pass because it checks nothing.
        Assert.Contains("[zone] HandleFaction(faction, faction_weight)", checkedMethods);
        Assert.Empty(problems);
    }

    /// <summary>
    /// Rule 2: in each retail section that a patch can change, two names whose lines interleave (A, B, A) are in one
    /// linked group, or the section type is order-sensitive, or the engine reads them independently: each name is
    /// unknown to the schema, a field of its own, or an entry handler that reads only its own line.
    /// </summary>
    [Fact]
    public void EachInterleaveInAPatchableRetailSectionIsLinkedOrIndependent()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var classified = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(baseFolder, "*.ini", SearchOption.AllDirectories))
        {
            var sections = IniDocument.Parse(file, File.ReadAllBytes(file)).Sections;
            var keyCounts = ProofSections.KeyCounts(sections);
            var typeCounts = sections.CountBy(s => s.Name.ToLowerInvariant()).ToDictionary();
            foreach (var section in sections)
            {
                if (!Patchable(section, keyCounts, typeCounts))
                    continue;
                foreach (var (a, b) in Interleaves(section))
                {
                    var type = section.Name.ToLowerInvariant();
                    var reason = SectionKeys.IsOrderSensitive(type) ? "order-sensitive"
                        : SectionKeys.LinkedGroups(type).Any(g => g.Contains(a, StringComparer.OrdinalIgnoreCase) &&
                                                                  g.Contains(b, StringComparer.OrdinalIgnoreCase)) ? "linked group"
                        : Independent(type, a, b) ? "independent"
                        : null;
                    if (reason == null)
                        problems.Add($"[{type}] {a}, {b}: {Path.GetRelativePath(baseFolder, file)}");
                    else
                        classified[$"[{type}] {a}, {b}: {reason}"] = classified.GetValueOrDefault($"[{type}] {a}, {b}: {reason}") + 1;
                }
            }
        }
        foreach (var (pair, count) in classified)
            output.WriteLine($"{pair} ({count})");
        foreach (var problem in problems)
            output.WriteLine("not classified: " + problem);
        // The rule finds the interleaves of the linked groups, so the test cannot pass because it checks nothing.
        Assert.Contains("[zone] encounter, faction: linked group", classified.Keys);
        Assert.Contains("[gf_npc] know, knowdb: linked group", classified.Keys);
        Assert.Contains("[gf_npc] rumor, rumorknowdb: linked group", classified.Keys);
        Assert.Empty(problems);
    }

    /// <summary>True when a patch can find the section: by a key that no other section of its type in the file has,
    /// or as the only section of a singleton type.</summary>
    private static bool Patchable(Section section, Dictionary<(string, string), int> keyCounts, Dictionary<string, int> typeCounts)
    {
        var match = SectionKeys.For(section.Name);
        if (match.Kind == SectionMatchKind.Singleton)
            return typeCounts[section.Name.ToLowerInvariant()] == 1;
        return match.Kind == SectionMatchKind.Keyed && ProofSections.KeyOf(section) is { } key &&
               keyCounts.GetValueOrDefault((section.Name, key)) == 1;
    }

    /// <summary>Each pair (A, B) of names in which a line of B comes between two lines of A.</summary>
    private static HashSet<(string A, string B)> Interleaves(Section section)
    {
        var pairs = new HashSet<(string, string)>();
        var names = section.Select(e => e.Name.ToLowerInvariant()).ToList();
        var last = new Dictionary<string, int>();
        for (var i = 0; i < names.Count; i++)
        {
            if (last.TryGetValue(names[i], out var previous))
            {
                foreach (var between in names.Skip(previous + 1).Take(i - previous - 1).Where(n => n != names[i]))
                    pairs.Add((names[i], between));
            }
            last[names[i]] = i;
        }
        return pairs;
    }

    /// <summary>
    /// True when every schema of the section type reads the two names independently. A type without a schema is
    /// not independent, because the test cannot tell how upstream code reads it.
    /// </summary>
    private static bool Independent(string type, string a, string b)
    {
        var schemas = SchemaCatalog.All.Where(s => s.Section.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
        return schemas.Count > 0 && schemas.All(s =>
        {
            var readerA = Reader(s.Schema.Class, a);
            var readerB = Reader(s.Schema.Class, b);
            // Two names that one member reads: the last line of the two wins, so their order counts.
            return (readerA == null || readerB == null || readerA != readerB) &&
                   ReadsItsOwnLine(type, a, readerA) && ReadsItsOwnLine(type, b, readerB);
        });
    }

    private static bool ReadsItsOwnLine(string type, string name, MemberInfo? reader) =>
        reader is null or FieldInfo || IndependentHandlers.Contains((type, name));

    /// <summary>The field or the entry handler method that reads a name, or null when the class does not know the
    /// name.</summary>
    private static MemberInfo? Reader(Type type, string name) =>
        Hierarchy(type).SelectMany(t => t.GetMembers(Members | BindingFlags.DeclaredOnly))
            .FirstOrDefault(m => m.GetCustomAttributes<EntryAttribute>().Any(e => Is(e.Name, name)) ||
                                 m.GetCustomAttributes<EntryHandlerAttribute>().Any(e => Is(e.Name, name)));

    private static IEnumerable<Type> Hierarchy(Type type)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            yield return t;
    }

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                         BindingFlags.Static;

    private sealed class SectionAndName : IEqualityComparer<(string Section, string Name)>
    {
        public static readonly SectionAndName Instance = new();

        public bool Equals((string Section, string Name) x, (string Section, string Name) y) =>
            Is(x.Section, y.Section) && Is(x.Name, y.Name);

        public int GetHashCode((string Section, string Name) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Section), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
