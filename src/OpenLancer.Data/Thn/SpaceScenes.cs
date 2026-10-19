using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Thn;

/// <summary>A system object of the loaded game data.</summary>
/// <param name="Nickname">The nickname, as the system file spells it.</param>
/// <param name="System">The nickname of the system, as <see cref="SpaceFacts.Systems"/> spells it.</param>
public sealed record SpaceObjectFact(string Nickname, string System, Vector3 Position, Quaternion Orientation);

/// <summary>
/// The facts of the loaded game data that the rules of the space lines read (decisions D641 and D644 of the space
/// context design). The tests give synthetic facts.
/// </summary>
/// <param name="Systems">The nicknames of the systems, in the order of <c>universe.ini</c>.</param>
/// <param name="BaseSystems">The system of each base, by the nickname of the base, without case.</param>
/// <param name="Objects">The system objects, by nickname, without case. The first object of a nickname wins.</param>
public sealed record SpaceFacts(
    IReadOnlyList<string> Systems,
    IReadOnlyDictionary<string, string> BaseSystems,
    IReadOnlyDictionary<string, SpaceObjectFact> Objects)
{
    private readonly Dictionary<string, string> spelling =
        Systems.DistinctBy(s => s, StringComparer.OrdinalIgnoreCase).ToDictionary(s => s, s => s,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The facts of the loaded game data.</summary>
    public static SpaceFacts From(GameItemDb items)
    {
        var systems = items.Systems.Select(s => s.Nickname).ToList();
        var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dock in items.Bases)
        {
            if (dock.System is { Length: > 0 } system)
                bases.TryAdd(dock.Nickname, system);
        }

        var objects = new Dictionary<string, SpaceObjectFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in items.Systems)
        {
            foreach (var obj in system.Objects)
                objects.TryAdd(obj.Nickname, new SpaceObjectFact(obj.Nickname, system.Nickname, obj.Position, obj.Rotation));
        }

        return new SpaceFacts(systems, bases, objects);
    }

    /// <summary>The nickname of a system as the facts spell it, or null when the data has no such system.</summary>
    public string? System(string name) => spelling.GetValueOrDefault(name);

    /// <summary>The system of a base, as the facts spell it, or null.</summary>
    public string? SystemOfBase(string name) => BaseSystems.TryGetValue(name, out var system) ? System(system) : null;
}

/// <summary>The rule that gave the system of a space line (decision D641).</summary>
public enum SpaceSystemRule
{
    /// <summary>The trigger of the line has <c>system = &lt;nickname&gt;</c>.</summary>
    TriggerKey,

    /// <summary>A condition of the trigger names a system, a base or a system object.</summary>
    Condition,

    /// <summary>A trigger that activates the trigger of the line gives the system.</summary>
    ActivatingTrigger,
}

/// <summary>The rule that gave the start pose of the main object of a space line (decision D644).</summary>
public enum SpaceStartRule
{
    /// <summary><c>Act_SetInitialPlayerPos</c> or <c>Act_MovePlayer</c>.</summary>
    PlayerPosition,

    /// <summary><c>Act_RelocateShip</c>.</summary>
    Relocate,

    /// <summary><c>Act_SpawnShip</c> with a position.</summary>
    Spawn,

    /// <summary>The <c>[MsnShip]</c> or <c>[MsnSolar]</c> of the main object.</summary>
    MissionObject,

    /// <summary>The <c>[MsnFormation]</c> that lists the main object.</summary>
    Formation,

    /// <summary>The system object of the main object.</summary>
    SystemObject,
}

/// <summary>A position and an orientation of the mission data.</summary>
public sealed record SpacePose(Vector3 Position, Quaternion Orientation);

/// <summary>One <c>Act_CallThorn</c> line, one system of it, and what the rules found.</summary>
/// <param name="Line">The line.</param>
/// <param name="Trigger">The nickname of the trigger of the line, as the file spells it.</param>
/// <param name="System">The system, as the facts spell it, or null when no rule gives a system.</param>
/// <param name="SystemRule">The rule that gave the system, or null.</param>
/// <param name="SystemSource">What gave the system: for <see cref="SpaceSystemRule.Condition"/> the system, the base or
/// the object that the condition names, for example "the base Zz02_01_Base". For
/// <see cref="SpaceSystemRule.ActivatingTrigger"/> the nickname of the trigger. Else null.</param>
/// <param name="MainObject">Value 2 of the line, or <c>Player</c>.</param>
/// <param name="Start">The start pose of the main object, or null.</param>
/// <param name="StartRule">The rule that gave the start pose, or null.</param>
/// <param name="StartSource">The trigger or the section that gave the start pose, or null.</param>
/// <param name="Position">The position of the form <c>script, x, y, z</c>, or null.</param>
/// <param name="Problem">Why the line gives no system or no start, when the data of the line is bad, or null.</param>
public sealed record SpaceUse(
    ThnReference Line,
    string Trigger,
    string? System,
    SpaceSystemRule? SystemRule,
    string? SystemSource,
    string MainObject,
    SpacePose? Start,
    SpaceStartRule? StartRule,
    string? StartSource,
    Vector3? Position,
    string? Problem = null)
{
    /// <summary>The main object of a line without value 2.</summary>
    public const string Player = "Player";

    /// <summary>True when the main object is the player.</summary>
    public bool MainObjectIsPlayer => MainObject.Equals(Player, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The space lines of the scripts: each <c>Act_CallThorn</c> line, its system and the start pose of its main object
/// (decisions D641, D642, D644 and D650 of the space context design). The rules read the raw lines of the mission
/// files, so the spelling of the values stays as the files have it. The class reads each mission file once, and it
/// keeps the result of each script. Bad data never throws: a bad line or value gives no system or no start, and
/// <see cref="SpaceUse.Problem"/> tells why.
/// </summary>
public sealed class SpaceScenes
{
    private const string SpaceKey = "Act_CallThorn";

    private static readonly string[] SystemConditions = ["Cnd_SpaceEnter", "Cnd_JumpInComplete", "Cnd_SystemEnter"];
    private static readonly string[] BaseConditions = ["Cnd_LocEnter", "Cnd_BaseEnter", "Cnd_LaunchComplete"];
    private static readonly string[] ObjectConditions = ["Cnd_DistShip", "Cnd_DistCircle", "Cnd_DistVec"];

    private readonly Func<string, byte[]> read;
    private readonly ThnReferences references;
    private readonly SpaceFacts facts;
    private readonly Dictionary<string, MissionFile> files = new(VfsPath.Comparer);
    private readonly Dictionary<string, IReadOnlyList<SpaceUse>> results = new(VfsPath.Comparer);

    /// <param name="view">The view of the game data that the references came from.</param>
    public SpaceScenes(FileSystem view, ThnReferences references, SpaceFacts facts)
        : this(view.ReadAllBytes, references, facts)
    {
    }

    /// <param name="read">Reads the bytes of an install path. The tests count the reads.</param>
    internal SpaceScenes(Func<string, byte[]> read, ThnReferences references, SpaceFacts facts)
    {
        this.read = read;
        this.references = references;
        this.facts = facts;
    }

    /// <summary>The facts that the rules read.</summary>
    public SpaceFacts Facts => facts;

    /// <summary>
    /// The space lines of a script, one use for each system of each line (decision D642). The lines of a file in the
    /// folder of the script come first, then the other lines in scan order. A line without a system gives one use
    /// without a system. Empty when no <c>Act_CallThorn</c> line names the script.
    /// </summary>
    public IReadOnlyList<SpaceUse> Of(string thnPath)
    {
        var path = VfsPath.Normalize(thnPath) ?? thnPath;
        if (results.TryGetValue(path, out var known))
            return known;

        var lines = references.UsedBy(path).Where(r => r.Key.Equals(SpaceKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var folder = FolderOf(path);
        var ordered = lines.Where(l => VfsPath.Comparer.Equals(FolderOf(l.File), folder))
            .Concat(lines.Where(l => !VfsPath.Comparer.Equals(FolderOf(l.File), folder)));
        var seen = new Dictionary<(string, int, string), int>();
        var uses = new List<SpaceUse>();
        foreach (var line in ordered)
        {
            // A trigger can have two lines with the same value. The nth reference is the nth line.
            var key = (line.File.ToUpperInvariant(), line.SectionIndex, line.Value);
            var nth = seen.GetValueOrDefault(key);
            seen[key] = nth + 1;
            uses.AddRange(UsesOf(line, nth));
        }

        results[path] = uses;
        return uses;
    }

    private static string FolderOf(string path)
    {
        var normal = VfsPath.Normalize(path) ?? path;
        var index = normal.LastIndexOf('\\');
        return index < 0 ? "" : normal[..index];
    }

    private IEnumerable<SpaceUse> UsesOf(ThnReference line, int nth)
    {
        var file = File(line.File);
        if (file.Problem != null)
            return [Bad(line, "", file.Problem)];
        if (line.SectionIndex >= file.Sections.Count ||
            !file.Sections[line.SectionIndex].Name.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
            return [Bad(line, "", $"The line is not in a [Trigger] section of {line.File}.")];

        var section = file.Sections[line.SectionIndex];
        var trigger = Value(section, "nickname") ?? "";
        var entry = section.Where(e => e.Name.Equals(SpaceKey, StringComparison.OrdinalIgnoreCase) && e.Count > 0 &&
                                       string.Equals(e[0].ToString(), line.Value, StringComparison.Ordinal))
            .Skip(nth).FirstOrDefault();
        if (entry == null)
            return [Bad(line, trigger, $"The trigger {trigger} has no {SpaceKey} line for {line.Value}.")];

        // Act_CallThorn = script, x, y, z gives a position, and the main object is the player (decision D650).
        var position = Vector(entry, 1);
        var main = position == null && entry.Count > 1 && entry[1].ToString() is { Length: > 0 } name
            ? name
            : SpaceUse.Player;
        var systems = SystemsOf(file, section, trigger);
        if (systems.Count == 0)
            return [new SpaceUse(line, trigger, null, null, null, main, null, null, null, position)];

        return systems.Select(s =>
        {
            var start = StartOf(file, section, trigger, main, s.System);
            return new SpaceUse(line, trigger, s.System, s.Rule, s.Source, main, start?.Pose, start?.Rule,
                start?.Source, position);
        }).ToList();
    }

    private static SpaceUse Bad(ThnReference line, string trigger, string problem) =>
        new(line, trigger, null, null, null, SpaceUse.Player, null, null, null, null, problem);

    /// <summary>
    /// The systems of a trigger (decision D641): its system key, else its conditions, else the triggers that activate
    /// it, level by level, nearest first. The walk stops at the first level that gives a system, and it skips a trigger
    /// that it saw before. Each system is one entry, in the order of the values and of the file.
    /// </summary>
    private List<(string System, SpaceSystemRule Rule, string? Source)> SystemsOf(MissionFile file, Section section,
        string trigger)
    {
        var found = Own(section);
        var seen = new HashSet<string>([trigger], StringComparer.OrdinalIgnoreCase);
        var level = file.ParentsOf(trigger);
        while (found.Count == 0 && level.Count > 0)
        {
            level = level.Where(p => seen.Add(p) && file.Triggers.ContainsKey(p)).ToList();
            found = level.SelectMany(p => Own(file.Triggers[p]).Select(f => (f.System,
                SpaceSystemRule.ActivatingTrigger, (string?)file.Spelling(p)))).ToList();
            level = level.SelectMany(file.ParentsOf).ToList();
        }

        return found.DistinctBy(f => f.System, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The systems that one trigger gives itself: its system key, else each value of its conditions that names
    /// a system, a base or a system object.</summary>
    private List<(string System, SpaceSystemRule Rule, string? Source)> Own(Section section)
    {
        if (Value(section, "system") is { } key && facts.System(key) is { } keyed)
            return [(keyed, SpaceSystemRule.TriggerKey, null)];

        var found = new List<(string, SpaceSystemRule, string?)>();
        foreach (var entry in section)
        {
            var name = entry.Name;
            foreach (var value in entry.Select(v => v.ToString()).OfType<string>())
            {
                if (Is(name, SystemConditions) && facts.System(value) is { } system)
                    found.Add((system, SpaceSystemRule.Condition, $"the system {system}"));
                else if (Is(name, BaseConditions) && facts.SystemOfBase(value) is { } ofBase)
                    found.Add((ofBase, SpaceSystemRule.Condition, $"the base {value}"));
                else if (Is(name, ObjectConditions) && facts.Objects.TryGetValue(value, out var obj) &&
                         facts.System(obj.System) is { } ofObject)
                    found.Add((ofObject, SpaceSystemRule.Condition, $"the object {obj.Nickname}"));
            }
        }

        return found;
    }

    /// <summary>
    /// The start pose of the main object (decision D644). Rules a and b read the trigger and then the triggers that
    /// activate it, nearest first. Then the mission ship or solar, the formation, and the system object of the
    /// system.
    /// </summary>
    private (SpacePose Pose, SpaceStartRule Rule, string Source)? StartOf(MissionFile file, Section section,
        string trigger, string main, string system)
    {
        var player = main.Equals(SpaceUse.Player, StringComparison.OrdinalIgnoreCase);
        var chain = new[] { (Name: trigger, Section: section) }
            .Concat(file.Ancestors(trigger).Select(p => (Name: file.Spelling(p), Section: file.Triggers[p])));
        foreach (var (name, lines) in chain)
        {
            foreach (var entry in lines)
            {
                var key = entry.Name;
                var ship = entry.Count > 0 ? entry[0].ToString() : null;
                var named = string.Equals(ship, main, StringComparison.OrdinalIgnoreCase);
                (SpacePose?, SpaceStartRule) found = default;
                if (player && Is(key, ["Act_SetInitialPlayerPos"]))
                    found = (Pose(entry, 0, 3), SpaceStartRule.PlayerPosition);
                else if (player && Is(key, ["Act_MovePlayer"]))
                    found = (Pose(entry, 0, null), SpaceStartRule.PlayerPosition);
                else if (named && Is(key, ["Act_RelocateShip"]))
                    found = (Pose(entry, 1, 4), SpaceStartRule.Relocate);
                else if (!player && named && Is(key, ["Act_SpawnShip"]))
                    found = (Pose(entry, 2, 5), SpaceStartRule.Spawn);
                if (found.Item1 is { } pose)
                    return (pose, found.Item2, name);
            }
        }

        if (file.Objects.TryGetValue(main, out var mission) && Pose(mission, "position", "orientation") is { } placed)
            return (placed, SpaceStartRule.MissionObject, Value(mission, "nickname") ?? main);
        if (file.Formations.TryGetValue(main, out var formation) &&
            Pose(formation, "position", "orientation") is { } grouped)
            return (grouped, SpaceStartRule.Formation, Value(formation, "nickname") ?? main);
        if (facts.Objects.TryGetValue(main, out var obj) && obj.System.Equals(system, StringComparison.OrdinalIgnoreCase))
            return (new SpacePose(obj.Position, obj.Orientation), SpaceStartRule.SystemObject, obj.Nickname);
        return null;
    }

    /// <summary>The pose of a line: three numbers from an index, and an orientation of four numbers with w first from
    /// another index. A line without the orientation, or a kind of line that has none, gives the identity. Null when
    /// the position is not three numbers.</summary>
    private static SpacePose? Pose(Entry entry, int position, int? orientation) =>
        Vector(entry, position) is { } at
            ? new SpacePose(at, orientation is { } index ? Orientation(entry, index) : Quaternion.Identity)
            : null;

    private static SpacePose? Pose(Section section, string position, string orientation)
    {
        var at = First(section, position) is { } line ? Vector(line, 0) : null;
        if (at == null)
            return null;
        var turn = First(section, orientation) is { } turnLine ? Orientation(turnLine, 0) : Quaternion.Identity;
        return new SpacePose(at.Value, turn);
    }

    private static Vector3? Vector(Entry entry, int index) =>
        entry.Count >= index + 3 && Number(entry, index) is { } x && Number(entry, index + 1) is { } y &&
        Number(entry, index + 2) is { } z
            ? new Vector3(x, y, z)
            : null;

    /// <summary>A mission orientation: w, x, y and z, as the engine reads it.</summary>
    private static Quaternion Orientation(Entry entry, int index) =>
        entry.Count >= index + 4 && Number(entry, index) is { } w && Number(entry, index + 1) is { } x &&
        Number(entry, index + 2) is { } y && Number(entry, index + 3) is { } z
            ? new Quaternion(x, y, z, w)
            : Quaternion.Identity;

    private static float? Number(Entry entry, int index) =>
        entry[index].TryToSingle(out var value) && float.IsFinite(value) ? value : null;

    private static bool Is(string key, string[] names) =>
        names.Any(n => n.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static Entry? First(Section section, string key) =>
        section.FirstOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static string? Value(Section section, string key) =>
        First(section, key) is { Count: > 0 } entry ? entry[0].ToString() : null;

    private MissionFile File(string path)
    {
        if (files.TryGetValue(path, out var file))
            return file;
        try
        {
            file = new MissionFile(IniDocument.Parse(path, read(path)).Sections);
        }
        catch (Exception e)
        {
            file = new MissionFile([]) { Problem = $"The file {path} cannot be read: {e.Message}" };
        }

        files[path] = file;
        return file;
    }

    /// <summary>The sections of one mission file that the rules read, by nickname without case.</summary>
    private sealed class MissionFile
    {
        private readonly Dictionary<string, List<string>> parents = new(StringComparer.OrdinalIgnoreCase);

        public MissionFile(IReadOnlyList<Section> sections)
        {
            Sections = sections;
            foreach (var section in sections)
            {
                var nickname = Value(section, "nickname");
                switch (section.Name.ToLowerInvariant())
                {
                    case "trigger" when nickname != null:
                        Triggers.TryAdd(nickname, section);
                        foreach (var entry in section.Where(e => e.Name.Equals("Act_ActTrig",
                                     StringComparison.OrdinalIgnoreCase) && e.Count > 0))
                        {
                            var child = entry[0].ToString()!;
                            if (!parents.TryGetValue(child, out var list))
                                parents[child] = list = [];
                            list.Add(nickname);
                        }

                        break;
                    case "msnship" or "msnsolar" when nickname != null:
                        Objects.TryAdd(nickname, section);
                        break;
                    case "msnformation":
                        foreach (var ship in section.Where(e => e.Name.Equals("ship", StringComparison.OrdinalIgnoreCase)
                                                                && e.Count > 0))
                            Formations.TryAdd(ship[0].ToString()!, section);
                        break;
                }
            }
        }

        public IReadOnlyList<Section> Sections { get; }

        public string? Problem { get; init; }

        public Dictionary<string, Section> Triggers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Section> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Section> Formations { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The triggers that activate a trigger, in file order.</summary>
        public List<string> ParentsOf(string trigger) => parents.TryGetValue(trigger, out var list) ? list : [];

        /// <summary>The triggers that activate a trigger, and the triggers that activate them, nearest first, each
        /// once.</summary>
        public IEnumerable<string> Ancestors(string trigger)
        {
            var seen = new HashSet<string>([trigger], StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(ParentsOf(trigger));
            while (queue.TryDequeue(out var next))
            {
                if (!seen.Add(next) || !Triggers.ContainsKey(next))
                    continue;
                yield return next;
                foreach (var parent in ParentsOf(next))
                    queue.Enqueue(parent);
            }
        }

        /// <summary>The nickname of a trigger as the file spells it.</summary>
        public string Spelling(string trigger) =>
            Triggers.TryGetValue(trigger, out var section) ? Value(section, "nickname") ?? trigger : trigger;
    }
}
