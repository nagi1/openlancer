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
/// <item>The cutscene checks (<see cref="CutsceneCheck"/>) of the listed cutscenes, with the loaded data. They also run
/// when the INI checks find errors, but then without the checks of templates and costumes.</item>
/// </list>
/// </remarks>
public static class ProfileValidator
{
    /// <param name="cutscenePaths">The install paths of the cutscenes that the mods add or change
    /// (<see cref="OpenLancer.Data.Overlay.ModOverlay.CutscenePaths"/>).</param>
    public static IReadOnlyList<Diagnostic> NewProblems(FileSystem vanilla, FileSystem modded,
        IReadOnlyCollection<string> cutscenePaths)
    {
        var vanillaReport = ModValidator.Validate(vanilla);
        var moddedReport = ModValidator.Validate(modded);
        var checks = Diagnostic.NewIn(vanillaReport.Checks, moddedReport.Checks);
        if (checks.Any(p => p.Severity == DiagnosticSeverity.Error))
        {
            return Diagnostic.NewIn(vanillaReport.ParseLines, moddedReport.ParseLines).Concat(checks)
                .Concat(CutsceneCheck.NewProblems(vanilla, modded, cutscenePaths, null, null)).ToList();
        }
        // The engine load parses the INI files again and reports the same parse lines.
        var vanillaLoad = EngineDataLoader.Load(vanilla);
        var moddedLoad = EngineDataLoader.Load(modded);
        return checks.Concat(Diagnostic.NewIn(EngineProblems(vanillaLoad, vanilla), EngineProblems(moddedLoad, modded)))
            .Concat(CutsceneCheck.NewProblems(vanilla, modded, cutscenePaths, vanillaLoad.Db, moddedLoad.Db)).ToList();
    }

    /// <summary>The problems that the engine load and the package checks find in one data set.</summary>
    private static IReadOnlyList<Diagnostic> EngineProblems(EngineLoadResult load, FileSystem fs)
    {
        if (load.Db == null)
            return load.Problems;
        return load.Problems.Concat(PackageCheck.Check(load.Db, load.PackageNicknames, fs)).ToList();
    }
}
