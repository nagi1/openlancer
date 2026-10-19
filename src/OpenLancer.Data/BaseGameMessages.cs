using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenLancer.Data;

/// <summary>
/// The rules of the base-game messages toggle (decision D519 of the console filter design, decision D527 of the M6a
/// design). A problem of the base game shows only while the toggle is on. A problem of a mod always shows. The Console,
/// the mission views and the completeness panels use these rules, so they count and say the same thing.
/// </summary>
public static class BaseGameMessages
{
    /// <summary>True when a problem shows: each problem of a mod, and the base-game problems while they show.</summary>
    /// <param name="showBaseGame">True while the base-game messages show.</param>
    public static bool Shows(Diagnostic problem, bool showBaseGame) => showBaseGame || problem.Origin?.IsBaseGame != true;

    /// <summary>The problems that show, in their order.</summary>
    public static IReadOnlyList<Diagnostic> Shown(IEnumerable<Diagnostic> problems, bool showBaseGame) =>
        problems.Where(p => Shows(p, showBaseGame)).ToList();

    /// <summary>The number of base-game problems, shown or hidden.</summary>
    public static int Count(IEnumerable<Diagnostic> problems) => problems.Count(p => p.Origin?.IsBaseGame == true);

    /// <summary>The number of hidden base-game problems: 0 while they show.</summary>
    public static int Hidden(IEnumerable<Diagnostic> problems, bool showBaseGame) => showBaseGame ? 0 : Count(problems);

    /// <summary>The dim line of the hidden base-game messages, or null for none.</summary>
    public static string? HiddenLine(int hidden) => hidden switch
    {
        <= 0 => null,
        1 => "1 base-game message is hidden.",
        _ => string.Create(CultureInfo.InvariantCulture, $"{hidden:N0} base-game messages are hidden."),
    };
}
