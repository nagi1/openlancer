using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Hierarchy;

/// <summary>One row of the Hierarchy tree: the system, a group, a trade lane, a zone role or an item.</summary>
internal sealed class HierarchyNode
{
    private readonly List<HierarchyNode> children = [];

    /// <param name="id">A stable id, unique in the tree. It is also the ImGui id of the row.</param>
    /// <param name="item">The item that a click selects, or null for a group.</param>
    public HierarchyNode(string id, ObjectKind kind, string name, string detail = "", object? item = null,
        string? tooltip = null)
    {
        Id = id;
        Label = "##" + id;
        Kind = kind;
        Name = name;
        Detail = detail;
        Item = item;
        Tooltip = tooltip;
    }

    public string Id { get; }

    /// <summary>The ImGui label of the row. The row draws its text itself, so the label has only the id.</summary>
    public string Label { get; }

    public ObjectKind Kind { get; }

    /// <summary>The main text of the row.</summary>
    public string Name { get; }

    /// <summary>A dim text after the name, for example the nickname. Empty for no text.</summary>
    public string Detail { get; }

    /// <summary>The item that a click selects, or null for a group that only opens and closes.</summary>
    public object? Item { get; }

    public string? Tooltip { get; }

    public HierarchyNode? Parent { get; private set; }

    public IReadOnlyList<HierarchyNode> Children => children;

    /// <summary>The number of items below this node.</summary>
    public int Total { get; private set; }

    /// <summary>The number of items below this node that match the search, or 1 or 0 for an item.</summary>
    public int Matches { get; internal set; }

    /// <summary>"14", or "3 / 14" during a search. Empty for an item and for the system row.</summary>
    public string CountText { get; internal set; } = "";

    public void Add(HierarchyNode child)
    {
        child.Parent = this;
        children.Add(child);
    }

    /// <summary>Counts the items below each node. Call it once, after the tree is complete.</summary>
    internal int CountItems()
    {
        Total = children.Count == 0 ? (Item != null ? 1 : 0) : children.Sum(c => c.CountItems());
        return Total;
    }
}

/// <summary>One visible row of the Hierarchy, with its tree depth.</summary>
internal readonly record struct HierarchyRow(HierarchyNode Node, int Depth);

/// <summary>
/// The Hierarchy tree of one system, the open state of its nodes and the search. The tree is built once for each
/// system. The search is applied again only when its text changes. The visible rows are a flat list, so the panel can
/// clip them and the keyboard can move along them, also to rows that are not on the screen.
/// </summary>
internal sealed class HierarchyModel
{
    /// <summary>
    /// The types that a "t:" word in the search selects. Each one tests the kind of an item and the item. A zone has
    /// the kind of its role.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Func<ObjectKind, object?, bool>> Types =
        new Dictionary<string, Func<ObjectKind, object?, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sun"] = (k, _) => k == ObjectKinds.Sun,
            ["planet"] = (k, _) => k == ObjectKinds.Planet,
            ["station"] = (k, _) => k == ObjectKinds.Station,
            ["ring"] = (k, _) => k == ObjectKinds.DockingRing,
            ["gate"] = (k, _) => k == ObjectKinds.JumpGate || k == ObjectKinds.AirlockGate,
            ["hole"] = (k, _) => k == ObjectKinds.JumpHole,
            ["lane"] = (k, _) => k == ObjectKinds.TradeLaneRing,
            ["satellite"] = (k, _) => k == ObjectKinds.Satellite,
            ["platform"] = (k, _) => k == ObjectKinds.WeaponsPlatform,
            ["zone"] = (_, item) => item is Zone,
            ["field"] = (k, _) => k == ObjectKinds.Of(ZoneRole.AsteroidField),
            ["nebula"] = (k, _) => k == ObjectKinds.Of(ZoneRole.Nebula),
            ["light"] = (_, item) => item is LightSource,
        };

    // The groups of the system objects, in the order of the tree.
    private static readonly (string Id, string Name, ObjectKind Kind, ObjectKind[] Members)[] ObjectGroups =
    [
        ("suns", "Suns and planets", ObjectKinds.Sun, [ObjectKinds.Sun, ObjectKinds.Planet]),
        ("stations", "Stations", ObjectKinds.Station, [ObjectKinds.Station, ObjectKinds.DockingRing]),
        ("gates", "Gates and holes", ObjectKinds.JumpGate,
            [ObjectKinds.JumpGate, ObjectKinds.AirlockGate, ObjectKinds.JumpHole]),
        ("lanes", "Trade lanes", ObjectKinds.TradeLaneRing, []),
        ("satellites", "Satellites and platforms", ObjectKinds.Satellite,
            [ObjectKinds.Satellite, ObjectKinds.WeaponsPlatform, ObjectKinds.Depot]),
        ("other", "Other objects", ObjectKinds.OtherObject, []),
    ];

    private static readonly Dictionary<ZoneRole, string> RoleNames = new()
    {
        [ZoneRole.AsteroidField] = "Asteroid fields",
        [ZoneRole.Nebula] = "Nebulae",
        [ZoneRole.Exclusion] = "Exclusion zones",
        [ZoneRole.Population] = "Population zones",
        [ZoneRole.PatrolPath] = "Patrol paths",
        [ZoneRole.TradeLane] = "Trade lane zones",
        [ZoneRole.Other] = "Other zones",
    };

    private readonly Func<int, string> name;
    private readonly Dictionary<object, HierarchyNode> nodes = new(ReferenceEqualityComparer.Instance);
    private readonly List<HierarchyNode> items = [];

    // The open groups without a search. Groups are closed by default, and the root is open.
    private readonly HashSet<HierarchyNode> open = [];

    // The groups that the user closed during the current search. During a search, groups with matches are open.
    private readonly HashSet<HierarchyNode> closedInSearch = [];

    private List<HierarchyRow>? rows;
    private string[] words = [];
    private Func<ObjectKind, object?, bool>[] types = [];

    /// <param name="name">The text of a string resource id, or an empty string when the id is 0 or unknown.</param>
    public HierarchyModel(SystemIndex index, Func<int, string> name)
    {
        Index = index;
        this.name = name;
        var system = index.System;
        var source = index.Source;
        Root = new HierarchyNode("root", ObjectKinds.StarSystem,
            name(source.IdsName) is { Length: > 0 } systemName ? systemName : source.Nickname, source.Nickname, source);
        open.Add(Root);

        // The rings of the lanes are in the lane group. Each other object is in the group of its kind, or in the
        // group of other objects when no group has its kind.
        var laneRings = new HashSet<SystemObject>(index.Lanes.SelectMany(l => l.Rings),
            ReferenceEqualityComparer.Instance);
        var groupedKinds = ObjectGroups.SelectMany(g => g.Members).ToHashSet();
        foreach (var (id, groupName, kind, members) in ObjectGroups)
        {
            var group = new HierarchyNode(id, kind, groupName);
            if (id == "lanes")
            {
                foreach (var lane in index.Lanes.Select(Lane).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
                {
                    group.Add(lane);
                }
            }
            else
            {
                bool InGroup(ObjectKind objectKind) =>
                    id == "other" ? !groupedKinds.Contains(objectKind) : members.Contains(objectKind);
                AddSorted(group, system.Objects.Where(o => !laneRings.Contains(o) && InGroup(ObjectKinds.Of(o)))
                    .Select(o => Item("o:" + o.Nickname, ObjectKinds.Of(o), o, o.Nickname)));
            }

            AddGroup(group);
        }

        var zones = new HierarchyNode("zones", ObjectKinds.Of(ZoneRole.Other), "Zones");
        foreach (var role in Enum.GetValues<ZoneRole>())
        {
            var roleNode = new HierarchyNode("zones/" + role, ObjectKinds.Of(role), RoleNames[role]);
            AddSorted(roleNode, system.Zones.Where(z => index.ZoneRole(z) == role)
                .Select(z => Item("z:" + z.Nickname, ObjectKinds.Of(role), z, z.Nickname)));
            if (roleNode.Children.Count > 0)
            {
                zones.Add(roleNode);
            }
        }

        AddGroup(zones);
        var lights = new HierarchyNode("lights", ObjectKinds.Light, "Lights");
        AddSorted(lights, system.LightSources.Select(l => Item("l:" + l.Nickname, ObjectKinds.Light, l, l.Nickname)));
        AddGroup(lights);

        Root.CountItems();
        ApplySearch();
    }

    /// <summary>
    /// The number of items of a system that each "t:" word of <see cref="Types"/> selects. The system does not have to
    /// be open.
    /// </summary>
    internal static Dictionary<string, int> TypeCounts(StarSystem system)
    {
        var roles = SystemIndex.ZoneRoles(system);
        List<(ObjectKind Kind, object Item)> items =
        [
            ..system.Objects.Select(o => (ObjectKinds.Of(o), (object)o)),
            ..system.Zones.Select(z => (ObjectKinds.Of(roles.GetValueOrDefault(z, ZoneRole.Other)), (object)z)),
            ..system.LightSources.Select(l => (ObjectKinds.Light, (object)l)),
        ];
        return Types.ToDictionary(t => t.Key, t => items.Count(i => t.Value(i.Kind, i.Item)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The index of the system that the tree shows.</summary>
    public SystemIndex Index { get; }

    /// <summary>The system row. It is the parent of all groups.</summary>
    public HierarchyNode Root { get; }

    /// <summary>The current search text.</summary>
    public string Search { get; private set; } = "";

    /// <summary>True when the search has text, so that only the matches show.</summary>
    public bool IsSearching => words.Length > 0 || types.Length > 0;

    /// <summary>The visible rows, from top to bottom.</summary>
    public IReadOnlyList<HierarchyRow> Rows => rows ??= BuildRows();

    /// <summary>
    /// Sets the search text. The words match the name or the nickname of an item, and case does not matter. A
    /// "t:station" word keeps only the items of that type. Several "t:" words keep the items of any of the types. An
    /// unknown type matches nothing.
    /// </summary>
    public void SetSearch(string text)
    {
        if (text == Search)
        {
            return;
        }

        Search = text;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        words = parts.Where(p => !p.StartsWith("t:", StringComparison.OrdinalIgnoreCase)).ToArray();
        types = parts.Where(p => p.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
            .Select(p => Types.GetValueOrDefault(p[2..], (_, _) => false))
            .ToArray();
        closedInSearch.Clear();
        ApplySearch();
    }

    public bool IsOpen(HierarchyNode node) =>
        IsSearching && node != Root ? !closedInSearch.Contains(node) : open.Contains(node);

    public void SetOpen(HierarchyNode node, bool value)
    {
        if (IsOpen(node) == value)
        {
            return;
        }

        if (IsSearching && node != Root)
        {
            if (value)
            {
                closedInSearch.Remove(node);
            }
            else
            {
                closedInSearch.Add(node);
            }
        }
        else if (value)
        {
            open.Add(node);
        }
        else
        {
            open.Remove(node);
        }

        rows = null;
    }

    /// <summary>The node of an item, or null when the item is not in the tree.</summary>
    public HierarchyNode? NodeOf(object? item) => item != null && nodes.TryGetValue(item, out var node) ? node : null;

    /// <summary>The index of the row of an item in <see cref="Rows"/>, or -1 when the row is not visible.</summary>
    public int IndexOf(object? item)
    {
        if (NodeOf(item) is not { } node)
        {
            return -1;
        }

        var list = Rows;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Node == node)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Opens the parents of the row of an item, so that the row is visible. Returns false when the item is not in the
    /// tree or does not match the search.
    /// </summary>
    public bool Reveal(object? item)
    {
        if (NodeOf(item) is not { } node || node.Matches == 0)
        {
            return false;
        }

        for (var parent = node.Parent; parent != null; parent = parent.Parent)
        {
            SetOpen(parent, true);
        }

        return true;
    }

    /// <summary>
    /// Makes the row of an item: its name and its nickname, or only its nickname when it has no name. The lookup by
    /// item finds the row.
    /// </summary>
    private HierarchyNode Item(string id, ObjectKind kind, object item, string nickname)
    {
        var itemName = name(EditorSession.NameIds(item));
        var node = itemName.Length == 0
            ? new HierarchyNode(id, kind, nickname, "", item)
            : new HierarchyNode(id, kind, itemName, nickname, item);
        nodes[item] = node;
        items.Add(node);
        return node;
    }

    /// <summary>
    /// A trade lane node with its rings in the order of the lane. The label names the two ends of the lane. It uses
    /// the trade lane space names of the end rings, then the names of the end rings, and then their nicknames, because
    /// all rings of a lane often have the same name.
    /// </summary>
    private HierarchyNode Lane(TradeLane lane)
    {
        var (first, last) = (lane.Rings[0], lane.Rings[^1]);
        var label = Ends(name(first.TradelaneSpaceName), name(last.TradelaneSpaceName)) ??
                    Ends(name(first.IdsName), name(last.IdsName)) ??
                    (lane.Rings.Count == 1 ? first.Nickname : $"{first.Nickname} -> {last.Nickname}");
        var rings = lane.Rings.Count == 1 ? "1 ring" : $"{lane.Rings.Count} rings";
        var node = new HierarchyNode("lane:" + first.Nickname, ObjectKinds.TradeLane, label, "", null,
            $"Trade lane from {first.Nickname} to {last.Nickname}, {rings}.");
        foreach (var ring in lane.Rings)
        {
            node.Add(Item("o:" + ring.Nickname, ObjectKinds.Of(ring), ring, ring.Nickname));
        }

        return node;
    }

    /// <summary>"A -> B", or null when a name is empty or both names are the same.</summary>
    private static string? Ends(string first, string last) =>
        first.Length == 0 || last.Length == 0 || first.Equals(last, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{first} -> {last}";

    private void AddGroup(HierarchyNode group)
    {
        // A group without items does not show.
        if (group.Children.Count > 0)
        {
            Root.Add(group);
        }
    }

    /// <summary>Adds the item rows sorted by their text, then by nickname. Case does not matter.</summary>
    private static void AddSorted(HierarchyNode parent, IEnumerable<HierarchyNode> children)
    {
        foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(c => c.Detail, StringComparer.OrdinalIgnoreCase))
        {
            parent.Add(child);
        }
    }

    private bool Matches(HierarchyNode node)
    {
        if (types.Length > 0 && !types.Any(t => t(node.Kind, node.Item)))
        {
            return false;
        }

        return words.All(w => node.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                              node.Detail.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Counts the matches of each node and sets the count texts.</summary>
    private void ApplySearch()
    {
        foreach (var item in items)
        {
            item.Matches = Matches(item) ? 1 : 0;
        }

        Count(Root);
        rows = null;
    }

    private int Count(HierarchyNode node)
    {
        if (node.Children.Count == 0)
        {
            return node.Matches;
        }

        node.Matches = node.Children.Sum(Count);
        // The system row has no count. Its groups show the counts.
        node.CountText = node == Root ? ""
            : IsSearching ? $"{Number(node.Matches)} / {Number(node.Total)}"
            : Number(node.Total);
        return node.Matches;
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private List<HierarchyRow> BuildRows()
    {
        var list = new List<HierarchyRow>();

        void Add(HierarchyNode node, int depth)
        {
            list.Add(new HierarchyRow(node, depth));
            if (!IsOpen(node))
            {
                return;
            }

            foreach (var child in node.Children)
            {
                // During a search, only the nodes with matches show.
                if (!IsSearching || child.Matches > 0)
                {
                    Add(child, depth + 1);
                }
            }
        }

        Add(Root, 0);
        return list;
    }
}
