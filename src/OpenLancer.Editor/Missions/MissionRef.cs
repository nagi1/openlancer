using System;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;

namespace OpenLancer.Editor.Missions;

/// <summary>The kind of an item of a mission script (section 4.3 of the M6a design).</summary>
internal enum MissionItemKind
{
    Mission,
    Trigger,

    /// <summary>A condition or an action line of a trigger.</summary>
    Row,
    Ship,
    Formation,
    Solar,
    Loot,
    OrderList,
    Objective,
    Conversation,
    Npc,
    Label,
    RandomEncounter,
    Act,
    PrefabInstance,
    PrefabKind,
    OtherSection,
}

/// <summary>
/// A selection of the Mission tab (section 4.3 of the M6a design): an item of a mission script. Two values are equal when
/// all fields are equal, and a new index keeps the values, so a selection stays after a data load.
/// </summary>
/// <param name="Script">The install path of the script, in normal form.</param>
/// <param name="Section">The section of a section item. It keeps the ordinal of a repeated nickname.</param>
/// <param name="Key">The name of an item without a section: a label, an act, a prefab instance or a prefab kind.</param>
/// <param name="Row">The index of a condition or action line in its section, or -1.</param>
internal sealed record MissionRef(string Script, MissionItemKind Kind, SectionRef? Section = null, string? Key = null,
    int Row = -1)
{
    /// <summary>The mission itself.</summary>
    public static MissionRef Of(string script) => new(VfsPath.Normalize(script) ?? script, MissionItemKind.Mission);

    public bool Equals(MissionRef? other) =>
        other is not null && VfsPath.Comparer.Equals(Script, other.Script) && Kind == other.Kind &&
        Equals(Section, other.Section) && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) &&
        Row == other.Row;

    public override int GetHashCode() => HashCode.Combine(VfsPath.Comparer.GetHashCode(Script), Kind, Section,
        Key == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Key), Row);
}
