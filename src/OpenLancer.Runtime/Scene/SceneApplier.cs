using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Resources;
using LibreLancer.World;
using LibreLancer.World.Components;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.Runtime.Scene;

/// <summary>
/// Applies W to the open scene: the clone of a star system that the scene shows, its world objects and its renderer
/// (through <see cref="ISceneHost"/>). Each change of W shows in the scene, also an undo, a redo and a restore of
/// unsaved edits, and the scene keeps the unsaved edits across a system change.
/// </summary>
/// <remarks>
/// <para><see cref="Note"/> only records a change. <see cref="Run"/> applies the recorded changes. The editor calls
/// <see cref="Run"/> at two points of the frame where no worker task of the renderer reads the zones (flush A and flush
/// B, section 3 of the design).</para>
/// <para>A run compares each recorded section of W with the view that it applied last. The effects of the changed keys
/// (see <see cref="SceneFields"/>) run in this order. First, for each section: Transform, then Data, Light and
/// Environment, which copy the values into the scene item. Then, one time for all sections: the moved zones, one
/// <see cref="ISceneHost.IndexChanged"/> and the lights. So the index reads the new values. The effects that load
/// models (see <see cref="SceneFields.Deferred"/>) run last, and only when no edit is open and no reload runs.</para>
/// <para>A restore of unsaved edits lists no section, so the run catches up the whole system (see
/// <see cref="CatchUp"/>). A structural change of a section of the system adds, deletes or renames an item (milestone
/// M3). Then the run gives the renamed items their new nicknames first, then runs the structure step: it removes the
/// item of each section that W does not have, and builds the item of each new section, at the place that the file
/// order gives. So a create, a delete, an undo and a redo change only their items, and a rename loads no model again.
/// The applier keeps each removed nebula and field, so the undo of its delete puts back the same item.</para>
/// <para>W keeps the text placeholder of a text of the active mod, for example <c>ids_name = $str:li01_depot</c>. An
/// unsaved text has no id, and the id of a saved text depends on all texts of the mod. So a built item never gives the
/// scene an id for a placeholder: each text id field whose W line holds a placeholder keeps the value of the scene item.
/// The editor reads each name from W.</para>
/// <para>When the checks give an error for a section, or the engine cannot build its item, the scene keeps the last
/// valid state of the item, and <see cref="Problems"/> tells why.</para>
/// <para>The applier uses the <see cref="GameItemDb"/> of the data that the system came from for the life of the open
/// system, so a compare never mixes the items of two loads. Use it on the UI thread only.</para>
/// </remarks>
public sealed class SceneApplier
{
    /// <summary>The effects that change what the index of the scene reads.</summary>
    private const SceneEffect IndexEffects = SceneEffect.Label | SceneEffect.Roles | SceneEffect.Lanes;

    private readonly EditSession session;
    private readonly GameItemDb items;
    private readonly ResourceManager resources;
    private readonly StarSystem system;
    private readonly GameWorld world;
    private readonly IDictionary<SystemObject, GameObject> objects;
    private readonly ISceneHost host;

    /// <summary>The <c>[system]</c> section of the system in <c>universe.ini</c>, or null.</summary>
    private readonly SectionRef? universe;

    /// <summary>The zones of the asteroid fields and the nebulae. The renderer reads their size only when it builds a
    /// field (AsteroidFieldRenderer.cs:45).</summary>
    private readonly HashSet<Zone> volumeZones;

    private readonly Dictionary<SectionRef, object> itemOf = [];
    private readonly Dictionary<object, SectionRef> sectionOf = new(ReferenceEqualityComparer.Instance);

    /// <summary>The lines of each section as the scene shows them. A section without an entry has an unknown state, so
    /// each of its keys counts as changed.</summary>
    private readonly Dictionary<SectionRef, IReadOnlyList<EntryView>> applied = [];

    private readonly Dictionary<SectionRef, Dictionary<Step, Diagnostic>> problems = [];

    /// <summary>The problems that the host got, so that it gets each problem once.</summary>
    private readonly HashSet<Diagnostic> reported = [];

    private readonly HashSet<SectionRef> noted = [];
    private readonly Dictionary<object, SceneEffect> deferred = new(ReferenceEqualityComparer.Instance);

    /// <summary>The renames of the changes since the last run, in order.</summary>
    private readonly List<(SectionRef From, SectionRef To)> renames = [];

    /// <summary>The objects, nebulae and fields that the structure step removed, with the W lines that the scene showed
    /// for their sections, so an undo of a delete gets the same item back (milestone M3).</summary>
    private readonly List<(SceneSection Kind, object Item, IReadOnlyList<EntryView> Lines)> kept = [];

    private bool catchUpNoted;
    private bool structureNoted;
    private bool textNoted;

    // The effects of the current run that apply once, after the sections.
    private bool zonesMoved;
    private bool lightsChanged;
    private SceneEffect indexChanges;

    /// <param name="items">The game data that <paramref name="system"/> came from.</param>
    /// <param name="resources">The resources of the world, for a model that loads again.</param>
    /// <param name="system">The clone of the system that the scene shows. The applier changes it.</param>
    /// <param name="world">The world of the scene, with <paramref name="system"/> loaded.</param>
    /// <param name="objects">The world object of each object of <paramref name="system"/>. The applier adds and removes
    /// the world objects of new and deleted objects, so the views that share the map see them.</param>
    public SceneApplier(EditSession session, GameItemDb items, ResourceManager resources, StarSystem system,
        GameWorld world, IDictionary<SystemObject, GameObject> objects, ISceneHost host)
    {
        this.session = session;
        this.items = items;
        this.resources = resources;
        this.system = system;
        this.world = world;
        this.objects = objects;
        this.host = host;
        SystemPath = session.SystemPath(system.Nickname);
        universe = session.SystemSection(system.Nickname);
        // The system comes from the data of the last load, and W can hold newer edits. So the first run catches up,
        // also when the editor does not call CatchUp.
        catchUpNoted = true;
        volumeZones = new HashSet<Zone>(
            system.AsteroidFields.Select(f => f.Zone).Concat(system.Nebulae.Select(n => n.Zone)).OfType<Zone>(),
            ReferenceEqualityComparer.Instance);
    }

    /// <summary>The install path of the file of the system in W, or null when W has none. Then the applier does
    /// nothing.</summary>
    public string? SystemPath { get; }

    /// <summary>True when the applier has changes or effects that it did not apply yet.</summary>
    public bool HasPending =>
        catchUpNoted || structureNoted || textNoted || renames.Count > 0 || noted.Count > 0 || deferred.Count > 0;

    /// <summary>
    /// The section of W that a scene item shows. An object, a zone, a light, a nebula or an asteroid field of the system
    /// shows its section. The system shows its <c>[system]</c> section in <c>universe.ini</c>. Null for another item,
    /// and for an item whose section W does not have. The catch-up finds the sections of the items, so call
    /// <see cref="CatchUp"/> first.
    /// </summary>
    public SectionRef? SectionOf(object item) => item is StarSystem other
        ? other.Nickname.Equals(system.Nickname, StringComparison.OrdinalIgnoreCase) ? universe : null
        : sectionOf.GetValueOrDefault(item);

    /// <summary>The scene item that a section of W sets: an object, a zone, a light, a nebula or an asteroid field. A
    /// section of the fields of the system sets the system. Null for another section.</summary>
    public object? ItemOf(SectionRef section) =>
        itemOf.GetValueOrDefault(section) ?? (KindOf(section) is { } kind && IsSystemKind(kind) ? system : null);

    /// <summary>Why the scene does not show the W state of a section, for example a missing archetype or an unknown
    /// attenuation curve. Empty when it shows it.</summary>
    public IReadOnlyList<Diagnostic> Problems(SectionRef section) =>
        problems.TryGetValue(section, out var steps) ? steps.Values.ToList() : [];

    /// <summary>
    /// True when a section of W of the system waits for the reload of the game data (decisions D248 and D282 of the M23
    /// design): the loaded game data does not have something that the section names, for example a new archetype of the
    /// active mod, so the scene has no item for it. The editor then opens the system again after the reload, so the
    /// scene shows the item.
    /// </summary>
    public bool WaitsForReload =>
        problems.Any(p => p.Value.TryGetValue(Step.Build, out var problem) &&
                          problem.Message.EndsWith(AfterReload, StringComparison.Ordinal) && session.Get(p.Key) != null);

    /// <summary>The end of the message of a section that the scene shows only after the reload of the game
    /// data.</summary>
    private const string AfterReload = "after the save and the game data reload.";

    /// <summary>
    /// Builds each section of the system in W with the engine code, compares the item with the scene item, and applies
    /// the differences. So the scene shows the unsaved edits after a system opens, after a change that lists no section,
    /// and after a structural change of the system. The deferred effects run when they can. The first <see cref="Run"/> of a new applier does this too.
    /// Call it after the world loads the system, so that the first frame shows W.
    /// </summary>
    /// <returns>The number of sections whose scene item differed from W.</returns>
    public int CatchUp()
    {
        var differences = CatchUpSections();
        Finish();
        return differences;
    }

    /// <summary>Records a change of W. Nothing changes in the scene until <see cref="Run"/>.</summary>
    public void Note(EditChange change)
    {
        var sections = change.Sections.Where(s => KindOf(s) != null).ToList();
        // A restore of unsaved edits, and its undo and redo, list no section, so the run catches up the whole system. A
        // structural change of a section of the system adds, deletes or renames an item, so the structure step changes
        // the items (milestone M3). A structural change of another file, for example a new item from a template, changes
        // nothing that the scene shows, because the applier keeps the game data that the system came from. A change that
        // lists only the text or cutscenes changes no section.
        var listsOtherChanges = change.Text || change.Cutscenes.Count > 0;
        if (change.Sections.Count == 0 && (change.Structural || !listsOtherChanges))
            catchUpNoted = true;
        else if (change.Structural && sections.Count > 0)
            structureNoted = true;
        renames.AddRange(change.Renames.Where(r => KindOf(r.From) != null || KindOf(r.To) != null));
        textNoted |= change.Text;
        noted.UnionWith(sections);
    }

    /// <summary>Applies the changes that <see cref="Note"/> recorded, then the deferred effects when they can
    /// run.</summary>
    public void Run()
    {
        if (renames.Count > 0)
            ApplyRenames();
        if (catchUpNoted)
            CatchUpSections();
        else if (structureNoted)
            Structure();
        foreach (var section in noted)
            ApplyChanges(section);
        noted.Clear();
        // A change of a text changes the names that W gives through a placeholder. It lists no section.
        if (textNoted)
            indexChanges |= SceneEffect.Label;
        textNoted = false;
        Finish();
    }

    /// <summary>The scene section of a section of the system file or of the <c>[system]</c> of the system, or null for
    /// another section.</summary>
    private SceneSection? KindOf(SectionRef section)
    {
        if (universe != null && section.Equals(universe))
            return SceneSection.UniverseSystem;
        return SystemPath != null && VfsPath.Comparer.Equals(section.Path, SystemPath)
            ? SceneFields.SectionOf(section.Id.Type, universe: false)
            : null;
    }

    /// <summary>True for a section that sets the fields of the system.</summary>
    private static bool IsSystemKind(SceneSection kind) =>
        kind is not (SceneSection.Object or SceneSection.Zone or SceneSection.LightSource or SceneSection.Nebula or
            SceneSection.Asteroids);

    /// <summary>True for the section of a nebula or a field. The scene does not build these items from W, because no
    /// key of their sections has an effect (see <see cref="SceneFields"/>). The applier only maps them to their
    /// items.</summary>
    private static bool IsVolumeKind(SceneSection kind) => kind is SceneSection.Nebula or SceneSection.Asteroids;

    /// <summary>The sections of the system file and the <c>[system]</c> of the system, in file order.</summary>
    private IEnumerable<SectionRef> Sections() =>
        (SystemPath == null ? [] : session.Sections(SystemPath)).Concat(universe == null ? [] : [universe]);

    /// <summary>Finds the section of each object, zone, light, nebula and field of the system. The k-th item with a
    /// nickname is the section with that nickname and the ordinal k, because the engine keeps the file order
    /// (GameItemDb.cs:1466-1728). A nebula and a field have the nickname of their zone, which is the key of their
    /// section.</summary>
    private void Map()
    {
        itemOf.Clear();
        sectionOf.Clear();
        var byKind = new Dictionary<SceneSection, Dictionary<string, List<object>>>
        {
            [SceneSection.Object] = ByNickname(system.Objects),
            [SceneSection.Zone] = ByNickname(system.Zones),
            [SceneSection.LightSource] = ByNickname(system.LightSources),
            [SceneSection.Nebula] = ByZone(system.Nebulae.Select(n => (n.Zone, (object)n))),
            [SceneSection.Asteroids] = ByZone(system.AsteroidFields.Select(f => (f.Zone, (object)f))),
        };
        foreach (var section in Sections())
        {
            if (KindOf(section) is { } kind && byKind.TryGetValue(kind, out var byNickname) &&
                section.Id.Key is { } key && byNickname.TryGetValue(key, out var list) && section.Id.Ordinal < list.Count)
            {
                var item = list[section.Id.Ordinal];
                itemOf[section] = item;
                sectionOf[item] = section;
            }
        }
    }

    private static Dictionary<string, List<object>> ByNickname(IEnumerable<NicknameItem> list)
    {
        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            if (!result.TryGetValue(item.Nickname, out var same))
                result[item.Nickname] = same = [];
            same.Add(item);
        }
        return result;
    }

    /// <summary>The items with a zone by the nickname of the zone, in list order.</summary>
    private static Dictionary<string, List<object>> ByZone(IEnumerable<(Zone? Zone, object Item)> list)
    {
        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (zone, item) in list)
        {
            if (zone == null)
                continue;
            if (!result.TryGetValue(zone.Nickname, out var same))
                result[zone.Nickname] = same = [];
            same.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Gives the item of each renamed section the nickname of the side of the pair that W has now: the item, its world
    /// object (whose setter also sets the hash) and the zone map of the system. The scene state of the section moves to
    /// the new reference, so the rename loads no model again. The pairs of an undo come in the other direction, so the
    /// same step serves a rename, its undo and its redo. A nebula and a field follow the nickname of their zone.
    /// </summary>
    private void ApplyRenames()
    {
        foreach (var (from, to) in renames)
        {
            // The side of the pair that W has now gives the nickname.
            var (now, before) = session.Get(to) != null ? (to, from) : (from, to);
            if (session.Get(now) == null || now.Id.Key is not { } name)
                continue;
            if ((itemOf.GetValueOrDefault(before) ?? itemOf.GetValueOrDefault(now)) is { } item)
            {
                switch (item)
                {
                    case SystemObject obj:
                        obj.Nickname = name;
                        if (objects.TryGetValue(obj, out var gameObject))
                            gameObject.Nickname = name;
                        break;
                    case Zone zone:
                        if (system.ZoneDict.TryGetValue(zone.Nickname, out var mapped) && ReferenceEquals(mapped, zone))
                            system.ZoneDict.Remove(zone.Nickname);
                        zone.Nickname = name;
                        system.ZoneDict[name] = zone;
                        break;
                    case LightSource light:
                        light.Nickname = name;
                        lightsChanged = true;
                        break;
                }
                itemOf.Remove(before);
                itemOf[now] = item;
                sectionOf[item] = now;
            }
            if (applied.Remove(before, out var lines))
                applied[now] = lines;
            if (problems.Remove(before, out var steps))
                problems[now] = steps;
            indexChanges |= IndexEffects;
        }
        renames.Clear();
        Map();
    }

    /// <summary>
    /// The structure step (section 14 of the M3 design): removes the item of each object, zone, light, nebula and field
    /// whose section W does not have now, and builds the item of each section of W without an item. A new item goes into
    /// the list of the system at the index that the file order gives, so the ordinals of the map stay right. A new object
    /// gets its world object, and a new zone a new zone lookup. A new nebula or field gets the removed item back when its
    /// lines equal the lines of that item. Then the step finds the section of each item again.
    /// </summary>
    /// <remarks>A new object loads its model, so it waits while an edit is open or the game data loads again. An object
    /// that the engine does not build, for example with an archetype that the loaded game data does not have, gets the
    /// problem of the catch-up and no item.</remarks>
    private void Structure()
    {
        structureNoted = false;
        if (SystemPath == null)
        {
            Map();
            return;
        }
        var sections = Sections().Select(s => (Section: s, Kind: KindOf(s))).ToList();
        List<SectionRef> Of(SceneSection kind) => sections.Where(s => s.Kind == kind).Select(s => s.Section).ToList();
        var changed = false;
        var zonesChanged = false;
        changed |= Sync(Of(SceneSection.Object), system.Objects, o => o.Nickname, RemoveObject,
            (section, at) => AddObject(section, at));
        if (Sync(Of(SceneSection.Zone), system.Zones, z => z.Nickname, RemoveZone, AddZone))
        {
            changed = zonesChanged = true;
        }
        if (Sync(Of(SceneSection.LightSource), system.LightSources, l => l.Nickname, RemoveLight, AddLight))
        {
            changed = lightsChanged = true;
        }
        changed |= Sync(Of(SceneSection.Nebula), system.Nebulae, n => n.Zone?.Nickname,
            n => RemoveVolume(SceneSection.Nebula, n, n.Zone), (section, at) => AddVolume(SceneSection.Nebula, section, at));
        changed |= Sync(Of(SceneSection.Asteroids), system.AsteroidFields, f => f.Zone?.Nickname,
            f => RemoveVolume(SceneSection.Asteroids, f, f.Zone), (section, at) => AddVolume(SceneSection.Asteroids, section, at));
        if (zonesChanged)
        {
            // The zone lookup of the world keeps a tree of the zones of the list.
            world.Zones?.Dispose();
            world.Zones = new ZoneLookup(system.Zones);
            zonesMoved = true;
        }
        if (changed)
            indexChanges |= IndexEffects;
        Map();
    }

    /// <summary>
    /// Removes the items of a list whose section W does not have, and adds an item for each section without one. An item
    /// matches the section with its key and the same ordinal among the items with the key.
    /// </summary>
    /// <param name="key">The key of an item: its nickname, or the nickname of the zone of a nebula or a field.</param>
    /// <param name="add">Builds and inserts the item of a section at an index of the list. False when it builds
    /// none.</param>
    /// <returns>True when the list changed.</returns>
    private static bool Sync<T>(List<SectionRef> wanted, List<T> list, Func<T, string?> key, Action<T> remove,
        Func<SectionRef, int, bool> add) where T : class
    {
        var keys = wanted.Select(s => (s.Id.Key?.ToLowerInvariant() ?? "", s.Id.Ordinal)).ToHashSet();
        var counts = new Dictionary<string, int>();
        var gone = new List<T>();
        foreach (var item in list)
        {
            var name = key(item)?.ToLowerInvariant() ?? "";
            var ordinal = counts.GetValueOrDefault(name);
            counts[name] = ordinal + 1;
            if (!keys.Contains((name, ordinal)))
                gone.Add(item);
        }
        foreach (var item in gone)
            remove(item);

        var present = new HashSet<(string, int)>();
        counts.Clear();
        foreach (var item in list)
        {
            var name = key(item)?.ToLowerInvariant() ?? "";
            var ordinal = counts.GetValueOrDefault(name);
            counts[name] = ordinal + 1;
            present.Add((name, ordinal));
        }
        var added = false;
        var index = 0;
        foreach (var section in wanted)
        {
            var id = (section.Id.Key?.ToLowerInvariant() ?? "", section.Id.Ordinal);
            if (present.Contains(id))
            {
                index++;
                continue;
            }
            if (add(section, Math.Min(index, list.Count)))
            {
                present.Add(id);
                added = true;
                index++;
            }
        }
        return gone.Count > 0 || added;
    }

    /// <summary>
    /// Adds the object of a new section and its world object, or false when the engine builds none or the model cannot
    /// load now. A removed object with the nickname of the section comes back (see <see cref="FindKept"/>). Another
    /// section gets an object that the engine builds from W.
    /// </summary>
    private bool AddObject(SectionRef section, int at)
    {
        if (session.Get(section) is not { } view)
            return false;
        var keptAt = FindKept(SceneSection.Object, section);
        SystemObject? obj;
        IReadOnlyList<EntryView> lines;
        if (keptAt >= 0)
        {
            obj = (SystemObject)kept[keptAt].Item;
            lines = kept[keptAt].Lines;
        }
        else if (session.Parse<Universe.SystemObject>(section, Universe.SystemObject.TryParse) is not { } ini ||
                 (obj = SceneBuild.Object(items, system.Nickname, ini, out _)) == null)
            return false;
        else
            lines = view.Entries;
        // The world object loads the model, which waits as Reinit waits (review finding F5 of M1).
        if (host.EditOpen || !host.CanLoadModels)
        {
            structureNoted = true;
            return false;
        }
        if (keptAt >= 0)
            kept.RemoveAt(keptAt);
        system.Objects.Insert(at, obj);
        var gameObject = new GameObject();
        world.InitObject(gameObject, false, obj, resources, null);
        objects[obj] = gameObject;
        Shown(section, view, lines);
        return true;
    }

    /// <summary>Removes an object and its world object, and keeps the object with the W lines that the scene showed for
    /// its section.</summary>
    private void RemoveObject(SystemObject obj)
    {
        Keep(SceneSection.Object, obj);
        system.Objects.Remove(obj);
        deferred.Remove(obj);
        if (!objects.Remove(obj, out var gameObject))
            return;
        world.RemoveObject(gameObject);
        gameObject.Unregister(world);
    }

    /// <summary>Adds the zone of a new section: a removed zone with its nickname (see <see cref="FindKept"/>), or a zone
    /// that the engine builds from W.</summary>
    private bool AddZone(SectionRef section, int at)
    {
        if (session.Get(section) is not { } view)
            return false;
        var keptAt = FindKept(SceneSection.Zone, section);
        Zone zone;
        IReadOnlyList<EntryView> lines;
        if (keptAt >= 0)
        {
            zone = (Zone)kept[keptAt].Item;
            lines = kept[keptAt].Lines;
            kept.RemoveAt(keptAt);
        }
        else if (session.Parse<Universe.Zone>(section, Universe.Zone.TryParse) is not { } ini)
            return false;
        else
        {
            zone = SceneBuild.Zone(ini);
            lines = view.Entries;
        }
        system.Zones.Insert(at, zone);
        // The engine keeps the last zone of a nickname (GameItemDb.cs:1680).
        system.ZoneDict[zone.Nickname] = zone;
        Shown(section, view, lines);
        return true;
    }

    private void RemoveZone(Zone zone)
    {
        Keep(SceneSection.Zone, zone);
        system.Zones.Remove(zone);
        if (system.ZoneDict.TryGetValue(zone.Nickname, out var mapped) && ReferenceEquals(mapped, zone))
        {
            system.ZoneDict.Remove(zone.Nickname);
            if (system.Zones.LastOrDefault(z => z.Nickname.Equals(zone.Nickname, StringComparison.OrdinalIgnoreCase)) is { } other)
                system.ZoneDict[zone.Nickname] = other;
        }
    }

    /// <summary>Adds the light of a new section: a removed light with its nickname (see <see cref="FindKept"/>), or a
    /// light that the engine builds from W.</summary>
    private bool AddLight(SectionRef section, int at)
    {
        if (session.Get(section) is not { } view)
            return false;
        var keptAt = FindKept(SceneSection.LightSource, section);
        LightSource? light;
        IReadOnlyList<EntryView> lines;
        if (keptAt >= 0)
        {
            light = (LightSource)kept[keptAt].Item;
            lines = kept[keptAt].Lines;
            kept.RemoveAt(keptAt);
        }
        else if (session.Parse<Universe.LightSource>(section, Universe.LightSource.TryParse) is not { } ini ||
                 (light = SceneBuild.Light(items, ini, out _)) == null)
            return false;
        else
            lines = view.Entries;
        system.LightSources.Insert(at, light);
        Shown(section, view, lines);
        return true;
    }

    private void RemoveLight(LightSource light)
    {
        Keep(SceneSection.LightSource, light);
        system.LightSources.Remove(light);
    }

    /// <summary>Records the lines that the scene shows for the item of a new section. The run then applies the keys of a
    /// kept item that differ from W, also after a wait for the model.</summary>
    private void Shown(SectionRef section, SectionView view, IReadOnlyList<EntryView> lines)
    {
        applied[section] = lines;
        if (!ReferenceEquals(lines, view.Entries))
            noted.Add(section);
    }

    /// <summary>Keeps a removed item with the W lines that the scene showed for its section, when it has a section.</summary>
    private void Keep(SceneSection kind, object item)
    {
        if (sectionOf.TryGetValue(item, out var section) && applied.GetValueOrDefault(section) is { } lines)
            kept.Add((kind, item, lines));
    }

    /// <summary>
    /// The index of the last removed object, zone or light of a kind with the nickname of a section, or -1. The item
    /// comes back with the lines that the scene showed for it. The run then applies the keys that differ from W, as for
    /// an edit. Thus the undo of a delete gives the same item back, also when a line of the section has an error.
    /// </summary>
    private int FindKept(SceneSection kind, SectionRef section) =>
        kept.FindLastIndex(k => k.Kind == kind &&
                                ((NicknameItem)k.Item).Nickname.Equals(section.Id.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes a nebula or a field, and keeps it with the W lines that the scene showed for its section, so the
    /// undo of its delete puts back the same item. A field also loses its world object.</summary>
    private void RemoveVolume(SceneSection kind, object item, Zone? zone)
    {
        Keep(kind, item);
        if (item is Nebula nebula)
            system.Nebulae.Remove(nebula);
        else if (item is AsteroidField field)
        {
            system.AsteroidFields.Remove(field);
            if (world.Objects.FirstOrDefault(o => o.TryGetComponent<AsteroidFieldComponent>(out var c) &&
                                                  ReferenceEquals(c.Field, field)) is { } fieldObject)
            {
                world.RemoveObject(fieldObject);
                fieldObject.Unregister(world);
            }
        }
        // A nebula and a field can share their zone.
        if (zone != null && !system.Nebulae.Any(n => ReferenceEquals(n.Zone, zone)) &&
            !system.AsteroidFields.Any(f => ReferenceEquals(f.Zone, zone)))
            volumeZones.Remove(zone);
        Defer(system, SceneEffect.Volume);
    }

    /// <summary>Puts back a removed nebula or field whose lines equal the lines of a new section. Another new section
    /// gets a problem: the editor cannot build a new field without a fork change (review finding R3).</summary>
    private bool AddVolume(SceneSection kind, SectionRef section, int at)
    {
        if (session.Get(section) is not { } view)
            return false;
        var keptAt = kept.FindIndex(k => k.Kind == kind && !SceneLines.ChangedKeys(k.Lines, view.Entries).Any());
        var zone = section.Id.Key is { } name ? system.ZoneDict.GetValueOrDefault(name) : null;
        if (keptAt < 0 || zone == null)
        {
            SetProblem(section, Step.Build, Problem(section, $"The Scene shows this item {AfterReload}", null));
            return false;
        }
        var item = kept[keptAt].Item;
        kept.RemoveAt(keptAt);
        if (item is Nebula nebula)
        {
            nebula.Zone = zone;
            system.Nebulae.Insert(at, nebula);
        }
        else if (item is AsteroidField field)
        {
            field.Zone = zone;
            system.AsteroidFields.Insert(at, field);
            var fieldObject = new GameObject();
            fieldObject.AddComponent(new AsteroidFieldComponent(field, resources, fieldObject));
            world.AddObject(fieldObject);
            fieldObject.Register(world);
        }
        volumeZones.Add(zone);
        applied[section] = view.Entries;
        Defer(system, SceneEffect.Volume);
        return true;
    }

    /// <summary>The catch-up of <see cref="CatchUp"/>, without the effects that apply once.</summary>
    private int CatchUpSections()
    {
        catchUpNoted = false;
        noted.Clear();
        applied.Clear();
        problems.Clear();
        // W can add or delete items since the load, also an unsaved edit before the system opened.
        Structure();
        var differences = 0;
        var systemSections = new List<SectionRef>();
        foreach (var section in Sections())
        {
            if (KindOf(section) is not { } kind || session.Get(section) is not { } view)
                continue;
            if (IsVolumeKind(kind))
            {
                // No key of these sections changes the scene, so the scene shows the lines of W.
                applied[section] = view.Entries;
                continue;
            }
            if (IsSystemKind(kind))
            {
                systemSections.Add(section);
                continue;
            }
            if (!itemOf.TryGetValue(section, out var item))
            {
                NotShown(section, kind);
                continue;
            }
            bool? differs = item switch
            {
                SystemObject obj => CatchUpObject(section, view, obj),
                Zone zone => CatchUpZone(section, view, zone),
                _ => CatchUpLight(section, view, (LightSource)item),
            };
            if (differs == null)
                continue;
            differences += differs.Value ? 1 : 0;
            applied[section] = view.Entries;
        }
        if (BuildSystem() is { } built)
        {
            if (!SceneBuild.SameSystem(built, system))
            {
                differences++;
                CopySystem(built);
            }
            foreach (var section in systemSections)
                applied[section] = session.Get(section)!.Entries;
        }
        if (differences > 0)
            indexChanges |= IndexEffects;
        return differences;
    }

    /// <returns>True when the object differed, false when it equals W, and null when W does not build.</returns>
    private bool? CatchUpObject(SectionRef section, SectionView view, SystemObject obj)
    {
        if (BuildObject(section, view, obj) is not { } built)
            return null;
        if (SceneBuild.SameObject(built, obj))
            return false;
        // Only these fields change the model or the components of the world object (GameWorld.cs:100-178).
        var reinit = !ReferenceEquals(built.Archetype, obj.Archetype) || !ReferenceEquals(built.Loadout, obj.Loadout) ||
                     !ReferenceEquals(built.Star, obj.Star) || !built.Spin.Equals(obj.Spin) ||
                     !SceneBuild.SameDock(built.Dock, obj.Dock);
        SceneBuild.CopyObject(built, obj);
        MoveWorldObject(obj);
        if (reinit)
            Defer(obj, SceneEffect.Reinit);
        return true;
    }

    /// <returns>True when the zone differed, false when it equals W, and null when W does not build.</returns>
    private bool? CatchUpZone(SectionRef section, SectionView view, Zone zone)
    {
        if (BuildZone(section, view, zone) is not { } built)
            return null;
        if (built.ZonesEqual(zone))
            return false;
        if (volumeZones.Contains(zone) && (built.Shape != zone.Shape || !built.Size.Equals(zone.Size)))
            Defer(system, SceneEffect.Volume);
        CopyZone(built, zone);
        zonesMoved = true;
        return true;
    }

    /// <returns>True when the light differed, false when it equals W, and null when W does not build.</returns>
    private bool? CatchUpLight(SectionRef section, SectionView view, LightSource light)
    {
        if (BuildLight(section, view) is not { } built)
            return null;
        if (SceneBuild.SameLight(built, light))
            return false;
        light.Light = built.Light;
        light.AttenuationCurveName = built.AttenuationCurveName;
        lightsChanged = true;
        return true;
    }

    /// <summary>Applies the keys of a section that changed since the scene applied it last.</summary>
    private void ApplyChanges(SectionRef section)
    {
        if (KindOf(section) is not { } kind)
            return;
        if (session.Get(section) is not { } view)
        {
            // The structure step removed the item of the section.
            applied.Remove(section);
            problems.Remove(section);
            return;
        }
        // A new nebula or field that the structure step could not put back keeps its problem.
        if (IsVolumeKind(kind) && !itemOf.ContainsKey(section))
            return;
        var effects = applied.TryGetValue(section, out var before)
            ? SceneLines.ChangedKeys(before, view.Entries).Aggregate(SceneEffect.None, (all, key) => all | SceneFields.Effect(kind, key))
            : SceneFields.All(kind);
        if (effects != SceneEffect.None &&
            !(IsSystemKind(kind) ? ApplySystem(effects) : ApplyItem(section, kind, view, effects)))
            return;
        // The scene now shows the lines of W, also when W went back to the state that the scene shows.
        applied[section] = view.Entries;
        problems.Remove(section);
    }

    /// <summary>Applies the effects of the changed keys of an object, a zone or a light.</summary>
    /// <returns>False when an effect could not apply. The scene then keeps the last valid state of the item, and the
    /// next change of the section applies the changes again.</returns>
    private bool ApplyItem(SectionRef section, SceneSection kind, SectionView view, SceneEffect effects)
    {
        if (!itemOf.TryGetValue(section, out var item))
        {
            NotShown(section, kind);
            return false;
        }
        var ok = (effects & SceneEffect.Transform) == 0 || ApplyTransform(section, kind, view, item);
        switch (item)
        {
            case SystemObject obj when (effects & SceneEffect.Data) != 0:
                if (BuildObject(section, view, obj) is { } builtObject)
                {
                    SceneBuild.CopyObject(builtObject, obj);
                    if ((effects & SceneEffect.Reinit) != 0)
                        Defer(obj, SceneEffect.Reinit);
                }
                else
                    ok = false;
                break;
            case Zone zone:
                if ((effects & SceneEffect.Volume) != 0 && ok && volumeZones.Contains(zone))
                    Defer(system, SceneEffect.Volume);
                if ((effects & SceneEffect.Data) != 0)
                {
                    if (BuildZone(section, view, zone) is { } builtZone)
                        CopyZone(builtZone, zone);
                    else
                        ok = false;
                }
                break;
            case LightSource light when (effects & SceneEffect.Light) != 0:
                if (BuildLight(section, view) is { } builtLight)
                {
                    light.Light = builtLight.Light;
                    light.AttenuationCurveName = builtLight.AttenuationCurveName;
                    lightsChanged = true;
                }
                else
                    ok = false;
                break;
        }
        indexChanges |= effects & IndexEffects;
        return ok;
    }

    /// <summary>
    /// Sets the transform of an item from the fast values of its lines, without a parse, so a drag costs no parse
    /// (section 7.6 of the design). A check error of a line that the fast values read keeps the last transform.
    /// </summary>
    private bool ApplyTransform(SectionRef section, SceneSection kind, SectionView view, object item)
    {
        if (SceneBuild.TransformError(session.Check(section), kind) is { } error)
        {
            SetProblem(section, Step.Transform, KeptProblem(section, error));
            return false;
        }
        ClearProblem(section, Step.Transform);
        var position = SceneBuild.Position(SceneLines.Last(view, "pos"));
        switch (item)
        {
            case SystemObject obj:
                obj.Position = position;
                obj.Rotation = SceneBuild.ObjectRotation(SceneLines.Last(view, "rotate"));
                MoveWorldObject(obj);
                break;
            case Zone zone:
                var shape = SceneBuild.ZoneShape(SceneLines.Last(view, "shape"));
                var rotation = SceneBuild.ZoneRotation(SceneLines.Last(view, "rotate"), out var radians);
                SetTransform(zone, position, rotation, radians, shape,
                    SceneBuild.ZoneSize(SceneLines.Last(view, "size"), shape));
                zonesMoved = true;
                break;
            case LightSource light:
                light.Light.Position = position;
                lightsChanged = true;
                break;
        }
        return true;
    }

    /// <summary>Builds the fields of the system again and copies them into the scene system.</summary>
    private bool ApplySystem(SceneEffect effects)
    {
        if ((effects & (SceneEffect.Data | SceneEffect.Label | SceneEffect.Environment | SceneEffect.Starsphere)) == 0)
            return true;
        if (BuildSystem() is not { } built)
            return false;
        CopySystem(built);
        indexChanges |= effects & IndexEffects;
        return true;
    }

    /// <summary>The object that the engine builds from W, or null with a problem when it builds none.</summary>
    /// <param name="clone">The scene object, whose text ids the built object keeps for a placeholder.</param>
    private SystemObject? BuildObject(SectionRef section, SectionView view, SystemObject clone)
    {
        if (Parse<Universe.SystemObject>(section, Universe.SystemObject.TryParse) is not { } ini)
            return null;
        if (SceneBuild.Object(items, system.Nickname, ini, out var problem) is not { } built)
        {
            SetProblem(section, Step.Build, Problem(section, problem!, "archetype"));
            return null;
        }
        ClearProblem(section, Step.Build);
        if (SceneLines.HoldsPlaceholder(view, "ids_name"))
            built.IdsName = clone.IdsName;
        if (SceneLines.HoldsPlaceholder(view, "ids_info"))
            built.IdsInfo = clone.IdsInfo;
        if (SceneLines.HoldsPlaceholder(view, "tradelane_space_name"))
            built.TradelaneSpaceName = clone.TradelaneSpaceName;
        return built;
    }

    /// <summary>The zone that the engine builds from W, or null with a problem when it builds none.</summary>
    /// <param name="clone">The scene zone, whose text ids the built zone keeps for a placeholder.</param>
    private Zone? BuildZone(SectionRef section, SectionView view, Zone clone)
    {
        if (Parse<Universe.Zone>(section, Universe.Zone.TryParse) is not { } ini)
            return null;
        ClearProblem(section, Step.Build);
        var built = SceneBuild.Zone(ini);
        if (SceneLines.HoldsPlaceholder(view, "ids_name"))
            built.IdsName = clone.IdsName;
        if (SceneLines.HoldsPlaceholder(view, "ids_info"))
            built.IdsInfo = clone.IdsInfo;
        return built;
    }

    /// <summary>The light that the engine builds from W, or null with a problem when it builds none, for example for an
    /// unknown attenuation curve.</summary>
    private LightSource? BuildLight(SectionRef section, SectionView view)
    {
        if (Parse<Universe.LightSource>(section, Universe.LightSource.TryParse) is not { } ini)
            return null;
        if (SceneBuild.Light(items, ini, out var problem) is not { } built)
        {
            SetProblem(section, Step.Build, Problem(section, problem!, "atten_curve"));
            return null;
        }
        ClearProblem(section, Step.Build);
        return built;
    }

    /// <summary>
    /// The fields of the system that the engine builds from W, or null with a problem when it builds none. A section
    /// with a check error keeps the last state of the whole system, because the build would leave the section out.
    /// </summary>
    private StarSystem? BuildSystem()
    {
        foreach (var section in Sections())
        {
            if (KindOf(section) is not { } kind || !IsSystemKind(kind))
                continue;
            if (FirstError(section) is { } error)
            {
                SetProblem(section, Step.Build, KeptProblem(section, error));
                return null;
            }
            ClearProblem(section, Step.Build);
        }
        if (SceneBuild.ParseSystem(session, system.Nickname) is not { } ini)
            return null;
        var built = SceneBuild.StarSystem(items, ini);
        if (universe != null && session.Get(universe) is { } view)
        {
            if (SceneLines.HoldsPlaceholder(view, "strid_name"))
                built.IdsName = system.IdsName;
            if (SceneLines.HoldsPlaceholder(view, "ids_info"))
                built.IdsInfo = system.IdsInfo;
        }
        return built;
    }

    /// <summary>Parses a section of W, or gives null with a problem that names the first check error.</summary>
    private T? Parse<T>(SectionRef section, SectionParser<T> parser) where T : class
    {
        if (session.Parse(section, parser) is { } value)
            return value;
        if (FirstError(section) is { } error)
            SetProblem(section, Step.Build, KeptProblem(section, error));
        return null;
    }

    private Diagnostic? FirstError(SectionRef section) =>
        session.Check(section).FirstOrDefault(p => p.Severity == DiagnosticSeverity.Error);

    /// <summary>Copies the fields of the system and the colors. The renderer loads changed starspheres later.</summary>
    private void CopySystem(StarSystem built)
    {
        if (!SceneBuild.SameStarspheres(built, system))
            Defer(system, SceneEffect.Starsphere);
        SceneBuild.CopySystem(built, system);
    }

    /// <summary>
    /// Copies the values of a built zone into a scene zone. <see cref="Zone.CopyTo"/> copies the shape data but not the
    /// outlines that the zone keeps from its last shape, and the transform setters clear them.
    /// </summary>
    private static void CopyZone(Zone built, Zone zone)
    {
        built.CopyTo(zone);
        SetTransform(zone, built.Position, built.RotationMatrix, built.RotationAngles, built.Shape, built.Size);
    }

    private static void SetTransform(Zone zone, Vector3 position, Matrix4x4 rotation, Vector3 radians, ShapeKind shape,
        Vector3 size)
    {
        zone.Position = position;
        zone.RotationMatrix = rotation;
        zone.RotationAngles = radians;
        zone.Shape = shape;
        zone.Size = size;
    }

    /// <summary>Gives the world object of an object the position and rotation of the object. The physics body moves
    /// too (GameObject.cs:256-281).</summary>
    private void MoveWorldObject(SystemObject obj)
    {
        if (objects.TryGetValue(obj, out var gameObject))
            gameObject.SetTransform(new Transform3D(obj.Position, obj.Rotation));
    }

    /// <summary>Records the problem of a section that has no scene item: the engine dropped its object, or the system
    /// loaded without the section.</summary>
    private void NotShown(SectionRef section, SceneSection kind)
    {
        var (message, key) = ("The scene does not show this section, because the system loaded without it. Save, then " +
                              "open the system again.", (string?)null);
        if (kind == SceneSection.Object)
        {
            // Parse records the check error.
            if (Parse<Universe.SystemObject>(section, Universe.SystemObject.TryParse) is not { } ini)
                return;
            // The engine drops an object without a valid archetype (GameItemDb.cs:2660-2664). An archetype of W that the
            // loaded game data does not have yet, for example an unsaved archetype of the active mod, shows after the
            // save (decision D248 of the M3 design).
            if (SceneBuild.Object(items, system.Nickname, ini, out var dropped) == null)
                (message, key) = ini.Archetype != null && session.Lists.List("solar_archetype")?.Find(ini.Archetype) != null
                    ? ($"The Scene shows this object {AfterReload}", "archetype")
                    : (dropped!, "archetype");
        }
        SetProblem(section, Step.Build, Problem(section, message, key));
    }

    private void Defer(object item, SceneEffect effect) => deferred[item] = deferred.GetValueOrDefault(item) | effect;

    /// <summary>The effects that apply once after the sections, then the deferred effects and the problems.</summary>
    private void Finish()
    {
        if (zonesMoved)
        {
            // The zone lookup of the world keeps the bounds of each zone.
            world.Zones?.UpdatePositions();
            host.ZonesMoved();
        }
        if (indexChanges != SceneEffect.None)
            host.IndexChanged(indexChanges);
        if (lightsChanged)
            host.LightsChanged(system);
        zonesMoved = false;
        lightsChanged = false;
        indexChanges = SceneEffect.None;
        RunDeferred();
        ReportProblems();
    }

    /// <summary>Runs the effects that load models, when no edit is open and no reload runs.</summary>
    private void RunDeferred()
    {
        if (deferred.Count == 0 || host.EditOpen || !host.CanLoadModels)
            return;
        var effects = deferred.ToList();
        deferred.Clear();
        foreach (var (item, effect) in effects)
        {
            if (item is SystemObject obj && objects.TryGetValue(obj, out var gameObject))
            {
                // The object holds the W values (Data), so the model, the loadout, the star and the dock come from W,
                // as LancerEdit SetArchetypeLoadout does it (SystemEditorTab.cs:714-724). InitObject also loads the model
                // and sets the transform of the object.
                world.InitObject(gameObject, true, obj, resources, null, false);
                continue;
            }
            if ((effect & SceneEffect.Volume) != 0)
                host.VolumesChanged(system);
            if ((effect & SceneEffect.Starsphere) != 0)
                host.StarspheresChanged(system);
        }
    }

    /// <summary>Gives each new problem to the host once, when no edit is open. A problem that goes and comes back is
    /// new again.</summary>
    private void ReportProblems()
    {
        if (host.EditOpen || problems.Count == 0 && reported.Count == 0)
            return;
        var current = problems.Values.SelectMany(s => s.Values).ToHashSet();
        foreach (var problem in current)
        {
            if (reported.Add(problem))
                host.ProblemFound(problem);
        }
        reported.IntersectWith(current);
    }

    private void SetProblem(SectionRef section, Step step, Diagnostic problem)
    {
        if (!problems.TryGetValue(section, out var steps))
            problems[section] = steps = [];
        steps[step] = problem;
    }

    private void ClearProblem(SectionRef section, Step step)
    {
        if (problems.TryGetValue(section, out var steps) && steps.Remove(step) && steps.Count == 0)
            problems.Remove(section);
    }

    private static Diagnostic Problem(SectionRef section, string message, string? key) =>
        new(DiagnosticSeverity.Warning, section.Path, $"{section.Id}: {message}") { Target = section, Key = key };

    /// <summary>The problem of a section whose W state the scene does not show because of a check error.</summary>
    private static Diagnostic KeptProblem(SectionRef section, Diagnostic error) =>
        Problem(section, error.Key == null
            ? "The scene shows the last valid state of this section. Correct the error first."
            : $"The scene shows the last valid state of this section. Correct the error of '{error.Key}' first.", error.Key);

    /// <summary>The step of an apply that a problem comes from. A step that applies clears its problem.</summary>
    private enum Step
    {
        Transform,
        Build,
    }
}
