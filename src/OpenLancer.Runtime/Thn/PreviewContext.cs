using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Runtime.Thn;

/// <summary>How the preview plays a cutscene script (section 8.2 of the M8 design).</summary>
public enum PreviewContextKind
{
    /// <summary>The script alone, without a room. The context has only the player ship of the Play package.</summary>
    ScriptOnly,

    /// <summary>The script in its room, as the game builds the room.</summary>
    Room,

    /// <summary>The START_MOTION events of the script on a stand-in character, as the game plays a fidget.</summary>
    Fidget,

    /// <summary>A mission script that the game plays in space. The preview plays it in the system of its mission line,
    /// or alone when no rule finds the system.</summary>
    Space,
}

/// <summary>How a plan plays one of its scripts. Some plays do not run all events of the script (section 7.3 of the M8
/// design).</summary>
public enum ScriptPlay
{
    /// <summary>The events run.</summary>
    Normal,

    /// <summary>The script is the set script of the room. The game makes its entities and runs none of its events
    /// (Cutscene.cs:279-283).</summary>
    Set,

    /// <summary>The script plays as a fidget. The game runs only its START_MOTION events (Cutscene.cs:229-241).</summary>
    Fidget,
}

/// <summary>
/// The context of a preview: the kind, the room, the fixture spot of a fidget and the ship package of the player ship.
/// <see cref="Suggest"/> gives the context that copies the game, and <see cref="Plan"/> gives the scripts and the room
/// data that a build uses.
/// </summary>
/// <param name="Base">The nickname of the base of a room, or null.</param>
/// <param name="Room">The nickname of the room, or null.</param>
/// <param name="Fixture">The stand marker of the room NPC whose fidget the script is, or null.</param>
/// <param name="ShipPackage">The ship package of the player ship, or null for the package of a new multiplayer
/// character (decision D15 of M4).</param>
public sealed record PreviewContext(PreviewContextKind Kind, string? Base, string? Room, string? Fixture,
    string? ShipPackage)
{
    /// <summary>The context of a script alone.</summary>
    public static readonly PreviewContext ScriptOnly = new(PreviewContextKind.ScriptOnly, null, null, null, null);

    /// <summary>The nickname of the system of a Space context, as the game data spells it, or null.</summary>
    public string? System { get; init; }

    /// <summary>The mission line of a Space context, or null.</summary>
    public SpaceUse? Use { get; init; }

    /// <summary>The templates that the context of a room fills in with the terrain of its base
    /// (ThnRoomHandler.cs:23-51, ThnScriptInstance.cs:106-109).</summary>
    private static readonly (string Template, Func<Base, string?> Terrain)[] TerrainTemplates =
    [
        ("$terrain_tiny", b => b.TerrainTiny),
        ("$terrain_sml", b => b.TerrainSml),
        ("$terrain_mdm", b => b.TerrainMdm),
        ("$terrain_lrg", b => b.TerrainLrg),
        ("$terrain_dyna_01", b => b.TerrainDyna1),
        ("$terrain_dyna_02", b => b.TerrainDyna2),
    ];

    /// <summary>The name of the context for the toolbar, for example "Room: Li01_01_base, Cityscape".</summary>
    public string Label => Kind switch
    {
        PreviewContextKind.Room => $"Room: {Base}, {Room}",
        PreviewContextKind.Fidget when Room != null => $"Fidget: {Base}, {Room}, {Fixture ?? "origin"}",
        PreviewContextKind.Fidget => "Fidget: stand-in at the origin",
        PreviewContextKind.Space => System != null ? $"Space: {System}" : "Space",
        _ => "Script only",
    };

    /// <summary>The context that copies how the game plays a script. It is the first of <see cref="Choices"/>.</summary>
    public static PreviewContext Suggest(string path, ThnReferences references, GameItemDb items) =>
        Choices(path, references, items)[0];

    /// <summary>
    /// The contexts that fit a script, the best first: each room that plays it and that the game data has, a fidget
    /// without a room, the space contexts of a mission script, and always the script alone at the end.
    /// </summary>
    /// <param name="spaces">The space lines of the game data. With them, each system of the lines of the script is one
    /// Space context, in the order of decision D642, and a script whose lines give no system gets one Space context
    /// without a system. Without them, a mission script gets one Space context without a system.</param>
    public static IReadOnlyList<PreviewContext> Choices(string path, ThnReferences references, GameItemDb items,
        SpaceScenes? spaces = null)
    {
        var choices = new List<PreviewContext>();
        void Add(PreviewContext context)
        {
            if (!choices.Any(c => Same(c, context)))
                choices.Add(context);
        }

        foreach (var use in references.Rooms(path))
        {
            if (FindRoom(items, use.Base, use.Room) == null)
                continue;
            Add(use.Role == ThnRoomRole.Fixture
                ? new PreviewContext(PreviewContextKind.Fidget, use.Base, use.Room, use.Spot, null)
                : new PreviewContext(PreviewContextKind.Room, use.Base, use.Room, null, null));
        }
        if (references.IsFidget(path))
            Add(new PreviewContext(PreviewContextKind.Fidget, null, null, null, null));
        if (spaces != null)
        {
            foreach (var space in SpaceChoices(spaces.Of(path)))
                Add(space);
        }
        else if (references.InSpace(path))
        {
            Add(new PreviewContext(PreviewContextKind.Space, null, null, null, null));
        }

        Add(ScriptOnly);
        return choices;
    }

    /// <summary>One Space context for each system of the lines, with the first line of that system. Lines without a
    /// system give one Space context without a system, but only when no line gives a system.</summary>
    internal static IEnumerable<PreviewContext> SpaceChoices(IReadOnlyList<SpaceUse> uses)
    {
        var systems = uses.Where(u => u.System != null).DistinctBy(u => u.System, StringComparer.OrdinalIgnoreCase)
            .Select(u => new PreviewContext(PreviewContextKind.Space, null, null, null, null) { System = u.System, Use = u })
            .ToList();
        return systems.Count > 0 || uses.Count == 0
            ? systems
            : [new PreviewContext(PreviewContextKind.Space, null, null, null, null) { Use = uses[0] }];
    }

    /// <summary>
    /// The plan of a build of this context, in the order of the game (section 8.2). The plan is pure data. The build
    /// reads the open script from W, and each other script from the session.
    /// </summary>
    /// <param name="openPath">The install path of the open script.</param>
    /// <param name="script">W of the open script, or null. A Script only plan of a script with <c>$terrain_*</c>
    /// templates gets a note that names the room context, because only a room fills in these templates.</param>
    /// <exception cref="ArgumentException">The game data has no room of this context.</exception>
    public PreviewPlan Plan(string openPath, GameItemDb items, ThnReferences references, ThnScriptModel? script = null)
    {
        var open = VfsPath.Normalize(openPath) ?? throw new ArgumentException($"{openPath} is not an install path.",
            nameof(openPath));
        var none = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (Kind)
        {
            case PreviewContextKind.Room:
                return RoomPlan(open, items, references);
            case PreviewContextKind.Fidget when Room != null:
                return RoomPlan(null, items, references) with
                {
                    Fidget = open,
                    FidgetSpot = Fixture,
                };
            case PreviewContextKind.Fidget:
                return new PreviewPlan(null, [], null, null, null, null, none)
                {
                    Fidget = open,
                    ShipPackage = ShipPackage,
                };
            case PreviewContextKind.Space:
                // The main object binds only to a main_object marker of the script.
                var main = script == null || HasMainObject(script) ? Use : null;
                return new PreviewPlan(null, [open], open, null, null, null, none)
                {
                    ShipPackage = ShipPackage,
                    Note = SpaceNote(System, Use),
                    System = System,
                    MainObject = main?.MainObject,
                    MainObjectStart = main?.Start,
                    ScriptPosition = Use?.Position,
                };
            default:
                return new PreviewPlan(null, [open], open, null, null, null, none)
                {
                    ShipPackage = ShipPackage,
                    Note = script != null && HasTerrainTemplates(script) ? TemplateNote(open, items, references) : null,
                };
        }
    }

    /// <summary>The note of a Space plan (section 3.3 of the cutscene fixes plan).</summary>
    internal static string SpaceNote(string? system, SpaceUse? use) => system != null
        ? "The game plays this script in space with the mission objects. It makes none of the script objects. The " +
          $"preview shows the system {system}, and the script objects as stand-ins."
        : $"No rule finds the system of the mission line of the trigger {use?.Trigger}. The preview shows the script " +
          "without a system. Give the trigger a system key, or a condition that names a system or a base.";

    /// <summary>An entity of the script has the user property <c>main_object</c>. The engine reads the user properties
    /// without case (ThnScript.cs).</summary>
    private static bool HasMainObject(ThnScriptModel script) =>
        script.Entities.Any(e => e.Table["userprops"] is ThnTable props &&
                                 props.Record.Any(f => f.Key.Equals("main_object", StringComparison.OrdinalIgnoreCase)));

    /// <summary>An entity of the script has a <c>$terrain_*</c> template. The engine compares the names without case
    /// (ThnScriptContext.cs:18).</summary>
    private static bool HasTerrainTemplates(ThnScriptModel script) =>
        script.Entities.Any(e => e.Table["template_name"] is ThnText name &&
                                 TerrainTemplates.Any(t => t.Template.Equals(name.Value,
                                     StringComparison.OrdinalIgnoreCase)));

    /// <summary>The note of a script with <c>$terrain_*</c> templates outside its room. It names the first room that
    /// plays the script.</summary>
    private static string TemplateNote(string open, GameItemDb items, ThnReferences references)
    {
        const string templates = "This script has $terrain_* templates, and only the context of a room fills them in.";
        return Choices(open, references, items).FirstOrDefault(c => c.Kind == PreviewContextKind.Room) is { } room
            ? $"{templates} Choose {room.Label} in the context list."
            : $"{templates} No room of the game data plays this script.";
    }

    /// <summary>
    /// The plan of the room: the set script, the scene scripts in INI order and then the open script, each path once.
    /// When the open script is the set script, a scene script or a mission ambient of the room, the room plays without a
    /// cutscene, so the plan has the room camera and the ship marker (RoomGameplay.cs:1406-1427, :1489-1491). Else the
    /// open script plays as the cutscene.
    /// </summary>
    /// <param name="open">The open script, or null for the room without a cutscene.</param>
    private PreviewPlan RoomPlan(string? open, GameItemDb items, ThnReferences references)
    {
        var dock = items.Bases.Get(Base) ?? throw new ArgumentException($"The game data has no base {Base}.");
        var room = dock.Rooms.Get(Room) ?? throw new ArgumentException($"The base {Base} has no room {Room}.");
        var set = PathOf(room.SetScript);
        var scripts = new List<string>();
        foreach (var scene in room.SceneScripts)
        {
            if (PathOf(scene.Thn) is { } path && !scripts.Contains(path, VfsPath.Comparer))
                scripts.Add(path);
        }

        var uses = open == null ? [] : references.Rooms(open).Where(IsThisRoom).ToList();
        var ambient = uses.Any(u => u.Role == ThnRoomRole.Ambient);
        var isRoomScript = open == null || VfsPath.Comparer.Equals(open, set) ||
                           scripts.Contains(open, VfsPath.Comparer) || ambient;
        // A mission ambient plays after the scene scripts (RoomGameplay.cs:1053), and a cutscene plays last
        // (RoomGameplay.cs:1458).
        if (open != null && !VfsPath.Comparer.Equals(open, set) && !scripts.Contains(open, VfsPath.Comparer))
            scripts.Add(open);

        string? note = null;
        if (uses.Any(u => u.Role == ThnRoomRole.Cart) && !isRoomScript)
            note = "The game does not play the goods cart scripts today (ResourceLoadExtensions.cs:63 has no caller).";
        return new PreviewPlan(set, scripts, isRoomScript ? null : open, isRoomScript ? room.Camera : null,
            isRoomScript ? room.PlayerShipPlacement : null, null, Substitutions(dock))
        {
            Base = dock.Nickname,
            Room = room.Nickname,
            Encounter = isRoomScript ? null : uses.FirstOrDefault(u => u.Role == ThnRoomRole.Encounter)?.File,
            ShipPackage = ShipPackage,
            Note = note,
        };
    }

    /// <summary>The <c>$terrain_*</c> templates of a base, as <c>ThnRoomHandler.CreateContext</c> gives them
    /// (ThnRoomHandler.cs:23-51). Only the base's own values: a key without a value is not there.</summary>
    internal static IReadOnlyDictionary<string, string> Substitutions(Base dock)
    {
        var substitutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (template, terrain) in TerrainTemplates)
        {
            if (terrain(dock) is { } value)
                substitutions.Add(template, value);
        }

        return substitutions;
    }

    private bool IsThisRoom(ThnRoomUse use) =>
        use.Base.Equals(Base, StringComparison.OrdinalIgnoreCase) &&
        use.Room.Equals(Room, StringComparison.OrdinalIgnoreCase);

    private static BaseRoom? FindRoom(GameItemDb items, string dock, string room) => items.Bases.Get(dock)?.Rooms.Get(room);

    /// <summary>The install path of a script of the game data, in normal form, or null.</summary>
    private static string? PathOf(ResolvedThn? thn) => thn?.DataPath is { } path ? VfsPath.Normalize(path) : null;

    /// <summary>Two contexts with the same kind, room and fixture. The nicknames compare without case, as the game data
    /// finds them.</summary>
    public static bool Same(PreviewContext a, PreviewContext b) =>
        a.Kind == b.Kind && string.Equals(a.Base, b.Base, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Room, b.Room, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Fixture, b.Fixture, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.System, b.System, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a build of the preview makes, as pure data (section 8.2 of the M8 design). The paths are install paths in
/// normal form.
/// </summary>
/// <param name="SetScript">The set script of the room: the build makes its entities and runs none of its events.</param>
/// <param name="Scripts">The scripts that the scene plays, in order, each path once. The cutscene is the last.</param>
/// <param name="Cutscene">The script that plays as the cutscene, or null when the room plays without one.</param>
/// <param name="RoomCamera">The <c>[Camera] name</c> of the room, which the build sets after <c>BeginScene</c> when the
/// room plays without a cutscene.</param>
/// <param name="ShipMarker">The <c>[PlayerShipPlacement] name</c> of the room, where the build puts the player ship when
/// the room plays without a cutscene.</param>
/// <param name="FidgetSpot">The marker where the stand-in of a fidget stands, or null for a marker at the origin.</param>
/// <param name="Substitutions">The <c>$terrain_*</c> templates of the base.</param>
public sealed record PreviewPlan(string? SetScript, IReadOnlyList<string> Scripts, string? Cutscene,
    string? RoomCamera, string? ShipMarker, string? FidgetSpot, IReadOnlyDictionary<string, string> Substitutions)
{
    /// <summary>The nickname of the base of the room, or null.</summary>
    public string? Base { get; init; }

    /// <summary>The nickname of the room, or null. The build binds its fixtures to the characters of the
    /// cutscene.</summary>
    public string? Room { get; init; }

    /// <summary>The script that plays as a fidget on a stand-in character, or null.</summary>
    public string? Fidget { get; init; }

    /// <summary>The RTC file whose <c>[Char]</c> lines bind the fixtures to the characters of the cutscene
    /// (RoomGameplay.cs:1447-1448), or null.</summary>
    public string? Encounter { get; init; }

    /// <summary>The ship package of the player ship, or null for the package of a new multiplayer character.</summary>
    public string? ShipPackage { get; init; }

    /// <summary>How the game plays the script differently, for a banner over the view, or null.</summary>
    public string? Note { get; init; }

    /// <summary>The nickname of the system that a Space plan plays in, or null.</summary>
    public string? System { get; init; }

    /// <summary>The main object of a Space plan whose script has a <c>main_object</c> marker, or null.</summary>
    public string? MainObject { get; init; }

    /// <summary>The start pose of the main object in the mission data, or null (decision D644).</summary>
    public SpacePose? MainObjectStart { get; init; }

    /// <summary>The position of the form <c>Act_CallThorn = script, x, y, z</c>, or null. The preview does not move the
    /// script to it (decision D650).</summary>
    public Vector3? ScriptPosition { get; init; }

    /// <summary>How the plan plays a script: as the set script of the room, as the fidget, or else with all its
    /// events.</summary>
    /// <param name="path">An install path, in normal form.</param>
    public ScriptPlay PlayOf(string path) =>
        VfsPath.Comparer.Equals(path, SetScript) ? ScriptPlay.Set
        : VfsPath.Comparer.Equals(path, Fidget) ? ScriptPlay.Fidget
        : ScriptPlay.Normal;
}
