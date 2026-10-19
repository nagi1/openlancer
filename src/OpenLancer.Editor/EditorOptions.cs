using System;
using System.Collections.Generic;

namespace OpenLancer.Editor;

/// <summary>
/// The command line of the editor: <c>OpenLancer.Editor [profile.json] [--bench-cutscene &lt;install path&gt;...]
/// [--bench-mission &lt;install path&gt;...]</c>.
/// </summary>
/// <param name="Profile">The profile file to open at the start, or null for the newest recent profile.</param>
/// <param name="BenchCutscenes">The cutscene scripts of the preview bench, or an empty list for a normal start. With
/// scripts, the editor opens the profile without an active mod and previews each script. Then it writes the times to
/// the log and closes (section 8.5 of the M8 design). It does not read or write the editor settings.</param>
/// <param name="BenchMissions">The mission scripts of the frame bench of the mission graph, or an empty list (section
/// 18.4 of the M6a design). With scripts, the editor opens the profile without an active mod and draws the graph of
/// each script. Then it writes the frame times to the log and closes. It does not read or write the editor
/// settings.</param>
internal sealed record EditorOptions(string? Profile, IReadOnlyList<string> BenchCutscenes,
    IReadOnlyList<string> BenchMissions)
{
    public const string BenchOption = "--bench-cutscene";

    public const string MissionBenchOption = "--bench-mission";

    public EditorOptions(string? profile, IReadOnlyList<string> benchCutscenes) : this(profile, benchCutscenes, [])
    {
    }

    /// <summary>True when the editor runs the preview bench or the mission bench.</summary>
    public bool Bench => BenchCutscenes.Count > 0 || BenchMissions.Count > 0;

    /// <summary>
    /// Why the command line of a normal start is not valid, or null. The editor then opens no profile, and the Welcome
    /// screen shows this text. The editor has no console window, so an error on the console would not show.
    /// </summary>
    public string? Problem { get; private init; }

    /// <summary>
    /// Reads the command line. A wrong command line of a normal start gives options with a <see cref="Problem"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The command line has <see cref="BenchOption"/> or
    /// <see cref="MissionBenchOption"/>, and it has an unknown option, a second profile, both bench options, or no
    /// script. The script that starts the bench reads the message and the exit code.</exception>
    public static EditorOptions Parse(IReadOnlyList<string> args)
    {
        string? profile = null;
        var cutscenes = new List<string>();
        var missionScripts = new List<string>();
        List<string>? bench = null;
        string? problem = null;
        foreach (var arg in args)
        {
            if (arg == BenchOption || arg == MissionBenchOption)
            {
                var list = arg == BenchOption ? cutscenes : missionScripts;
                if (bench != null && bench != list)
                {
                    problem ??= $"The command line has {BenchOption} and {MissionBenchOption}. Run one bench at a time.";
                }

                bench = list;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                problem ??= $"Unknown option {arg}. The options are [profile.json], {BenchOption} <install path>... " +
                            $"and {MissionBenchOption} <install path>...";
            }
            else if (bench != null)
            {
                bench.Add(arg);
            }
            else if (profile == null)
            {
                profile = arg;
            }
            else
            {
                problem ??= $"The command line names two profiles: {profile} and {arg}.";
            }
        }

        if (bench != null && bench.Count == 0)
        {
            problem ??= bench == cutscenes
                ? $"{BenchOption} needs at least one install path of a cutscene script."
                : $"{MissionBenchOption} needs at least one install path of a mission script.";
        }

        if (problem == null)
        {
            return new EditorOptions(profile, cutscenes, missionScripts);
        }

        if (bench != null)
        {
            throw new ArgumentException(problem);
        }

        return new EditorOptions(null, []) { Problem = $"{problem} The editor opened no profile." };
    }
}
