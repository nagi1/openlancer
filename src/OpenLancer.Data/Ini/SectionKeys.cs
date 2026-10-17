using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Ini;

internal enum SectionMatchKind
{
    /// <summary>The section is found by the value of one key entry, such as <c>nickname</c>.</summary>
    Keyed,

    /// <summary>The file has at most one section of this type.</summary>
    Singleton,

    /// <summary>Patch sections are added as new sections. The engine merges repeated sections.</summary>
    Append,
}

internal readonly record struct SectionMatch(SectionMatchKind Kind, string? KeyEntry);

/// <summary>
/// How a patch section of each type finds the section that it changes, and which section types belong to a parent.
/// </summary>
/// <remarks>
/// Most Freelancer sections are identified by <c>nickname</c>. This table lists the exceptions.
/// </remarks>
internal static class SectionKeys
{
    public const string DefaultKey = "nickname";

    private static readonly Dictionary<string, SectionMatch> Exceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        // DATA\initialworld.ini and faction_prop.ini
        ["FactionProps"] = new(SectionMatchKind.Keyed, "affiliation"),
        ["RepChangeEffects"] = new(SectionMatchKind.Keyed, "group"),
        // DATA\FX\fuse*.ini
        ["fuse"] = new(SectionMatchKind.Keyed, "name"),
        // Market files: the engine adds the goods of repeated [BaseGood] sections for one base.
        ["BaseGood"] = new(SectionMatchKind.Append, null),
        // Star system files
        ["Nebula"] = new(SectionMatchKind.Keyed, "zone"),
        ["Asteroids"] = new(SectionMatchKind.Keyed, "zone"),
        ["SystemInfo"] = new(SectionMatchKind.Singleton, null),
        ["Music"] = new(SectionMatchKind.Singleton, null),
        ["Dust"] = new(SectionMatchKind.Singleton, null),
        ["Ambient"] = new(SectionMatchKind.Singleton, null),
        ["Background"] = new(SectionMatchKind.Singleton, null),
        ["TexturePanels"] = new(SectionMatchKind.Singleton, null),
        // Room files (decision D66 of the M8 design). In retail, each of these occurs at most once in a file, and only
        // in the 446 room files. [CharacterPlacement] has a name line, but no nickname.
        ["Room_Info"] = new(SectionMatchKind.Singleton, null),
        ["CharacterPlacement"] = new(SectionMatchKind.Singleton, null),
        ["PlayerShipPlacement"] = new(SectionMatchKind.Singleton, null),
    };

    /// <summary>
    /// Section types that the engine attaches to the closest section before them that is not of a child type
    /// (upstream <c>[Section(..., Child = true)]</c>).
    /// </summary>
    private static readonly HashSet<string> ChildTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CollisionGroup", // [Ship], [Solar]
        "LOD", // equipment
        // [fuse]. The engine does not read the last five, but they are part of a fuse in the retail files.
        "start_effect", "destroy_group", "destroy_hp_attachment", "start_cam_particles", "ignite_fuse", "impulse",
        "destroy_root", "tumble", "damage_group", "damage_root", "make_invincible", "dump_cargo",
        "MVendor", "MRoom", "GF_NPC", "BaseFaction", // [MBase]
    };

    /// <summary>
    /// Entry names whose lines belong together: the engine attaches each later name to the last line before it.
    /// A patch must list all names of a group, or none.
    /// </summary>
    private static readonly Dictionary<string, string[][]> LinkedEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Zone.HandleFaction adds faction and faction_weight lines to the last encounter before them.
        ["Zone"] = [["encounter", "faction", "faction_weight"]],
        ["GF_NPC"] = [["know", "knowdb"], ["rumor", "rumor_type2", "rumorknowdb"]],
        ["EncounterFormation"] = [["ship_by_class", "ship_by_npc_arch", "pilot_job", "make_class"]],
    };

    public static SectionMatch For(string sectionName) =>
        Exceptions.TryGetValue(sectionName, out var match) ? match : new(SectionMatchKind.Keyed, DefaultKey);

    public static bool IsChild(string sectionName) => ChildTypes.Contains(sectionName);

    /// <summary>The number of child sections that directly follow the section at <paramref name="index"/>.</summary>
    public static int ChildCount(IReadOnlyList<Section> sections, int index)
    {
        var count = 0;
        while (index + 1 + count < sections.Count && IsChild(sections[index + 1 + count].Name))
            count++;
        return count;
    }

    public static IReadOnlyList<string[]> LinkedGroups(string sectionName) =>
        LinkedEntries.TryGetValue(sectionName, out var groups) ? groups : [];

    /// <summary>
    /// True when the engine reads the lines of the section one by one in file order, so the order of all lines
    /// counts. For example, a mission <c>[Trigger]</c> runs its actions in order.
    /// </summary>
    /// <remarks>
    /// These are the section types whose upstream schema class implements <see cref="IEntryHandler"/>. The list comes
    /// from the <c>[Section]</c> attributes of the upstream schema, so it follows upstream changes.
    /// </remarks>
    public static bool IsOrderSensitive(string sectionName) => OrderSensitiveTypes.Value.Contains(sectionName);

    private static readonly Lazy<HashSet<string>> OrderSensitiveTypes = new(() =>
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        return typeof(IEntryHandler).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(flags))
            .SelectMany(f => f.GetCustomAttributes<SectionAttribute>().Select(a => (a.Name, Type: a.Type ?? ItemType(f.FieldType))))
            .Where(s => typeof(IEntryHandler).IsAssignableFrom(s.Type))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>The section class of a <c>[Section]</c> field: the field type, or the item type of a list.</summary>
    private static Type ItemType(Type fieldType) =>
        fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>)
            ? fieldType.GetGenericArguments()[0]
            : fieldType;
}
