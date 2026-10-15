using System;
using System.Collections.Generic;

namespace OpenLancer.Editor;

/// <summary>
/// The command line of the editor: <c>OpenLancer.Editor [profile.json] [--bench-cutscene &lt;install path&gt;...]</c>.
/// </summary>
/// <param name="Profile">The profile file to open at the start, or null for the newest recent profile.</param>
/// <param name="BenchCutscenes">The cutscene scripts of the preview bench, or an empty list for a normal start. With
/// scripts, the editor opens the profile without an active mod, previews each script, writes the times to the log, and
/// closes (section 8.5 of the M8 design). It does not read or write the editor settings.</param>
internal sealed record EditorOptions(string? Profile, IReadOnlyList<string> BenchCutscenes)
{
    public const string BenchOption = "--bench-cutscene";

    /// <summary>True when the editor runs the preview bench.</summary>
    public bool Bench => BenchCutscenes.Count > 0;

    /// <summary>
    /// Why the command line of a normal start is not valid, or null. The editor then opens no profile, and the Welcome
    /// screen shows this text. The editor has no console window, so an error on the console would not show.
    /// </summary>
    public string? Problem { get; private init; }

    /// <summary>
    /// Reads the command line. A wrong command line of a normal start gives options with a <see cref="Problem"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The command line has <see cref="BenchOption"/>, and it has an unknown
    /// option, a second profile, or no script. The script that starts the bench reads the message and the exit
    /// code.</exception>
    public static EditorOptions Parse(IReadOnlyList<string> args)
    {
        string? profile = null;
        var bench = new List<string>();
        var benchOption = false;
        string? problem = null;
        foreach (var arg in args)
        {
            if (arg == BenchOption)
            {
                benchOption = true;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                problem ??= $"Unknown option {arg}. The options are [profile.json] and {BenchOption} <install path>...";
            }
            else if (benchOption)
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

        if (benchOption && bench.Count == 0)
        {
            problem ??= $"{BenchOption} needs at least one install path of a cutscene script.";
        }

        if (problem == null)
        {
            return new EditorOptions(profile, bench);
        }

        if (benchOption)
        {
            throw new ArgumentException(problem);
        }

        return new EditorOptions(null, []) { Problem = $"{problem} The editor opened no profile." };
    }
}
