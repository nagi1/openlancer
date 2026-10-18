using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Data.Editing.Assets;

/// <summary>
/// The static prop archetypes of a model (decisions D94, D278 and D279 of the M23 design). Retail has 57 solar archetypes
/// whose <c>DA_archetype</c> is a ship model, for docking battleships, wrecks and decor ships. So a ship, an asteroid or a
/// model with a user spawns through an existing solar archetype of its model, or through a new one.
/// </summary>
public static class PropArchetypes
{
    /// <summary>The <c>type</c> of a new static prop archetype, as the retail decor ship <c>large_transport</c> has
    /// it.</summary>
    internal const string PropType = "NON_TARGETABLE";

    /// <summary>The lists whose names a new archetype must not have, by name or by FLHash. Some retail code looks up an
    /// archetype id in the ship table, then in the solar table, then in the equipment table (review finding
    /// R11).</summary>
    private static readonly string[] NameLists = ["solar_archetype", "ship", "equipment"];

    /// <summary>
    /// The solar archetypes of W whose <c>DA_archetype</c> names the model of a source, in list order. The paths compare
    /// below the data folder, without letter case, with <c>\</c> and <c>/</c> alike.
    /// </summary>
    public static IReadOnlyList<TemplateArchetype> Existing(EditSession session, ModelSource source)
    {
        if (session.Lists.List("solar_archetype") is not { } list)
            return [];
        var users = session.Assets.Users.Of(source.Model)
            .Where(u => u.Kind == AssetKind.SolarArchetype).Select(u => u.Section).ToHashSet();
        return list.Entries.Where(e => e.Section != null && users.Contains(e.Section))
            .Select(e => new TemplateArchetype(e.Value, ScenePlans.TypeOf(session, e.Value) ?? "", e))
            .ToList();
    }

    /// <summary>
    /// The nickname of a new static prop archetype (decision D279): <c>&lt;stem&gt;_prop</c>, then <c>_prop_2</c> and on.
    /// No entry of the solar archetypes, the ships or the equipment has it by name or by FLHash.
    /// </summary>
    /// <param name="stem">The nickname of the ship or the asteroid, or the file name of the model.</param>
    public static string Nickname(EditSession session, string stem)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<uint>();
        foreach (var id in NameLists)
        {
            foreach (var entry in session.Lists.List(id)?.Entries ?? [])
            {
                names.Add(entry.Value);
                hashes.Add(FLHash.CreateID(entry.Value));
            }
        }
        var root = stem.Trim().ToLowerInvariant() + "_prop";
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? root : $"{root}_{n}";
            if (!names.Contains(name) && !hashes.Contains(FLHash.CreateID(name)))
                return name;
        }
    }

    /// <summary>
    /// The lines of a new static prop archetype (decision D279): <c>nickname</c> and <c>type = NON_TARGETABLE</c>, then
    /// the <c>DA_archetype</c>, each <c>material_library</c>, the <c>envmap_material</c> and the <c>LODranges</c> of the
    /// source with the values of the source. No <c>hit_pts</c> and no <c>destructible</c> line: retail reads a missing
    /// <c>destructible</c> as false, so the prop cannot be destroyed.
    /// </summary>
    /// <returns>The lines, or null when the file cannot hold the nickname. The error then tells why.</returns>
    internal static List<EntryView>? Lines(ModelSource source, string nickname, bool binary, out string? error)
    {
        if (ScenePlans.Entries([("nickname", nickname), ("type", PropType)], binary, out error) is not { } head)
            return null;
        return [.. head, .. source.Lines.Select(l => new EntryView(l.Name, l.Values.ToList()))];
    }
}
