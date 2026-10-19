using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The glyphs of the mission studio: the event groups of the conditions, the action groups of the chips, the behavior
/// prefabs and the pins of the graph. The graph, the Hierarchy and the Inspector take their glyphs from here, so each
/// item looks the same everywhere.
/// </summary>
internal static class MissionGlyphs
{
    /// <summary>The glyph of an event group of a condition (section 13.2 of the M6a design).</summary>
    public static char OfEvent(string? group) => group switch
    {
        "start" => EditorIcons.Play,
        "talk" => EditorIcons.Comments,
        "place" => EditorIcons.MapMarkerAlt,
        "distance" => EditorIcons.Bullseye,
        "combat" => EditorIcons.Crosshairs,
        "story" => EditorIcons.CodeBranch,
        _ => EditorIcons.QuestionCircle,
    };

    /// <summary>The glyph of an action group of a chip (section 13.3 of the M6a design).</summary>
    public static char OfGroup(string? group) => group switch
    {
        "starts" => EditorIcons.Play,
        "stops" => EditorIcons.Stop,
        "checkpoint" => EditorIcons.Save,
        "end" => EditorIcons.Flag,
        "spawn" => EditorIcons.SpaceShuttle,
        "orders" => EditorIcons.Route,
        "sides" => EditorIcons.Users,
        "armor" => EditorIcons.ShieldAlt,
        "area" => EditorIcons.VectorSquare,
        "warnings" => EditorIcons.Bell,
        "comms" => EditorIcons.VolumeUp,
        "music" => EditorIcons.Music,
        "objective" => EditorIcons.Bullseye,
        "cutscene" => EditorIcons.Film,
        "player" => EditorIcons.User,
        "world" => EditorIcons.Globe,
        "debug" => EditorIcons.Wrench,
        _ => EditorIcons.Bolt,
    };

    /// <summary>The glyph of a behavior prefab (section 8.2 of the M6a design).</summary>
    public static char OfPrefab(string? id) => id switch
    {
        "checkpoint" => EditorIcons.Save,
        "offer" => EditorIcons.Store,
        "spaceCutscene" => EditorIcons.Film,
        "branch" => EditorIcons.CodeBranch,
        "ambush" => EditorIcons.Crosshairs,
        "spawnWave" => EditorIcons.SpaceShuttle,
        "destroyTargets" => EditorIcons.Bomb,
        "conversation" => EditorIcons.Comments,
        "success" => EditorIcons.Flag,
        "fail" => EditorIcons.Ban,
        "keepApplying" => EditorIcons.Sync,
        "setReputation" => EditorIcons.Users,
        "lockGate" => EditorIcons.Lock,
        "objective" => EditorIcons.Bullseye,
        "watchedState" => EditorIcons.Eye,
        "escort" => EditorIcons.ShieldAlt,
        "patrol" => EditorIcons.Route,
        _ => EditorIcons.Cubes,
    };

    /// <summary>The glyph of a pin (section 6.2 of the M6a design): an arrow for Start, a bar for Stop, a disk for Load
    /// and an eye for a watch.</summary>
    public static char OfPin(GraphPinKind kind) => kind switch
    {
        GraphPinKind.Start or GraphPinKind.Starts => EditorIcons.Play,
        GraphPinKind.Stop or GraphPinKind.Stops => EditorIcons.Stop,
        GraphPinKind.Load or GraphPinKind.Saves => EditorIcons.Save,
        GraphPinKind.Watches or GraphPinKind.State => EditorIcons.Eye,
        _ => EditorIcons.CaretRight,
    };

    /// <summary>The glyph of a frame or of its collapsed node.</summary>
    public static char OfFrame(GraphFrame frame) => frame.Kind switch
    {
        GraphFrameKind.Prefab => OfPrefab(frame.Instance?.Definition.Id),
        GraphFrameKind.NotStarted => EditorIcons.EyeSlash,
        _ => EditorIcons.Film,
    };
}
