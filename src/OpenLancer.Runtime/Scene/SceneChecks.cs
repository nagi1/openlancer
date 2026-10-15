using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Schema;
using OpenLancer.Data;
using OpenLancer.Data.Editing;

namespace OpenLancer.Runtime.Scene;

/// <summary>The checks of the scene sections that run before a save writes files.</summary>
public static class SceneChecks
{
    private const string CurveKey = "atten_curve";

    /// <summary>
    /// An error for each <c>[LightSource]</c> in W of an edited star system file whose <c>atten_curve</c> names a
    /// curve that the engine cannot load (decision D34). The engine throws when it loads such a light
    /// (GameItemDb.cs:1352). So a saved mod with such a light stops each later load of the profile, and then the editor
    /// cannot open the scene to correct it.
    /// </summary>
    /// <remarks>
    /// <para>The check reads only W of the files that the session edited (see <see cref="EditSession.EditedPaths"/>),
    /// so it opens no new file. A light of another file got its curve from a file that the game data loaded.</para>
    /// <para>The check reads the curve as the generated parser reads it: the first value of the last
    /// <c>atten_curve</c> line that has a value.</para>
    /// </remarks>
    /// <param name="graphs">The graphs of the loaded game data.</param>
    public static IReadOnlyList<Diagnostic> Curves(EditSession session, GraphIni graphs)
    {
        var problems = new List<Diagnostic>();
        foreach (var path in session.EditedPaths.Where(session.IsSystemFile))
        {
            foreach (var section in session.Sections(path))
            {
                if (SceneFields.SectionOf(section.Id.Type, universe: false) != SceneSection.LightSource ||
                    session.Get(section)?.Lines(CurveKey).LastOrDefault(l => l.Values.Count > 0) is not { } line)
                    continue;
                var curve = line.Values[0].ToString();
                if (SceneBuild.CurveProblem(graphs, curve, out _) is { } problem)
                {
                    problems.Add(new Diagnostic(DiagnosticSeverity.Error, path,
                        $"{section.Id}: {CurveKey} = {curve}. {problem}") { Target = section, Key = CurveKey });
                }
            }
        }
        return problems;
    }
}
