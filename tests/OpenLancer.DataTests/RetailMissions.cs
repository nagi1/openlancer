using System;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Runtime.Missions;

namespace OpenLancer.DataTests;

/// <summary>The run of the mission checks over the 14 retail story scripts, made once for the retail tests of the mission
/// studio. The workspace closes after the run, because the result holds all that the tests read.</summary>
internal static class RetailMissions
{
    private static readonly Lazy<MissionRunResult> Vanilla = new(() =>
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6aproof");
        return MissionRun.Execute(workspace.Open().MissionInputs(EngineCoverage.Instance));
    });

    /// <summary>The run, or a skip without the retail data.</summary>
    public static MissionRunResult Run()
    {
        GameDataPaths.RequireBase();
        return Vanilla.Value;
    }

    /// <summary>The analysis of the script of a story slot, for example M10.</summary>
    public static MissionAnalysis Slot(string slot)
    {
        var run = Run();
        return run.Of(run.Scripts.Single(s => s.Slot?.Slot == slot).Path)!;
    }
}
