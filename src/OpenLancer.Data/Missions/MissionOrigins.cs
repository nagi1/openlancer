using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;

namespace OpenLancer.Data.Missions;

/// <summary>Which mod changes which mission script and section (section 12.2 of the M6a design).</summary>
public interface IMissionLedger
{
    /// <summary>The mods that change a file in any way, in load order.</summary>
    IReadOnlyList<string> ModsThatChangeFile(string path);

    /// <summary>The mods that change a section in any way, in load order.</summary>
    IReadOnlyList<string> ModsThatChangeSection(string path, SectionIdentity section);

    /// <summary>The mod whose whole file gives the content of a file, or null.</summary>
    string? ReplacedBy(string path);
}

/// <summary>A ledger without mods: each script is base game.</summary>
public sealed class NoMods : IMissionLedger
{
    public static readonly NoMods Instance = new();

    public IReadOnlyList<string> ModsThatChangeFile(string path) => [];

    public IReadOnlyList<string> ModsThatChangeSection(string path, SectionIdentity section) => [];

    public string? ReplacedBy(string path) => null;
}

/// <summary>
/// The origin and the severity of the mission problems (section 12.2 of the M6a design, decision D551). A mission
/// problem is a base-game problem only when the same check gives the same problem on the base game:
/// <list type="number">
/// <item>A script that no mod changes gives only base-game problems. One exception: a mod removes or renames a name of
/// the base game that the script names. Then the base-game run checks the script with the names of the base game, as in
/// rule 2.</item>
/// <item>For a script that a mod changes, a problem whose subject the base-game run also has is a base-game problem. Each
/// other problem has the origin of the last mod that changes its section. Else it has the mod that removes or renames a
/// name of its line, else the last mod that changes the whole file.</item>
/// <item>A script that only a mod has gives only problems of that mod.</item>
/// </list>
/// So a problem that a mod causes always shows, also when the mod changes another file (section 0 item 2 of the M6a
/// design). A problem of the active mod keeps the severity of its rule. A problem of a lower mod or of the base game is
/// a warning (decision D410). So the retail scripts as base-game data give no error.
/// </summary>
public static class MissionOrigins
{
    /// <summary>Gives each problem of a script its origin and its severity.</summary>
    /// <param name="current">The problems of the edit view version of the script.</param>
    /// <param name="baseRun">The problems of the base-game run of the script, or null when no base-game run
    /// exists.</param>
    /// <param name="activeModId">The active mod, or null for a profile without an active mod.</param>
    /// <param name="removedBy">Gives the mod that removes or renames a name of the line of a problem, or null.</param>
    public static IReadOnlyList<Diagnostic> Apply(IReadOnlyList<Diagnostic> current, IReadOnlyList<Diagnostic>? baseRun,
        MissionScriptInfo script, IMissionLedger ledger, string? activeModId, Func<Diagnostic, string?>? removedBy = null)
    {
        if (script.State == MissionScriptState.BaseGame && baseRun == null)
            return current.Select(BaseGame).ToList();
        var known = script.InBase && baseRun != null
            ? baseRun.CountBy(d => d.Subject ?? d.Message).ToDictionary(c => c.Key, c => c.Value)
            : new Dictionary<string, int>();
        var result = new List<Diagnostic>(current.Count);
        foreach (var problem in current)
        {
            var subject = problem.Subject ?? problem.Message;
            if (known.TryGetValue(subject, out var count) && count > 0)
            {
                known[subject] = count - 1;
                result.Add(BaseGame(problem));
                continue;
            }
            var (mod, evidence) = ModOf(problem, script, ledger, removedBy);
            var origin = mod == null ? MessageOrigin.SomeMod : MessageOrigin.Mod(mod, evidence);
            var severity = mod != null && mod == activeModId && problem.Rule != null
                ? MissionChecks.SeverityOf(problem.Rule)
                : DiagnosticSeverity.Warning;
            result.Add(problem with { Severity = severity, Origin = origin });
        }
        return result;
    }

    private static Diagnostic BaseGame(Diagnostic problem) =>
        problem with { Severity = DiagnosticSeverity.Warning, Origin = MessageOrigin.BaseGame };

    /// <summary>The mod of a problem with the evidence. It is the last mod that changes the section of the problem, else
    /// the mod that removes or renames a name of its line. Else it is the last mod that changes the whole file.</summary>
    private static (string? Mod, ModEvidence Evidence) ModOf(Diagnostic problem, MissionScriptInfo script,
        IMissionLedger ledger, Func<Diagnostic, string?>? removedBy)
    {
        if (problem.Target is { } target && ledger.ModsThatChangeSection(target.Path, target.Id) is [.., var last])
            return (last, ModEvidence.Section);
        if (removedBy?.Invoke(problem) is { } remover)
            return (remover, ModEvidence.Name);
        if (ledger.ModsThatChangeFile(script.Path) is [.., var file])
            return (file, ModEvidence.File);
        return (script.ModId, ModEvidence.File);
    }
}
