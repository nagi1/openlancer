using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Hierarchy;

/// <summary>One row of the Hierarchy tree: the system or the cutscene, a group, a trade lane, a zone role or an
/// item.</summary>
internal sealed class HierarchyNode
{
    private readonly List<HierarchyNode> children = [];

    /// <param name="id">A stable id, unique in the tree. It is also the ImGui id of the row.</param>
    /// <param name="item">The item that a click selects, or null for a group.</param>
    /// <param name="search">More text that the search matches (see <see cref="Search"/>).</param>
    public HierarchyNode(string id, ObjectKind kind, string name, string detail = "", object? item = null,
        string? tooltip = null, string search = "")
    {
        Id = id;
        Label = "##" + id;
        Kind = kind;
        Name = name;
        Detail = detail;
        Item = item;
        Tooltip = tooltip;
        Search = search;
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

    /// <summary>More text that the search matches, but that the row does not show. An example is the words of the
    /// groups of a row of the cutscene list. Empty for no text.</summary>
    public string Search { get; }

    public HierarchyNode? Parent { get; private set; }

    public IReadOnlyList<HierarchyNode> Children => children;

    /// <summary>The number of items below this node.</summary>
    public int Total { get; private set; }

    /// <summary>The number of items below this node that match the search, or 1 or 0 for an item.</summary>
    public int Matches { get; internal set; }

    /// <summary>"14", or "3 / 14" during a search. Empty for an item and for the system row.</summary>
    public string CountText { get; internal set; } = "";

    /// <summary>The mark of the worst problem of a row of a mission, or none.</summary>
    public RowProblem Problem { get; init; }

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

/// <summary>The mark of a row whose section the active mod changes or adds (decision D245 of the M3 design).</summary>
internal enum RowMark
{
    None,

    /// <summary>The active mod changes the section: a blue bar, as Unity marks a prefab override.</summary>
    Changed,

    /// <summary>The active mod adds the section: a blue bar and a "+", as Unity marks an added object.</summary>
    Added,
}

/// <summary>
/// The Hierarchy tree of one system or of one cutscene, the open state of its nodes and the search. The tree is built
/// again when the system, a name or the cutscene changes, and it keeps the open state of the tree before it. The search
/// is applied again only when its text changes. The visible rows are a flat list, so the panel can clip them and the
/// keyboard can move along them, also to rows that are not on the screen.
/// </summary>
/// <remarks>An item compares by its equality, so a value such as a cutscene element can be an item.</remarks>
internal sealed partial class HierarchyModel
{
    /// <summary>
    /// The types that a "t:" word in the search selects. Each one tests the kind of an item and the item. A zone has
    /// the kind of its role. "t:field" and "t:nebula" select the asteroid fields and the nebulae, and "t:zone" selects
    /// the zones (milestone M22).
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
            ["field"] = (_, item) => item is AsteroidField,
            ["nebula"] = (_, item) => item is Nebula,
            ["light"] = (_, item) => item is LightSource,
        };

    /// <summary>The types that a "t:" word selects in the tree of a cutscene: the entity types of the
    /// Timeline.</summary>
    internal static readonly IReadOnlyDictionary<string, Func<ObjectKind, object?, bool>> CutsceneTypes =
        new Dictionary<string, Func<ObjectKind, object?, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["camera"] = (k, _) => k == ObjectKinds.Thn.Camera,
            ["monitor"] = (k, _) => k == ObjectKinds.Thn.Monitor,
            ["character"] = (k, _) => k == ObjectKinds.Thn.Character,
            ["prop"] = (k, _) => k == ObjectKinds.Thn.Prop,
            ["light"] = (k, _) => k == ObjectKinds.Thn.Light,
            ["effect"] = (k, _) => k == ObjectKinds.Thn.Effect,
            ["sound"] = (k, _) => k == ObjectKinds.Thn.Sound,
            ["marker"] = (k, _) => k == ObjectKinds.Thn.Marker,
            ["path"] = (k, _) => k == ObjectKinds.Thn.Path,
            ["scene"] = (k, _) => k == ObjectKinds.Thn.Scene,
            ["other"] = (k, _) => k == ObjectKinds.Thn.Other,
        };

    /// <summary>The types of a plain tree: none, so a "t:" word matches nothing.</summary>
    private static readonly IReadOnlyDictionary<string, Func<ObjectKind, object?, bool>> NoTypes =
        new Dictionary<string, Func<ObjectKind, object?, bool>>();

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
        [ZoneRole.AsteroidField] = "Field zones",
        [ZoneRole.Nebula] = "Nebula zones",
        [ZoneRole.Exclusion] = "Exclusion zones",
        [ZoneRole.Population] = "Population zones",
        [ZoneRole.PatrolPath] = "Patrol paths",
        [ZoneRole.TradeLane] = "Trade lane zones",
        [ZoneRole.Other] = "Other zones",
    };

    private readonly IReadOnlyDictionary<string, Func<ObjectKind, object?, bool>> typeWords;
    private readonly Dictionary<object, HierarchyNode> nodes = [];
    private readonly List<HierarchyNode> items = [];

    // The open groups without a search. Groups are closed by default, and the root is open.
    private readonly HashSet<HierarchyNode> open = [];

    // The groups that the user closed during the current search. During a search, groups with matches are open.
    private readonly HashSet<HierarchyNode> closedInSearch = [];

    private List<HierarchyRow>? rows;
    private string[] words = [];
    private Func<ObjectKind, object?, bool>[] types = [];

    /// <param name="text">The text of a text key of an item, for example <c>ids_name</c>, or an empty string (see
    /// <see cref="EditorSession.TextOf"/>).</param>
    /// <param name="previous">The tree before this one, or null. When it shows the same system, the new tree keeps its
    /// open groups and its search.</param>
    public HierarchyModel(SystemIndex index, Func<object, string, string> text, HierarchyModel? previous = null)
        : this(SystemTree(index, text), index.System, Types, previous)
    {
        Index = index;
    }

    /// <param name="root">The root of the tree. Each node with an item is a row that a click selects.</param>
    /// <param name="identity">What the tree shows. A new tree with the same identity as the tree before keeps its open
    /// groups and its search.</param>
    /// <param name="typeWords">The types of the "t:" words of the search.</param>
    private HierarchyModel(HierarchyNode root, object identity,
        IReadOnlyDictionary<string, Func<ObjectKind, object?, bool>> typeWords, HierarchyModel? previous)
    {
        Root = root;
        Identity = identity;
        this.typeWords = typeWords;
        open.Add(Root);
        Register(Root);
        Root.CountItems();
        ApplySearch();
        if (previous != null && Equals(previous.Identity, identity))
        {
            KeepState(previous);
        }
    }

    /// <summary>
    /// The tree of a cutscene: the cutscene row, then the entities in the groups of the Timeline and in its order. The
    /// monitors are first in the cameras. A tree of the same timeline is the tree before. A new tree keeps the open
    /// groups and the search of the tree before for the same cutscene.
    /// </summary>
    public static HierarchyModel ForCutscene(TimelineModel timeline, HierarchyModel? previous) =>
        previous != null && ReferenceEquals(previous.Timeline, timeline)
            ? previous
            : new HierarchyModel(CutsceneTree(timeline), timeline.Path.ToUpperInvariant(), CutsceneTypes, previous)
            {
                Timeline = timeline,
            };

    /// <summary>
    /// A plain tree that the caller makes, for example the cutscene list of the Cutscene toolbar (decision D611 of the
    /// cutscene categories design). The search has no "t:" words, so a "t:" word matches nothing.
    /// </summary>
    /// <param name="root">The root of the tree. Each node with an item is a row that a click selects.</param>
    /// <param name="identity">What the tree shows. A new tree with the same identity as the tree before keeps its open
    /// groups and its search.</param>
    /// <param name="previous">The tree before this one, or null.</param>
    public static HierarchyModel ForList(HierarchyNode root, object identity, HierarchyModel? previous) =>
        new(root, identity, NoTypes, previous);

    /// <summary>
    /// The tree of the scene: the tree before when the index and the names did not change, or else a new tree that keeps
    /// the open groups and the search of the tree before for the same system.
    /// </summary>
    /// <param name="namesVersion">The names version of the scene (see <see cref="SceneView.NamesVersion"/>). A new
    /// version means that a name changed.</param>
    /// <param name="previous">The tree before this one, or null.</param>
    public static HierarchyModel For(SystemIndex index, int namesVersion, Func<object, string, string> text,
        HierarchyModel? previous) =>
        previous != null && ReferenceEquals(previous.Index, index) && previous.NamesVersion == namesVersion
            ? previous
            : new HierarchyModel(index, text, previous) { NamesVersion = namesVersion };

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
            ..system.Nebulae.Select(n => (ObjectKinds.Nebula, (object)n)),
            ..system.AsteroidFields.Select(f => (ObjectKinds.AsteroidField, (object)f)),
        ];
        return Types.ToDictionary(t => t.Key, t => items.Count(i => t.Value(i.Kind, i.Item)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The mark of the row of an item whose section of W is <paramref name="view"/>, or none for no
    /// section.</summary>
    internal static RowMark MarkOf(Data.Editing.SectionView? view) =>
        view == null ? RowMark.None : view.IsNew ? RowMark.Added : view.IsChanged ? RowMark.Changed : RowMark.None;

    /// <summary>The index of the system that the tree shows, or null for the tree of a cutscene.</summary>
    public SystemIndex? Index { get; }

    /// <summary>The timeline that the tree of a cutscene shows, or null for the tree of a system.</summary>
    public TimelineModel? Timeline { get; private init; }

    /// <summary>What the tree shows: the system, or the install path of the cutscene without letter case.</summary>
    public object Identity { get; }

    /// <summary>The names version of the scene that the tree shows (see <see cref="For"/>).</summary>
    public int NamesVersion { get; private init; }

    /// <summary>The system row or the cutscene row. It is the parent of all groups.</summary>
    public HierarchyNode Root { get; }

    /// <summary>The current search text.</summary>
    public string Search { get; private set; } = "";

    /// <summary>True when the search has text, so that only the matches show.</summary>
    public bool IsSearching => words.Length > 0 || types.Length > 0;

    /// <summary>The visible rows, from top to bottom.</summary>
    public IReadOnlyList<HierarchyRow> Rows => rows ??= BuildRows();

    /// <summary>
    /// Sets the search text. The words match the name, the nickname or the search text of an item (see
    /// <see cref="HierarchyNode.Search"/>), and case does not matter. A "t:station" word keeps only the items of that
    /// type. Several "t:" words keep the items of any of the types. An unknown type matches nothing.
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
            .Select(p => typeWords.GetValueOrDefault(p[2..], (_, _) => false))
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
    /// Takes the search and the open groups of the tree before this one. A node has the same id in both trees, also
    /// when its name changed.
    /// </summary>
    private void KeepState(HierarchyModel previous)
    {
        SetSearch(previous.Search);
        var byId = new Dictionary<string, HierarchyNode>();

        void Add(HierarchyNode node)
        {
            byId[node.Id] = node;
            foreach (var child in node.Children)
            {
                Add(child);
            }
        }

        Add(Root);
        open.Clear();
        open.UnionWith(previous.open.Select(n => byId.GetValueOrDefault(n.Id)).OfType<HierarchyNode>());
        closedInSearch.UnionWith(previous.closedInSearch.Select(n => byId.GetValueOrDefault(n.Id))
            .OfType<HierarchyNode>());
        rows = null;
    }

    /// <summary>Records the node of each item below a node, so that the lookup by item finds the row.</summary>
    private void Register(HierarchyNode node)
    {
        if (node.Item != null)
        {
            // An item can have more than one row, for example a ship in Cast and in a label. Its first row is its row.
            nodes.TryAdd(node.Item, node);
            items.Add(node);
        }

        foreach (var child in node.Children)
        {
            Register(child);
        }
    }

    /// <summary>The tree of a system: the system row, the groups of the objects, the nebulae, the asteroid fields, the
    /// zones by role and the lights.</summary>
    private static HierarchyNode SystemTree(SystemIndex index, Func<object, string, string> text)
    {
        var system = index.System;
        var source = index.Source;
        var root = new HierarchyNode("root", ObjectKinds.StarSystem,
            text(source, "strid_name") is { Length: > 0 } systemName ? systemName : source.Nickname, source.Nickname,
            source);

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
                foreach (var lane in index.Lanes.Select(l => Lane(l, text))
                             .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
                {
                    group.Add(lane);
                }
            }
            else
            {
                bool InGroup(ObjectKind objectKind) =>
                    id == "other" ? !groupedKinds.Contains(objectKind) : members.Contains(objectKind);
                AddSorted(group, system.Objects.Where(o => !laneRings.Contains(o) && InGroup(ObjectKinds.Of(o)))
                    .Select(o => Item("o:" + o.Nickname, ObjectKinds.Of(o), o, o.Nickname, text)));
            }

            AddGroup(root, group);
        }

        // A nebula and a field are scene items with their own groups, also when they share a zone (decision D158 of the
        // M22 design).
        AddGroup(root, VolumeGroup("nebulae", "Nebulae", ObjectKinds.Nebula, "n:", index, text));
        AddGroup(root, VolumeGroup("fields", "Asteroid fields", ObjectKinds.AsteroidField, "f:", index, text));

        var zones = new HierarchyNode("zones", ObjectKinds.Of(ZoneRole.Other), "Zones");
        foreach (var role in Enum.GetValues<ZoneRole>())
        {
            var roleNode = new HierarchyNode("zones/" + role, ObjectKinds.Of(role), RoleNames[role]);
            AddSorted(roleNode, system.Zones.Where(z => index.ZoneRole(z) == role)
                .Select(z => Item("z:" + z.Nickname, ObjectKinds.Of(role), z, z.Nickname, text)));
            if (roleNode.Children.Count > 0)
            {
                zones.Add(roleNode);
            }
        }

        AddGroup(root, zones);
        var lights = new HierarchyNode("lights", ObjectKinds.Light, "Lights");
        AddSorted(lights,
            system.LightSources.Select(l => Item("l:" + l.Nickname, ObjectKinds.Light, l, l.Nickname, text)));
        AddGroup(root, lights);
        return root;
    }

    /// <summary>
    /// The tree of a cutscene: the cutscene row, then the groups of the Timeline with their entities in the order of the
    /// file. The target names that no entity of the script has are not entities, so they are not in the tree.
    /// </summary>
    private static HierarchyNode CutsceneTree(TimelineModel timeline)
    {
        var root = new HierarchyNode("cutscene", ObjectKinds.Thn.Script,
            System.IO.Path.GetFileNameWithoutExtension(timeline.Path), "", new CutsceneRoot(timeline.Path),
            timeline.Path);
        foreach (var group in timeline.Groups.Where(g => g.Id != TimelineGroupKind.Missing))
        {
            var node = new HierarchyNode("g:" + group.Id, group.Kind, group.Label);
            var added = new HashSet<ThnRef>();
            foreach (var lane in group.Lanes)
            {
                if (lane.Monitor is { } monitor && added.Add(monitor))
                {
                    node.Add(new HierarchyNode("e:" + monitor.Id.Value, ObjectKinds.Thn.Monitor, lane.Name,
                        lane.Main ? "main" : "", monitor, lane.Note));
                }
            }

            foreach (var row in group.Rows)
            {
                if (row.Entity is { } entity && added.Add(entity))
                {
                    var detail = row.Mark == TimelineModel.HiddenEntityMark ? "not used"
                        : row.Mark == TimelineModel.IgnoredMonitorMark ? "ignored monitor only"
                        : "";
                    node.Add(new HierarchyNode("e:" + entity.Id.Value, row.Kind, row.Name, detail, entity, row.Mark));
                }
            }

            AddGroup(root, node);
        }

        return root;
    }

    /// <summary>
    /// The group of the nebulae or of the asteroid fields: one row for each item of that kind. The name of a row is the
    /// name of the zone, or else the file name without the extension. The detail is the zone nickname. The id is the
    /// prefix and the zone nickname, with "#k" for the k-th repeat of a zone in the group.
    /// </summary>
    private static HierarchyNode VolumeGroup(string id, string groupName, ObjectKind kind, string prefix,
        SystemIndex index, Func<object, string, string> text)
    {
        var group = new HierarchyNode(id, kind, groupName);
        var repeats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<HierarchyNode>();
        foreach (var volume in index.VolumeItems.Where(v => v.Kind == kind))
        {
            var nickname = volume.Zone.Nickname;
            var repeat = repeats[nickname] = repeats.GetValueOrDefault(nickname) + 1;
            var rowId = prefix + nickname + (repeat > 1 ? $"#{repeat}" : "");
            var itemName = text(volume.Zone, "ids_name") is { Length: > 0 } named
                ? named
                : SceneItems.FallbackName(volume.Item) ?? nickname;
            rows.Add(new HierarchyNode(rowId, kind, itemName, nickname, volume.Item));
        }

        AddSorted(group, rows);
        return group;
    }

    /// <summary>Makes the row of an item: its name and its nickname, or only its nickname when it has no
    /// name.</summary>
    private static HierarchyNode Item(string id, ObjectKind kind, object item, string nickname,
        Func<object, string, string> text)
    {
        var itemName = text(item, "ids_name");
        return itemName.Length == 0
            ? new HierarchyNode(id, kind, nickname, "", item)
            : new HierarchyNode(id, kind, itemName, nickname, item);
    }

    /// <summary>
    /// A trade lane node with its rings in the order of the lane. The label names the two ends of the lane. It uses
    /// the trade lane space names of the end rings, then the names of the end rings, and then their nicknames, because
    /// all rings of a lane often have the same name.
    /// </summary>
    private static HierarchyNode Lane(TradeLane lane, Func<object, string, string> text)
    {
        var (first, last) = (lane.Rings[0], lane.Rings[^1]);
        var label = Ends(text(first, "tradelane_space_name"), text(last, "tradelane_space_name")) ??
                    Ends(text(first, "ids_name"), text(last, "ids_name")) ??
                    (lane.Rings.Count == 1 ? first.Nickname : $"{first.Nickname} -> {last.Nickname}");
        var rings = lane.Rings.Count == 1 ? "1 ring" : $"{lane.Rings.Count} rings";
        var node = new HierarchyNode("lane:" + first.Nickname, ObjectKinds.TradeLane, label, "", null,
            $"Trade lane from {first.Nickname} to {last.Nickname}, {rings}.");
        foreach (var ring in lane.Rings)
        {
            node.Add(Item("o:" + ring.Nickname, ObjectKinds.Of(ring), ring, ring.Nickname, text));
        }

        return node;
    }

    /// <summary>"A -> B", or null when a name is empty or both names are the same.</summary>
    private static string? Ends(string first, string last) =>
        first.Length == 0 || last.Length == 0 || first.Equals(last, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{first} -> {last}";

    private static void AddGroup(HierarchyNode root, HierarchyNode group)
    {
        // A group without items does not show.
        if (group.Children.Count > 0)
        {
            root.Add(group);
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
                              node.Detail.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                              node.Search.Contains(w, StringComparison.OrdinalIgnoreCase));
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
        // The root row has no count. Its groups show the counts.
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
