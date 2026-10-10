using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Validation;

namespace OpenLancer.Runtime.Validation;

/// <summary>
/// The full validation of a mod stack: the problems that the mods add to vanilla.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The INI reference checks (<see cref="ModValidator"/>). When they find errors, the result stops here,
/// because some of those errors make the engine load fail. The result then also holds the engine's INI parse
/// lines, because a parse error can be the cause of a reference error.</item>
/// <item>The engine load (<see cref="EngineDataLoader"/>): the engine's warning and error lines, a load failure,
/// and dropped ship packages.</item>
/// <item>The package checks (<see cref="PackageCheck"/>) on the loaded data.</item>
/// </list>
/// </remarks>
public static class ProfileValidator
{
    public static IReadOnlyList<Diagnostic> NewProblems(FileSystem vanilla, FileSystem modded)
    {
        var vanillaReport = ModValidator.Validate(vanilla);
        var moddedReport = ModValidator.Validate(modded);
        var checks = Diagnostic.NewIn(vanillaReport.Checks, moddedReport.Checks);
        if (checks.Any(p => p.Severity == DiagnosticSeverity.Error))
            return Diagnostic.NewIn(vanillaReport.ParseLines, moddedReport.ParseLines).Concat(checks).ToList();
        // The engine load parses the INI files again and reports the same parse lines.
        return checks.Concat(Diagnostic.NewIn(EngineProblems(vanilla), EngineProblems(modded))).ToList();
    }

    /// <summary>The problems that the engine load and the package checks find in one data set.</summary>
    public static IReadOnlyList<Diagnostic> EngineProblems(FileSystem fs)
    {
        var load = EngineDataLoader.Load(fs);
        if (load.Db == null)
            return load.Problems;
        return load.Problems.Concat(PackageCheck.Check(load.Db, load.PackageNicknames, fs)).ToList();
    }
}
