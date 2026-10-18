using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing.Assets;

/// <summary>
/// The model of an asset in W (section 5.5 of the M23 design): the model file of <c>DA_archetype</c>, the files of each
/// <c>material_library</c> and the <c>envmap_material</c>, as the engine reads them for a solar archetype. The thumbnail
/// renders it, and a new static prop archetype copies its lines (decision D279).
/// </summary>
/// <param name="Model">The model file below the data folder, as the line names it, for example
/// <c>ships\liberty\li_elite\li_elite.cmp</c>.</param>
/// <param name="Libraries">The files of the <c>material_library</c> lines below the data folder, in line order.</param>
/// <param name="EnvMapMaterial">The material of the environment map, or null.</param>
/// <param name="Lines">The lines that a static prop archetype copies, with the values of the source: the
/// <c>DA_archetype</c> line, each <c>material_library</c> line, and the <c>envmap_material</c> and <c>LODranges</c>
/// lines, in that order. A value of a binary source keeps its type, as Duplicate keeps it.</param>
/// <param name="From">The section that gives the lines, or null.</param>
public sealed record ModelSource(string Model, IReadOnlyList<string> Libraries, string? EnvMapMaterial,
    IReadOnlyList<EntryView> Lines, SectionRef? From)
{
    /// <summary>The key of the model file: its path below the data folder in lowercase with <c>\</c>, so the spellings
    /// of one file give one key. Retail writes <c>Ships\</c> and <c>ships\</c>.</summary>
    public string Key => ModelUsers.Key(Model) ?? Model.ToLowerInvariant();

    /// <summary>
    /// The model of a section of W: a <c>[Solar]</c>, a <c>[Ship]</c>, an <c>[Asteroid]</c> or equipment. Null when the
    /// section has no <c>DA_archetype</c> line with a value.
    /// </summary>
    public static ModelSource? Of(EditSession session, SectionRef section)
    {
        if (session.Get(section) is not { } view || Last(view, "DA_archetype") is not { } model)
            return null;
        var lines = new List<EntryView> { model };
        var libraries = view.Lines("material_library").Where(l => l.Values.Count > 0).ToList();
        lines.AddRange(libraries);
        var envMap = Last(view, "envmap_material");
        if (envMap != null)
            lines.Add(envMap);
        if (Last(view, "LODranges") is { } lod)
            lines.Add(lod);
        return new ModelSource(Text(model), libraries.Select(Text).ToList(), envMap == null ? null : Text(envMap), lines,
            section);
    }

    /// <summary>The last line of a key with a value. The engine reads the last line of a key that is not
    /// multiline.</summary>
    private static EntryView? Last(SectionView view, string key) => view.Lines(key).LastOrDefault(l => l.Values.Count > 0);

    private static string Text(EntryView line) => line.Values[0].ToString().Trim();
}

/// <summary>A section of W that names a model file in its <c>DA_archetype</c> line.</summary>
/// <param name="Kind">The kind of the section: a solar archetype, a ship, an asteroid or equipment.</param>
public sealed record ModelUser(SectionRef Section, AssetKind Kind);

/// <summary>
/// The sections of W that name each model file in <c>DA_archetype</c>: the solar archetypes, the ships, the asteroids and
/// the equipment (section 5.5 of the M23 design). The paths compare below the data folder, without letter case, with
/// <c>\</c> and <c>/</c> alike. The index builds again only when one of the four lists changes.
/// </summary>
internal sealed class ModelUsers(EditSession session)
{
    /// <summary>The lists of the users, in the order that the first user of a model file comes from.</summary>
    private static readonly (string List, AssetKind Kind)[] Sources =
    [
        ("solar_archetype", AssetKind.SolarArchetype), ("ship", AssetKind.Ship), ("asteroid", AssetKind.Asteroid),
        ("equipment", AssetKind.Equipment),
    ];

    private object?[] views = [];
    private Dictionary<string, List<ModelUser>> byModel = [];

    /// <summary>An object that changes each time the index builds again.</summary>
    public object State
    {
        get
        {
            Refresh();
            return byModel;
        }
    }

    /// <summary>The key of a model file (see <see cref="ModelSource.Key"/>), or null for a path that leaves the
    /// install root.</summary>
    public static string? Key(string model) => ThnReferences.PathOf(model.Trim())?.ToLowerInvariant();

    /// <summary>The users of a model file, in the order of <see cref="Sources"/> and then in list order.</summary>
    public IReadOnlyList<ModelUser> Of(string model)
    {
        Refresh();
        return Key(model) is { } key ? byModel.GetValueOrDefault(key) ?? [] : [];
    }

    private void Refresh()
    {
        var now = Sources.Select(s => (object?)session.Lists.List(s.List)).ToArray();
        if (now.Length == views.Length && now.Zip(views).All(p => ReferenceEquals(p.First, p.Second)))
            return;
        views = now;
        var map = new Dictionary<string, List<ModelUser>>();
        for (var i = 0; i < Sources.Length; i++)
        {
            if (now[i] is not Lists.ListView list)
                continue;
            foreach (var entry in list.Entries)
            {
                if (entry.Section is not { } section || session.WorkingSection(section) is not { } working ||
                    working.LastOrDefault(e => SceneSections.Is(e.Name, "DA_archetype") && e.Count > 0) is not { } line ||
                    Key(line[0].ToString()) is not { } key)
                    continue;
                if (!map.TryGetValue(key, out var users))
                    map[key] = users = [];
                users.Add(new ModelUser(section, Sources[i].Kind));
            }
        }
        byModel = map;
    }
}
