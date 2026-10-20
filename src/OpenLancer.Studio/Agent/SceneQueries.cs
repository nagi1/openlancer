using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Studio.Agent;

/// <summary>A star system of the edit view (tool <c>list_systems</c>).</summary>
/// <param name="Ref">The <c>system:</c> reference.</param>
/// <param name="Name">The name that the game shows, or an empty text.</param>
/// <param name="File">The install path of the system file, or null when the <c>[system]</c> names no file.</param>
internal sealed record SystemRow(string Ref, string Nickname, string Name, string? File, ItemOrigin Origin);

/// <summary>A line of a section: its key and its values.</summary>
internal sealed record LineRow(string Key, JsonArray Values);

/// <summary>A jump gate, a jump hole or a trade lane ring of a system.</summary>
/// <param name="Kind"><c>gate</c>, <c>hole</c> or <c>lane_ring</c>.</param>
/// <param name="ToSystem">The system of the partner of a gate or a hole, or null.</param>
/// <param name="ToObject">The partner object of a gate or a hole, or null.</param>
/// <param name="Previous">The previous ring of a trade lane ring, or null.</param>
/// <param name="Next">The next ring of a trade lane ring, or null.</param>
internal sealed record ConnectionRow(string Ref, string Kind, string? ToSystem, string? ToObject, string? Previous,
    string? Next);

/// <summary>A star system with its counts, bases, connections and fields (tool <c>get_system</c>).</summary>
internal sealed record SystemView(string Ref, string Name, string? File)
{
    /// <summary>The lines of the <c>[system]</c> section of <c>universe.ini</c>.</summary>
    public IReadOnlyList<LineRow> Fields { get; init; } = [];

    /// <summary>The number of items of each kind: object, zone, light, nebula, field.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();

    /// <summary>The number of objects of each object kind, for example station or gate.</summary>
    public IReadOnlyDictionary<string, int> ObjectKinds { get; init; } = new Dictionary<string, int>();

    /// <summary>The <c>base:</c> references of the bases that objects of the system name.</summary>
    public IReadOnlyList<string> Bases { get; init; } = [];

    public IReadOnlyList<ConnectionRow> Connections { get; init; } = [];

    /// <summary>The <c>nebula:</c> references.</summary>
    public IReadOnlyList<string> Nebulae { get; init; } = [];

    /// <summary>The <c>field:</c> references of the asteroid fields.</summary>
    public IReadOnlyList<string> AsteroidFields { get; init; } = [];

    public IReadOnlyList<Problem> Problems { get; init; } = [];
}

/// <summary>An item of a system (tool <c>list_items</c>).</summary>
/// <param name="Ref">The canonical reference, for example <c>obj:</c> or <c>zone:</c>.</param>
/// <param name="Nickname">The nickname, or the zone of a nebula or an asteroid field.</param>
/// <param name="Kind"><c>object</c>, <c>zone</c>, <c>light</c>, <c>nebula</c> or <c>field</c>.</param>
internal sealed record SceneItemRow(string Ref, string? Nickname, string Name, string Kind)
{
    /// <summary>The object kind of an object, for example <c>station</c> or <c>wreck</c>, else null.</summary>
    public string? ObjectKind { get; init; }

    public string? Archetype { get; init; }

    /// <summary>The position in meters, as the <c>pos</c> line gives it.</summary>
    public float[]? Pos { get; init; }

    /// <summary>The rotation in degrees about X, Y and Z, as the <c>rotate</c> line gives it.</summary>
    public float[]? Rotate { get; init; }

    /// <summary>The size of a zone, as the <c>size</c> line gives it.</summary>
    public float[]? Size { get; init; }

    public IReadOnlyList<ObjectComponent> Components { get; init; } = [];

    public required ItemOrigin Origin { get; init; }
}

/// <summary>The query tools of the star systems and their items (section 7.1 of the MCP1 design).</summary>
internal static class SceneQueries
{
    /// <summary>The kinds of the items of a system, for the argument <c>kinds</c>.</summary>
    public static readonly IReadOnlyList<string> ItemKinds = ["object", "zone", "light", "nebula", "field"];

    /// <summary>The object kinds, for the argument <c>object_kinds</c>.</summary>
    public static readonly IReadOnlyList<string> ObjectKindNames =
        ["station", "planet", "sun", "satellite", "wreck", "gate", "hole", "lane_ring", "other"];

    public static ToolStep ListSystems(ToolCall call)
    {
        var session = call.Session;
        var text = call.Args.Text("text");
        var entries = session.Lists.List("system")?.Entries.Where(e => e.Section != null).ToList() ?? [];
        var rows = entries.Select(e => (Entry: e, Name: call.Texts.TextOf(e.Section!, "strid_name")))
            .Where(r => ToolCall.Matches(text, r.Entry.Value, r.Name))
            .ToList();
        return ToolResult.Of(call.Paged(rows, r => new SystemRow(
            ItemRefs.Format(new ItemReference(ItemRefKind.System, r.Entry.Value)), r.Entry.Value, r.Name,
            session.SystemPath(r.Entry.Value),
            new ItemOrigin(r.Entry.Origin, r.Entry.ModId, session.IsUnsaved(r.Entry.Section!)))));
    }

    public static ToolStep GetSystem(ToolCall call)
    {
        var session = call.Session;
        var (_, section, path) = System(session, call.Args.Text("system")!);
        var view = session.Get(section)!;
        var items = path == null ? [] : Items(session, path).ToList();
        var kinds = new ObjectKinds(session);
        var objects = items.Where(i => i.Kind == SceneKind.Object)
            .Select(i => (i.Section, View: session.Get(i.Section)!))
            .Select(o => (o.Section, o.View, Kind: kinds.Of(o.View)))
            .ToList();
        string Ref(SectionRef s) => ItemRefs.Canonical(session, s);
        return ToolResult.Of(new SystemView(Ref(section), call.Texts.TextOf(section, "strid_name"), path)
        {
            Fields = view.Entries.Select(e => new LineRow(e.Name, AgentRows.Values(e.Values))).ToList(),
            Counts = ItemKinds.ToDictionary(k => k, k => items.Count(i => KindName(i.Kind) == k)),
            ObjectKinds = objects.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.Count()),
            Bases = objects.Select(o => SceneSections.Last(o.View, "base")).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(b => ItemRefs.Format(new ItemReference(ItemRefKind.Base, b))).ToList(),
            Connections = objects.Where(o => o.Kind is "gate" or "hole" or "lane_ring")
                .Select(o => Connection(session, o.Section, o.View, o.Kind)).ToList(),
            Nebulae = items.Where(i => i.Kind == SceneKind.Nebula).Select(i => Ref(i.Section)).ToList(),
            AsteroidFields = items.Where(i => i.Kind == SceneKind.Asteroids).Select(i => Ref(i.Section)).ToList(),
            Problems = AgentRows.ProblemsOf(session, section),
        });
    }

    public static ToolStep ListItems(ToolCall call)
    {
        var session = call.Session;
        var args = call.Args;
        var (_, _, path) = System(session, args.Text("system")!);
        var kinds = args.Choices("kinds");
        var objectKinds = args.Choices("object_kinds");
        var text = args.Text("text");
        var origin = args.Text("origin");
        var near = args.Group("near");
        var point = near?.Point("point");
        var radius = near?.Number("radius");
        var kindsOf = new ObjectKinds(session);
        // A filter reads only what it needs, and the rows of the page read the rest. So a list of a large system
        // stays in one frame of the editor.
        var rows = new List<(SectionRef Section, SceneKind Kind)>();
        foreach (var (section, kind) in path == null ? [] : Items(session, path))
        {
            if (kinds != null && !kinds.Contains(KindName(kind)))
                continue;
            SectionView? view = null;
            SectionView View() => view ??= session.Get(section)!;
            if (objectKinds != null && (kind != SceneKind.Object || !objectKinds.Contains(kindsOf.Of(View()))))
                continue;
            if (text != null && !ToolCall.Matches(text, section.Id.Key,
                    call.Texts.NameOf(Named(session, section, kind) ?? section), SceneSections.Last(View(), "archetype")))
                continue;
            if (point is { } p && (AgentRows.Numbers(View(), "pos") is not [var x, var y, var z] ||
                                   Vector3.Distance(p, new Vector3(x, y, z)) > radius))
                continue;
            if (origin != null && AgentTools.Snake(Origin(session, section, View(), kind).Origin.ToString()) != origin)
                continue;
            rows.Add((section, kind));
        }
        return ToolResult.Of(call.Paged(rows, r =>
        {
            var view = session.Get(r.Section)!;
            var isObject = r.Kind == SceneKind.Object;
            return new SceneItemRow(ItemRefs.Canonical(session, r.Section), r.Section.Id.Key,
                call.Texts.NameOf(Named(session, r.Section, r.Kind) ?? r.Section), KindName(r.Kind))
            {
                ObjectKind = isObject ? kindsOf.Of(view) : null,
                Archetype = isObject ? SceneSections.Last(view, "archetype") : null,
                Pos = AgentRows.Numbers(view, "pos"),
                Rotate = AgentRows.Numbers(view, "rotate"),
                Size = r.Kind == SceneKind.Zone ? AgentRows.Numbers(view, "size") : null,
                Components = isObject ? ObjectComponents.Of(session, r.Section).Select(c => c.Kind).ToList() : [],
                Origin = Origin(session, r.Section, view, r.Kind),
            };
        }));
    }

    /// <summary>The <c>[system]</c> of a nickname, with its file.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/> with the closest names.</exception>
    internal static (string Nickname, SectionRef Section, string? Path) System(EditSession session, string nickname)
    {
        var reference = new ItemReference(ItemRefKind.System, nickname);
        var section = ItemRefs.ResolveSection(session, reference);
        var key = section.Id.Key ?? nickname;
        return (key, section, session.SystemPath(key));
    }

    /// <summary>The items of a system file in file order, with their kinds (see <see cref="SceneSections.KindOf"/>).</summary>
    internal static IEnumerable<(SectionRef Section, SceneKind Kind)> Items(EditSession session, string path)
    {
        if (!session.IsSystemFile(path))
            yield break;
        foreach (var section in session.Sections(path))
        {
            SceneKind? kind = section.Id.Type.ToLowerInvariant() switch
            {
                "object" => SceneKind.Object,
                "zone" => SceneKind.Zone,
                "lightsource" => SceneKind.Light,
                "nebula" => SceneKind.Nebula,
                "asteroids" => SceneKind.Asteroids,
                _ => null,
            };
            if (kind is { } found)
                yield return (section, found);
        }
    }

    /// <summary>The name of a kind of item for the results.</summary>
    internal static string KindName(SceneKind kind) => kind switch
    {
        SceneKind.Object => "object",
        SceneKind.Zone => "zone",
        SceneKind.Light => "light",
        SceneKind.Nebula => "nebula",
        _ => "field",
    };

    /// <summary>
    /// The object kinds of the objects of one call. An object of a connection type, or with a <c>goto</c>, a
    /// <c>prev_ring</c> or a <c>next_ring</c> line, is a connection (decision D253). Else the kind follows the template
    /// of the archetype type. A <c>mission_satellite</c> is a wreck: retail gives wrecks this type (section 7.3 of the
    /// MCP1 design). The archetype types come from the list once for each archetype of the call.
    /// </summary>
    internal sealed class ObjectKinds(EditSession session)
    {
        private readonly Dictionary<string, string?> types = new(StringComparer.OrdinalIgnoreCase);
        private readonly ListView? archetypes = session.Lists.List("solar_archetype");

        public string Of(SectionView view)
        {
            var type = TypeOf(view);
            if (type != null && session.Lists.RuleData.ConnectionTypes.Contains(type))
            {
                return type switch
                {
                    "jump_gate" or "airlock_gate" => "gate",
                    "jump_hole" or "jumphole" => "hole",
                    _ => "lane_ring",
                };
            }
            if (view.Lines("goto").Count > 0)
                return "gate";
            if (view.Lines("prev_ring").Count > 0 || view.Lines("next_ring").Count > 0)
                return "lane_ring";
            return type switch
            {
                "station" or "docking_ring" => "station",
                "planet" => "planet",
                "sun" => "sun",
                "satellite" or "waypoint" => "satellite",
                "mission_satellite" => "wreck",
                _ => "other",
            };
        }

        /// <summary>The <c>[Solar] type</c> of the archetype of an object in lowercase, or null.</summary>
        private string? TypeOf(SectionView view)
        {
            if (SceneSections.Last(view, "archetype") is not { } archetype)
                return null;
            if (!types.TryGetValue(archetype, out var type))
            {
                type = archetypes?.Find(archetype)?.Section is { } solar && session.Get(solar) is { } section
                    ? SceneSections.Last(section, "type")?.ToLowerInvariant()
                    : null;
                types[archetype] = type;
            }
            return type;
        }
    }

    /// <summary>The section that gives the name of an item: the zone of a nebula or a field, else the item.</summary>
    private static SectionRef? Named(EditSession session, SectionRef section, SceneKind kind) =>
        kind is SceneKind.Nebula or SceneKind.Asteroids && section.Id.Key is { } zone
            ? session.Nicknames.All.FirstOrDefault(n => n.IsZone && n.Name.Equals(zone, StringComparison.OrdinalIgnoreCase))?.Section
            : null;

    private static ItemOrigin Origin(EditSession session, SectionRef section, SectionView view, SceneKind kind) =>
        AgentRows.OriginOf(session, section, view, kind is SceneKind.Nebula or SceneKind.Asteroids ? "zone" : "nickname");

    private static ConnectionRow Connection(EditSession session, SectionRef section, SectionView view, string kind)
    {
        var go = view.Lines("goto").LastOrDefault();
        return new ConnectionRow(ItemRefs.Canonical(session, section), kind,
            go is { Values.Count: >= 1 } ? go.Values[0].ToString().Trim() : null,
            go is { Values.Count: >= 2 } ? go.Values[1].ToString().Trim() : null,
            SceneSections.Last(view, "prev_ring"), SceneSections.Last(view, "next_ring"));
    }
}
