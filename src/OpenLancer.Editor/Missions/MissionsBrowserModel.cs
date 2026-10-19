using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The model of the Missions tab (section 5 of the M6a design): the column texts, and the rows, the search matches and
/// the bundles of the last run of the checks. The queries of <see cref="MissionSummary"/> make them. The model keeps each
/// result until the run, the toggle or the search changes. So the tab does not make them again in each frame. The model makes no ImGui call, so the tests check it (review finding RV12).
/// </summary>
internal sealed class MissionsBrowserModel
{
    /// <summary>The tooltips of the columns (section 13.1 of the M6a design).</summary>
    public static readonly IReadOnlyList<(string Name, string Tooltip)> Columns =
    [
        ("Slot", "The story slot. The game code has 14 slots in a fixed order."),
        ("Mission script", "The file that the game loads for this slot."),
        ("Start stage", "The story stage when the mission starts. News and rumors use the stage."),
        ("Accept stage", "The story stage after the player accepts the mission. A success adds 1 to it."),
        ("Next mission", "The time when the next mission starts after a success. The game code sets it. A mod cannot change it."),
        ("State", "Base game: no mod changes this script. Changed: a mod patch changes it. Replaced: a mod has its own file " +
                  "at this path. Missing: no file is at this path."),
        ("Triggers", "The number of triggers in the script."),
        // A base-game problem is a warning (decision D410), so only the Warnings column has a hidden count.
        ("Errors", "Problems that make the game stop, drop an item or ignore a line."),
        ("Warnings", "Problems that can give a result that you do not expect. The number in brackets counts the hidden " +
                     "base-game messages."),
        ("OpenLancer", "What the OpenLancer engine runs of this script. The retail game runs all lines."),
    ];

    /// <summary>The index of the OpenLancer column.</summary>
    public const int OpenLancerColumn = 9;

    private (MissionRunResult? Result, bool ShowBaseGame, IReadOnlyList<MissionRow> Rows) rows = (null, false, []);
    private (MissionRunResult? Result, string Search, Dictionary<string, IReadOnlyList<MissionTrigger>> Matches) matches =
        (null, "", []);
    private (MissionRunResult? Result, IReadOnlyList<string> Systems) systems = (null, []);
    private (MissionRunResult? Result, Dictionary<string, IReadOnlyList<MissionBundleRow>> Rows) bundles = (null, []);

    /// <summary>The rows of the scripts of a run, in the order of <see cref="MissionSummary.Rows"/>.</summary>
    /// <param name="showBaseGame">True while the base-game messages show.</param>
    public IReadOnlyList<MissionRow> Rows(MissionRunResult result, bool showBaseGame)
    {
        if (!ReferenceEquals(rows.Result, result) || rows.ShowBaseGame != showBaseGame)
            rows = (result, showBaseGame, MissionSummary.Rows(result, showBaseGame));
        return rows.Rows;
    }

    /// <summary>The triggers of a script that match the search, in file order (see
    /// <see cref="MissionSummary.MatchingTriggers"/>).</summary>
    public IReadOnlyList<MissionTrigger> MatchingTriggers(MissionRunResult result, MissionAnalysis analysis, string search)
    {
        if (!ReferenceEquals(matches.Result, result) || matches.Search != search)
            matches = (result, search, new Dictionary<string, IReadOnlyList<MissionTrigger>>(StringComparer.OrdinalIgnoreCase));
        if (!matches.Matches.TryGetValue(analysis.Script.Path, out var found))
            matches.Matches[analysis.Script.Path] = found = MissionSummary.MatchingTriggers(analysis, search);
        return found;
    }

    /// <summary>True when a row matches the search (see <see cref="MissionSummary.Matches"/>).</summary>
    public bool Matches(MissionRunResult result, MissionRow row, string search) =>
        MissionSummary.Matches(row, result.Of(row.Script.Path) is { } analysis
            ? MatchingTriggers(result, analysis, search)
            : [], search);

    /// <summary>The systems that the missions of a run name, in name order, for the filter.</summary>
    public IReadOnlyList<string> Systems(MissionRunResult result)
    {
        if (!ReferenceEquals(systems.Result, result))
            systems = (result, result.Analyses.Values.SelectMany(a => MissionSummary.Systems(a.Index))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList());
        return systems.Systems;
    }

    /// <summary>The bundle of a mission (see <see cref="MissionSummary.Bundle"/>).</summary>
    public IReadOnlyList<MissionBundleRow> Bundle(MissionRunResult result, MissionAnalysis analysis)
    {
        if (!ReferenceEquals(bundles.Result, result))
            bundles = (result, new Dictionary<string, IReadOnlyList<MissionBundleRow>>(StringComparer.OrdinalIgnoreCase));
        if (!bundles.Rows.TryGetValue(analysis.Script.Path, out var found))
            bundles.Rows[analysis.Script.Path] = found = MissionSummary.Bundle(result, analysis);
        return found;
    }

    /// <summary>Forgets the results, for example when the profile closes.</summary>
    public void Reset()
    {
        rows = (null, false, []);
        matches = (null, "", []);
        systems = (null, []);
        bundles = (null, []);
    }
}
