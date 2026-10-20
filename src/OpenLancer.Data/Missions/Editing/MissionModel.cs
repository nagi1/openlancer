using System.Collections.Generic;
using OpenLancer.Data.Editing;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The current model of one mission script (section 4.4 of the M6b design): W, the index, the problems with their
/// origins, the prefab instances, the acts, the graph file and the version. The model never changes. A new state of W
/// gives a new model.
/// </summary>
/// <param name="Script">The install path of the script, in normal form.</param>
/// <param name="Version">The script version (section 4.2 of the M6b design).</param>
/// <param name="Analysis">The analysis of W of the script.</param>
public sealed record MissionModel(string Script, long Version, MissionAnalysis Analysis)
{
    /// <summary>The document of W.</summary>
    public MissionDocument Document => Analysis.Document;

    /// <summary>The index of W.</summary>
    public MissionIndex Index => Analysis.Index;

    /// <summary>The problems of W with their origins and severities.</summary>
    public IReadOnlyList<Diagnostic> Problems => Analysis.Problems;

    /// <summary>The behavior prefab instances that the matcher finds.</summary>
    public MissionPrefabResult? Prefabs => Analysis.Prefabs;

    /// <summary>The proposed acts of the script.</summary>
    public MissionActs? Acts => Analysis.Acts;

    /// <summary>The text of the graph file of the script in the active mod (section 6.8 of the M6b design), or null
    /// when the mod has none.</summary>
    public string? GraphFile { get; init; }

    /// <summary>Why the editor cannot change the script, or null.</summary>
    public string? ReadOnlyReason { get; init; }
}

/// <summary>
/// Gives the mission editor the names of the game data that the checks need: the world, the base view, the ledger of
/// the saved files, the coverage and the check options (section 4.4 of the M6b design). The editor gives the world of
/// the last snapshot. A test gives a fixed world.
/// </summary>
public interface IMissionWorldSource
{
    /// <summary>The inputs of an analysis now, without an overlay. The source gives the same instance until the names
    /// change, so the model cache can keep its models.</summary>
    MissionInputs Inputs();
}

/// <summary>A world source that always gives the same inputs, for example in a test.</summary>
public sealed class FixedMissionWorld(MissionInputs inputs) : IMissionWorldSource
{
    public MissionInputs Inputs() => inputs;
}

/// <summary>
/// The world source of an edit session: the snapshot of the session (see <see cref="EditSession.MissionInputs"/>). A
/// new snapshot comes when the edit view changes, for example after a save, and when W of a file outside the mission
/// scripts changes, for example a new ship build or a renamed object (section 7 of the M6b design).
/// </summary>
internal sealed class SessionMissionWorld(EditSession session) : IMissionWorldSource
{
    private (object View, long World, IEngineCoverage Coverage, MissionInputs Inputs)? last;

    /// <summary>The OpenLancer marks of the checks. The editor gives the marks of the engine.</summary>
    public IEngineCoverage Coverage { get; set; } = FullCoverage.Instance;

    public MissionInputs Inputs()
    {
        if (last is var (view, world, coverage, inputs) && ReferenceEquals(view, session.EditView) &&
            world == session.WorldRevision && ReferenceEquals(coverage, Coverage))
            return inputs;
        var made = session.MissionWorldInputs(Coverage);
        last = (session.EditView, session.WorldRevision, Coverage, made);
        return made;
    }
}
