using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Editor.Missions;

/// <summary>The references of the mission commands for the items of an analysis (section 4.2 of the M6b design). The
/// graph, the pages and the Hierarchy make their commands with them.</summary>
internal static class MissionCommandRefs
{
    /// <summary>The reference of a section of the script.</summary>
    public static ItemRef ItemOf(MissionSection section) => new(section.Ref.Path, section.Ref.Id);

    /// <summary>The reference of a line, with the guard of its key.</summary>
    public static RowRef RowOf(MissionLine line) => new(ItemOf(line.Section), line.Entry) { ExpectKey = line.Key };

    /// <summary>The reference of a trigger by its nickname.</summary>
    public static TriggerRef TriggerOf(MissionTrigger trigger) => new(trigger.Section.Ref.Path, trigger.Nickname);
}
