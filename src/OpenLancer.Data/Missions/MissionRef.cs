using System;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions;

/// <summary>The kind of an item of a mission script (section 4.3 of the M6a design).</summary>
public enum MissionItemKind
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
/// An item of a mission script (section 4.3 of the M6a design), for example the selection of the Mission tab. Two values
/// are equal when all fields are equal. A new index keeps the values, so a selection stays after a data load. The type
/// is in the data layer, so the queries of the cross-links and later an agent server (roadmap row MCP1) can name items.
/// </summary>
/// <param name="Script">The install path of the script, in normal form.</param>
/// <param name="Section">The section of a section item. It keeps the ordinal of a repeated nickname.</param>
/// <param name="Key">The name of an item without a section: a label, an act, a prefab instance or a prefab kind.</param>
/// <param name="Row">The index of a condition or action line in its section, or -1.</param>
public sealed record MissionRef(string Script, MissionItemKind Kind, SectionRef? Section = null, string? Key = null,
    int Row = -1)
{
    /// <summary>The mission itself.</summary>
    public static MissionRef Of(string script) => new(VfsPath.Normalize(script) ?? script, MissionItemKind.Mission);

    /// <summary>The item of a section of a mission, or of a line of it. A line of a trigger is a row. A line of another
    /// section is its section, and the <c>[Mission]</c> section is the mission.</summary>
    /// <param name="entry">The index of the line in the section, or -1 for the whole section.</param>
    public static MissionRef OfSection(SectionRef target, int entry)
    {
        var path = VfsPath.Normalize(target.Path) ?? target.Path;
        var kind = KindOf(MissionSection.KindOf(target.Id.Type), line: entry >= 0);
        return kind == MissionItemKind.Mission
            ? Of(path)
            : new MissionRef(path, kind, target, Row: kind == MissionItemKind.Row ? entry : -1);
    }

    /// <summary>The item of a placeable item of the Scene: the line of a trigger, or the section of an item.</summary>
    public static MissionRef OfPlacement(string script, MissionPlacement placement) => placement.Trigger != null
        ? new MissionRef(script, MissionItemKind.Row, placement.Section.Ref, Row: placement.Entry)
        : OfSection(placement.Section.Ref, -1);

    /// <summary>The item kind of a section kind: a section of an unknown type is another section.</summary>
    /// <param name="line">True for a line of the section. A line of a trigger is a row.</param>
    public static MissionItemKind KindOf(MissionSectionKind kind, bool line = false) => kind switch
    {
        MissionSectionKind.Mission => MissionItemKind.Mission,
        MissionSectionKind.Trigger => line ? MissionItemKind.Row : MissionItemKind.Trigger,
        MissionSectionKind.Ship => MissionItemKind.Ship,
        MissionSectionKind.Formation => MissionItemKind.Formation,
        MissionSectionKind.Solar => MissionItemKind.Solar,
        MissionSectionKind.Loot => MissionItemKind.Loot,
        MissionSectionKind.OrderList => MissionItemKind.OrderList,
        MissionSectionKind.Objective => MissionItemKind.Objective,
        MissionSectionKind.Npc => MissionItemKind.Npc,
        MissionSectionKind.Conversation => MissionItemKind.Conversation,
        MissionSectionKind.RandomEncounter => MissionItemKind.RandomEncounter,
        _ => MissionItemKind.OtherSection,
    };

    public bool Equals(MissionRef? other) =>
        other is not null && VfsPath.Comparer.Equals(Script, other.Script) && Kind == other.Kind &&
        Equals(Section, other.Section) && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) &&
        Row == other.Row;

    public override int GetHashCode() => HashCode.Combine(VfsPath.Comparer.GetHashCode(Script), Kind, Section,
        Key == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Key), Row);
}
