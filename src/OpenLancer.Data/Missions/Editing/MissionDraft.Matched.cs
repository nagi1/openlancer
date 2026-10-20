using System.Collections.Generic;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The matched prefab instances in the draft (section 5.8 of the M6b design).</summary>
internal sealed partial class MissionDraft
{
    /// <summary>Writes a field of a matched instance.</summary>
    public void WriteMatchedField(InstanceRef instance, string field, IReadOnlyList<string> rows) =>
        throw Refuse(MissionErrors.FieldNotWritableError(field));

    /// <summary>Makes a prefab asset from a matched instance.</summary>
    public void SaveAsPrefab(InstanceRef instance, string id, string? name) =>
        throw Refuse(MissionErrors.InstanceNotFoundError(instance.Script, instance.Id));
}
