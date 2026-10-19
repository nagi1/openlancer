using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LibreLancer;

namespace OpenLancer.Data;

/// <summary>The class of a kind of engine line (section 4.5 of the console filter design).</summary>
public enum EngineLineClass
{
    /// <summary>The game data has a problem, and the retail game runs with it.</summary>
    DataProblem,

    /// <summary>The data is valid for the retail game, but the OpenLancer engine does not read it yet.</summary>
    EngineGap,

    /// <summary>A note about the speed of the game.</summary>
    PerformanceNote,

    /// <summary>A line about the state of the engine process, not about the data.</summary>
    ProcessLine,
}

/// <summary>What the Console does with a line of a kind.</summary>
public enum EngineLineDisposition
{
    /// <summary>One entry for each line.</summary>
    Entry,

    /// <summary>No entry. The log file keeps the line (decision D506).</summary>
    Drop,

    /// <summary>One info entry for each Play run (decision D507).</summary>
    Notice,
}

/// <summary>The data sets of the pins: the base folders that the retail tests know.</summary>
public enum EngineLineDataSet
{
    /// <summary>The retail game 1.0 or 1.1. Both give the same lines.</summary>
    Vanilla,

    /// <summary>Freelancer: HD Edition 0.7.1.</summary>
    HdEdition071,
}

/// <summary>The item that a line of a component without a file names.</summary>
public enum EngineLineSubject
{
    /// <summary>The nickname of a <c>[Ship]</c> of the ship files.</summary>
    Ship,

    /// <summary>The nickname of a <c>[Pilot]</c> of the pilot files.</summary>
    Pilot,
}

/// <summary>A pattern of the lines of a kind: the component, the message without its place, and the section type.</summary>
/// <param name="Fallback">True for a pattern that applies only when no other pattern matches, for example each unknown
/// entry that no engine gap names.</param>
public sealed record EngineLinePattern(string Component, Regex Core, Regex? Section = null, bool Fallback = false)
{
    public bool Matches(EngineLine line) =>
        string.Equals(line.Component, Component, StringComparison.OrdinalIgnoreCase) && Core.IsMatch(line.Core) &&
        (Section == null || line.Section != null && Section.IsMatch(line.Section));
}

/// <summary>One kind of engine line: its id, class, text and patterns, and the number of its lines in each data set
/// that the base game gives.</summary>
/// <param name="Id">For example B01 or G13 (sections 3.1 to 3.3 of the console audit).</param>
/// <param name="Text">What the line means, without the origin, for the details pane.</param>
/// <param name="Sample">A synthetic line of the kind for the tests.</param>
/// <param name="Pins">The number of lines of the kind that the base run of each data set gives, before the fork
/// commits OL-0039 to OL-0052. A fork commit that removes lines lowers its pin in the same change.</param>
public sealed record EngineLineKind(string Id, EngineLineClass Class, string Text, IReadOnlyList<EngineLinePattern> Patterns,
    string Sample, IReadOnlyDictionary<EngineLineDataSet, int> Pins)
{
    public EngineLineDisposition Disposition { get; init; } = EngineLineDisposition.Entry;

    /// <summary>True when the line is about the work of OpenLancer itself, so its origin is OpenLancer, for example the
    /// new-character save of Play that the engine builds itself (G15).</summary>
    public bool OpenLancerOrigin { get; init; }

    /// <summary>The kind line of the details pane: "{class}. {text}".</summary>
    public string DetailText => $"{ClassName(Class)}. {Text}";

    /// <summary>The name of a class for the user.</summary>
    public static string ClassName(EngineLineClass value) => value switch
    {
        EngineLineClass.DataProblem => "Data problem",
        EngineLineClass.EngineGap => "Engine gap",
        EngineLineClass.PerformanceNote => "Performance note",
        _ => "Process line",
    };
}

/// <summary>
/// The kinds of engine lines that the data load, the Scene, the story missions and Play give (section 4.5 of the
/// console filter design). A kind gives the text of the details pane of the Console, and it can drop a line or make it
/// a notice. The kind does not decide the origin of a line, except for the lines about OpenLancer itself.
/// </summary>
/// <remarks>
/// <para>The texts tell what the retail game does with the data, in plain words. The audit took each fact from the
/// retail game and the engine source.</para>
/// <para>A line can match more than one pattern, for example each "Duplicate of" line and the "Duplicate of
/// animation" line of a room. <see cref="Match"/> tries the patterns that are not fallbacks first, in the order of the
/// table, then the fallbacks.</para>
/// </remarks>
public static class EngineLineRules
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly IReadOnlyDictionary<EngineLineDataSet, int> NoPins = new Dictionary<EngineLineDataSet, int>();

    // The fields that the table uses come first, because static fields start in the order of the text.
    private static readonly Regex SlowPattern = Pattern(@"^(Still )?running slow: update took (?<ms>[0-9.]+) ?ms$");
    private static readonly Regex ShipSubject = Pattern(@"^Unrecognised hp_type \S+ in (?<nickname>\S+)$");
    private static readonly Regex PilotSubject = Pattern(@"^(?<nickname>[^:\s]+): ");

    /// <summary>The kinds in the order of the console audit.</summary>
    public static IReadOnlyList<EngineLineKind> Kinds { get; } =
    [
        Kind("B01", EngineLineClass.DataProblem,
            "A start effect has no effect line. The game ignores the start effect. OpenLancer does the same.",
            "[Error: 12:00:00] Ini: Missing required field effect at section start_effect: EXE\\..\\data\\fx\\sample.ini, line 4",
            36, 36, Ini(@"Missing required field effect", "start_effect")),
        Kind("B02", EngineLineClass.DataProblem,
            "The game does not read a particles line in a start effect. The game ignores the line.",
            "[Warning: 12:00:00] Ini: Unknown entry particles at section start_effect: EXE\\..\\data\\fx\\sample.ini, line 5",
            3, 3, Ini(@"Unknown entry particles", "start_effect")),
        Kind("B03", EngineLineClass.DataProblem,
            "The game does not read explosion_impulse in an asteroid mine. The game ignores the line.",
            "[Warning: 12:00:00] Ini: Unknown entry explosion_impulse in section AsteroidMine: EXE\\..\\data\\solar\\sample.ini (line not available)",
            3, 3, Ini(@"Unknown entry explosion_impulse", "AsteroidMine")),
        Kind("B04", EngineLineClass.DataProblem,
            "The game reads this key one time, but the section has more lines of it. The game uses the last line.",
            "[Warning: 12:00:00] Ini: Duplicate of ids_info at section Object: EXE\\..\\data\\universe\\sample.ini, line 9",
            45, 3, Fallback("Ini", @"Duplicate of \S+")),
        Kind("B05", EngineLineClass.DataProblem,
            "The game does not know this key. The game ignores the line.",
            "[Warning: 12:00:00] Ini: Unknown entry nicknam at section Zone: EXE\\..\\data\\universe\\sample.ini, line 7",
            32, 0, Fallback("Ini", @"Unknown entry .+")),
        Kind("B06", EngineLineClass.DataProblem,
            "The game does not read this key or section here. The game ignores it.",
            "[Warning: 12:00:00] Ini: Unknown entry name at section SystemInfo: EXE\\..\\data\\universe\\sample.ini, line 3",
            41, 0,
            Ini(@"Unknown entry name", "SystemInfo"),
            Ini(@"Unknown entry behavior", "LightSource"),
            Ini(@"Unknown entry LODranges", "fuse"),
            Ini(@"Unknown entry (dmg_hp|dmg_obj|separable)", "destroy_group"),
            Fallback("Ini", @"Unknown section \S+")),
        Kind("B07", EngineLineClass.DataProblem,
            "The line has another number of values than the game reads. The game reads the values that it needs.",
            "[Warning: 12:00:00] Ini: Too many components for pos in section Formation: EXE\\..\\data\\missions\\sample.ini (line not available)",
            3, 0, Fallback("Ini", @"(Too many|Not enough) components for \S+")),
        Kind("B08", EngineLineClass.DataProblem,
            "The pilot names a gun block that does not exist. The game uses the default gun behavior.",
            "[Warning: 12:00:00] Pilot: sample_pilot: Unable to find GunBlock 'sample_guns'",
            1, 0, new EngineLinePattern("Pilot", Pattern(@"^[^:]+: Unable to find GunBlock '"))),
        Kind("B09", EngineLineClass.DataProblem,
            "The sphere file does not have all six sides. The game shows the glow of the sun from the star data.",
            "[Warning: 12:00:00] Sph: Sph EXE\\..\\data\\solar\\sample.sph does not contain all 6 sides and will not render",
            1, 1, new EngineLinePattern("Sph", Pattern(@"^Sph .+ does not contain all 6 sides and will not render$"))),
        Kind("G01", EngineLineClass.EngineGap,
            "The game knows this generic hardpoint type. The OpenLancer engine does not know it yet, so the ship has no " +
            "mount point of this type.",
            "[Error: 12:00:00] Ship: Unrecognised hp_type hp_turret in sample_ship",
            12, 12, new EngineLinePattern("Ship",
                Pattern(@"^Unrecognised hp_type (hp_gun|hp_turret|hp_torpedo|hp_shield_generator|hp_cargo_pod) in \S+$"))),
        Kind("G02", EngineLineClass.EngineGap,
            "The game reads one lifetime value as the minimum and the maximum. The OpenLancer engine needs two values.",
            "[Error: 12:00:00] Ini: Not enough components for lifetime at section explosion: EXE\\..\\data\\fx\\sample.ini, line 11",
            23, 23, Ini(@"Not enough components for lifetime", "explosion")),
        Kind("G03", EngineLineClass.EngineGap,
            "The game picks a random time between the two at_t values. The OpenLancer engine reads only one value.",
            "[Warning: 12:00:00] Ini: Too many components for at_t at section destroy_group: EXE\\..\\data\\fx\\sample.ini, line 13",
            17, 17, Ini(@"Too many components for at_t")),
        Kind("G04", EngineLineClass.EngineGap,
            "The game runs this fuse action. The OpenLancer engine does not run it yet.",
            "[Warning: 12:00:00] Ini: Unknown section tumble at EXE\\..\\data\\fx\\sample.ini, line 6",
            13, 13, Ini(@"Unknown section (tumble|damage_root|damage_group|damage_hp_attachment|make_invincible|dump_cargo)")),
        Kind("G05", EngineLineClass.EngineGap,
            "The game attaches this particle effect to the asteroid. The OpenLancer engine does not show it yet.",
            "[Warning: 12:00:00] Ini: Unknown entry particle_effect at section DynamicAsteroid: EXE\\..\\data\\solar\\sample.ini, line 8",
            3, 3, Ini(@"Unknown entry particle_effect", "DynamicAsteroid")),
        Kind("G06", EngineLineClass.EngineGap,
            "The game uses this level of detail on low detail settings. OpenLancer does not need it.",
            "[Warning: 12:00:00] Ini: Unknown entry MinSpecLOD at section Simple: EXE\\..\\data\\ships\\sample.ini, line 6",
            1, 1, Ini(@"Unknown entry MinSpecLOD", "Simple")),
        Kind("G07", EngineLineClass.EngineGap,
            "The game changes the color of the light with this curve. The OpenLancer engine does not change it yet.",
            "[Warning: 12:00:00] Ini: Unknown entry color_curve at section LightSource: EXE\\..\\data\\universe\\sample.ini, line 50",
            2, 2, Ini(@"Unknown entry color_curve", "LightSource")),
        Kind("G08", EngineLineClass.EngineGap,
            "The game plays each animation line of a room. The OpenLancer engine does not play room animations yet.",
            "[Warning: 12:00:00] Ini: Duplicate of animation at section Room_Info: EXE\\..\\data\\universe\\sample.ini, line 5",
            0, 1, Ini(@"Duplicate of animation", "Room_Info")),
        Kind("G09", EngineLineClass.EngineGap,
            "The game plays each effect line of an explosion. The OpenLancer engine plays only the last one.",
            "[Warning: 12:00:00] Ini: Duplicate of effect in section explosion: EXE\\..\\data\\fx\\sample.ini (line not available)",
            2, 0, Ini(@"Duplicate of effect", "explosion")),
        Kind("G10", EngineLineClass.EngineGap,
            "The game reads this ship key. The OpenLancer engine does not use it yet.",
            "[Warning: 12:00:00] Ini: Unknown entry docking_sphere in section Ship: EXE\\..\\data\\ships\\sample.ini (line not available)",
            7, 0, Ini(@"Unknown entry (camera_angular_slerp_multiplier|distance_render|docking_camera|docking_sphere)", "Ship")),
        Kind("G11", EngineLineClass.EngineGap,
            "The game shows this picture for a new character. OpenLancer does not need it.",
            "[Warning: 12:00:00] Ini: Unknown entry thumb in section Pilot: EXE\\..\\data\\characters\\sample.ini (line not available)",
            1, 0, Ini(@"Unknown entry thumb", "Pilot")),
        Kind("G12", EngineLineClass.EngineGap,
            "The game reads this mission section. The OpenLancer engine does not run it yet.",
            "[Warning: 12:00:00] Ini: Unknown section MsnRandEnc at EXE\\..\\data\\missions\\sample.ini, line 20",
            13, 1, Ini(@"Unknown section (MsnRandEnc|MetaBehavior)")),
        Kind("G13", EngineLineClass.EngineGap,
            "The game shows the text of an objective of type base. The OpenLancer engine does not show it yet.",
            "[Error: 12:00:00] Ini: Invalid value for enum base at section NNObjective: EXE\\..\\data\\missions\\sample.ini, line 21",
            1, 1, Ini(@"Invalid value for enum base", "NNObjective")),
        Kind("G14", EngineLineClass.EngineGap,
            "The game reads the label of a mission formation. The OpenLancer engine does not use it yet.",
            "[Warning: 12:00:00] Ini: Unknown entry label in section MsnFormation: EXE\\..\\data\\missions\\sample.ini (line not available)",
            1, 0, Ini(@"Unknown entry label", "MsnFormation")),
        Kind("G15", EngineLineClass.EngineGap,
            "The OpenLancer engine reads the cargo of a new character with the values in the wrong places. The cargo " +
            "gets the health 0.",
            "[Error: 12:00:00] Ini: Failed to parse float '' for cargo in section Player: sample: new_player:16",
            0, 0, Ini(@"Failed to parse float '' for cargo", "Player")) with { OpenLancerOrigin = true },
        Kind("O01", EngineLineClass.ProcessLine,
            "A second data load in one process replaces the global material map of the engine.",
            "[Warning: 12:00:00] MaterialMap: Switching global material map. TODO make instance",
            0, 0, new EngineLinePattern("MaterialMap", Pattern(@"^Switching global material map\b")))
            with { Disposition = EngineLineDisposition.Drop },
        Kind("O02", EngineLineClass.PerformanceNote,
            "The game server took more time for an update than the game allows.",
            "[Warning: 12:00:00] Server: Running slow: update took 34.57ms",
            0, 0, new EngineLinePattern("Server", SlowPattern))
            with { Disposition = EngineLineDisposition.Notice },
    ];

    private static readonly Dictionary<string, EngineLineKind> ById =
        Kinds.ToDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase);

    // The patterns that apply first, then the fallbacks, each in the order of the table.
    private static readonly (EngineLinePattern Pattern, EngineLineKind Kind)[] Order =
    [
        .. Kinds.SelectMany(k => k.Patterns.Where(p => !p.Fallback).Select(p => (p, k))),
        .. Kinds.SelectMany(k => k.Patterns.Where(p => p.Fallback).Select(p => (p, k))),
    ];

    /// <summary>The kind of a line, or null when no kind matches.</summary>
    public static EngineLineKind? Match(EngineLine line)
    {
        foreach (var (pattern, kind) in Order)
        {
            if (pattern.Matches(line))
                return kind;
        }
        return null;
    }

    /// <summary>The kind with an id, or null.</summary>
    public static EngineLineKind? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>
    /// The item that a line of a component without a file names: the ship of "Ship: Unrecognised hp_type X in s" or the
    /// pilot of "Pilot: p: ...". Null for another line.
    /// </summary>
    public static (EngineLineSubject Kind, string Nickname)? Subject(EngineLine line)
    {
        if (line.File != null)
            return null;
        if (string.Equals(line.Component, "Ship", StringComparison.OrdinalIgnoreCase) &&
            ShipSubject.Match(line.Core) is { Success: true } ship)
            return (EngineLineSubject.Ship, ship.Groups["nickname"].Value);
        if (string.Equals(line.Component, "Pilot", StringComparison.OrdinalIgnoreCase) &&
            PilotSubject.Match(line.Core) is { Success: true } pilot)
            return (EngineLineSubject.Pilot, pilot.Groups["nickname"].Value);
        return null;
    }

    /// <summary>The time of the update of a line of the slow game server (O02), in milliseconds, as the engine wrote it,
    /// or null for another line.</summary>
    public static string? SlowUpdate(EngineLine line) =>
        string.Equals(line.Component, "Server", StringComparison.OrdinalIgnoreCase) &&
        SlowPattern.Match(line.Core) is { Success: true } slow
            ? slow.Groups["ms"].Value
            : null;

    /// <summary>The pin of a kind for a data set, or 0.</summary>
    public static int Pin(EngineLineKind kind, EngineLineDataSet set) => kind.Pins.GetValueOrDefault(set);

    private static EngineLineKind Kind(string id, EngineLineClass value, string text, string sample, int vanilla, int hd,
        params EngineLinePattern[] patterns)
    {
        var pins = vanilla == 0 && hd == 0
            ? NoPins
            : new Dictionary<EngineLineDataSet, int>
            {
                [EngineLineDataSet.Vanilla] = vanilla,
                [EngineLineDataSet.HdEdition071] = hd,
            };
        return new EngineLineKind(id, value, text, patterns, sample, pins);
    }

    /// <summary>A pattern of an INI line: the message without its place, and the section type or any section.</summary>
    private static EngineLinePattern Ini(string core, string? section = null) =>
        new("Ini", Pattern($"^{core}$"), section == null ? null : Pattern($"^{section}$"));

    private static EngineLinePattern Fallback(string component, string core) =>
        new(component, Pattern($"^{core}$"), null, Fallback: true);

    private static Regex Pattern(string pattern) => new(pattern, Options | RegexOptions.Compiled);
}
