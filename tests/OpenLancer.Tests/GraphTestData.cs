using OpenLancer.Data.Missions;

namespace OpenLancer.Tests;

/// <summary>
/// A synthetic mission for the tests of the mission graph (section 18.1 of the M6a design). All names are invented. The
/// main path is t_start, t_wait, t_kill, t_save and t_end. The checkpoint and the change from sys_a to sys_b start the
/// second act. t_spawn starts a spawn wave whose end trigger is t_kill. t_lonely is not started by the mission, and
/// t_after starts a trigger that the script does not have.
/// </summary>
internal static class GraphTestData
{
    public const string Script = """
        [Mission]

        [MsnShip]
        nickname = enemy1
        system = sys_a
        position = 100, 0, 200

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        system = sys_a
        Cnd_True = no_params
        Act_ActTrig = t_spawn
        Act_ActTrig = t_wait

        [Trigger]
        nickname = t_spawn
        system = sys_a
        Cnd_True = no_params
        Act_SpawnShip = enemy1
        Act_ActTrig = t_after

        [Trigger]
        nickname = t_kill
        system = sys_a
        Cnd_Destroyed = enemy1
        Act_ActTrig = t_save

        [Trigger]
        nickname = t_wait
        system = sys_a
        Cnd_Timer = 5
        Act_ActTrig = t_kill

        [Trigger]
        nickname = t_after
        system = sys_a
        Cnd_Timer = 2
        Act_ActTrig = t_missing

        [Trigger]
        nickname = t_save
        system = sys_b
        Cnd_True = no_params
        Act_Save = t_restore
        Act_ActTrig = t_end

        [Trigger]
        nickname = t_restore
        system = sys_b
        Cnd_True = no_params
        Act_ActTrig = t_end

        [Trigger]
        nickname = t_end
        system = sys_b
        Cnd_WatchTrigger = t_save, ON
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_lonely
        Cnd_True = no_params
        Act_ActTrig = t_end
        """;

    /// <summary>The install path of the script: a story slot, so the run has a slot row.</summary>
    public const string Path = @"DATA\MISSIONS\M01A\m01a.ini";

    /// <summary>The analysis of a script text.</summary>
    public static MissionAnalysis Analysis(string text = Script)
    {
        using var folder = new MissionFolder();
        folder.Write(Path, text);
        return MissionRun.Execute(folder.Inputs()).Of(Path)!;
    }
}
