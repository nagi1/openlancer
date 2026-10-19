namespace OpenLancer.Data;

/// <summary>Who causes a message of the Console (decision D500 of the console filter design).</summary>
public enum OriginKind
{
    /// <summary>The base game without mods gives the same message. The Console hides it by default.</summary>
    BaseGame,

    /// <summary>A known mod causes the message.</summary>
    Mod,

    /// <summary>A mod causes the message, but OpenLancer cannot find which mod.</summary>
    SomeMod,

    /// <summary>The message is about the work of OpenLancer, not about the game data.</summary>
    OpenLancer,

    /// <summary>OpenLancer did not compare the message with the base game.</summary>
    Unknown,

    /// <summary>The message waits for the comparison with the base game. The Console does not show it yet
    /// (decision D517).</summary>
    Pending,
}

/// <summary>How OpenLancer found the mod of a message (sections 3.5 and 3.6 of the console filter design).</summary>
public enum ModEvidence
{
    /// <summary>The mod changes the section of the message.</summary>
    Section,

    /// <summary>The mod changes the file of the message.</summary>
    File,

    /// <summary>The mod is the only mod of the profile that changes game files.</summary>
    OnlyMod,

    /// <summary>The mod is the active mod, and a check of its data finds the problem: the save gate or the validation
    /// of a save.</summary>
    ActiveMod,

    /// <summary>A lower mod changes the key of the problem, and the save gate finds the problem in that key (decision
    /// D518).</summary>
    Key,
}

/// <summary>The origin of a Console message: who causes it.</summary>
/// <param name="ModId">The mod of <see cref="OriginKind.Mod"/>, else null.</param>
/// <param name="Evidence">How OpenLancer found the mod of <see cref="OriginKind.Mod"/>.</param>
public sealed record MessageOrigin(OriginKind Kind, string? ModId = null, ModEvidence Evidence = ModEvidence.Section)
{
    public static readonly MessageOrigin BaseGame = new(OriginKind.BaseGame);
    public static readonly MessageOrigin SomeMod = new(OriginKind.SomeMod);
    public static readonly MessageOrigin OpenLancer = new(OriginKind.OpenLancer);
    public static readonly MessageOrigin Unknown = new(OriginKind.Unknown);
    public static readonly MessageOrigin Pending = new(OriginKind.Pending);

    public static MessageOrigin Mod(string id, ModEvidence evidence) => new(OriginKind.Mod, id, evidence);

    /// <summary>True when the base game without mods gives the same message.</summary>
    public bool IsBaseGame => Kind == OriginKind.BaseGame;

    /// <summary>The origin line of the details pane of the Console.</summary>
    /// <remarks>Only the comparison with the base run (<see cref="ModEvidence.Section"/>, <see cref="ModEvidence.File"/>
    /// and <see cref="ModEvidence.OnlyMod"/>) shows that the base game does not give the message. A check of the data
    /// of a mod does not compare with the base game. For example, a mod can write a key again with the error that the
    /// base game has too.</remarks>
    public string Text => Kind switch
    {
        OriginKind.BaseGame => "Origin: base game. The base game without mods gives the same message.",
        OriginKind.Mod => $"Origin: mod '{ModId}'. " + Evidence switch
        {
            ModEvidence.Section => $"{NotInBase} Mod '{ModId}' changes this section.",
            ModEvidence.File => $"{NotInBase} Mod '{ModId}' changes this file.",
            ModEvidence.OnlyMod => $"{NotInBase} Mod '{ModId}' is the only mod that changes game files.",
            ModEvidence.Key => $"Mod '{ModId}' changes this key, and a check of its data finds the problem.",
            _ => $"Mod '{ModId}' is the active mod, and a check of its data finds the problem.",
        },
        OriginKind.SomeMod => $"Origin: a mod. {NotInBase} OpenLancer cannot find the mod that causes it.",
        OriginKind.OpenLancer => "Origin: OpenLancer. This message is about the work of OpenLancer, not about the game data.",
        OriginKind.Unknown => "Origin: unknown. OpenLancer did not compare this message with the base game.",
        _ => "Origin: not known yet. OpenLancer compares this message with the base game.",
    };

    private const string NotInBase = "The base game without mods does not give this message.";
}
