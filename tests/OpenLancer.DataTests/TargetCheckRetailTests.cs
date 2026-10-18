using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The content checks of the retail targets on the retail data (section 9.5 of the M11 design). The checks read each INI
/// file of the game as a file that a mod adds. So each finding is a finding of the game data itself. The game data has
/// only the scripted cases that the class comment of <see cref="TargetChecks"/> names. These are a cutscene missile
/// without its loot entries, and capital ships of the story missions without an engine. The tests only read the game
/// folder.
/// </summary>
internal static class TargetCheckRetail
{
    private const string Munition = "Munition '";
    private const string NpcShip = "NPC ship '";

    /// <summary>Checks each INI file of a game as new data, and asserts the counts of the scripted cases.</summary>
    public static void AssertOnlyScriptedCases(string root, int npcShips, ITestOutputHelper output)
    {
        var findings = TargetChecks.Run(new WholeGameView(root));

        foreach (var finding in findings)
            output.WriteLine(finding.ToString());
        Assert.All(findings, f => Assert.True(f.Message.StartsWith(Munition) || f.Message.StartsWith(NpcShip), f.ToString()));
        // The cutscene missile and its launcher have no lootprops.ini entry.
        Assert.Equal(2, findings.Count(f => f.Message.StartsWith(Munition)));
        Assert.Equal(npcShips, findings.Count(f => f.Message.StartsWith(NpcShip)));
    }

    /// <summary>The sample mod, merged on the game, adds no finding.</summary>
    public static void AssertTheSampleAddsNoFinding(string root, ITestOutputHelper output)
    {
        var files = TargetBuild.Run(GameBase.Standard(root), [Mod.Load(GameDataPaths.SampleMod("ol-sample"))],
            ModOverlay.StringsDllName, DllStringResourceWriter.Instance, ThornEvaluator.Instance);

        var findings = TargetChecks.Run(files);

        foreach (var finding in findings)
            output.WriteLine(finding.ToString());
        Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(findings);
    }

    /// <summary>A view in which each INI file of the game is new: the data without the mods is empty.</summary>
    private sealed class WholeGameView : CheckView
    {
        private readonly FileSystem fs;
        private readonly Dictionary<string, IReadOnlyList<Section>?> read = new(VfsPath.Comparer);

        public WholeGameView(string root)
        {
            fs = GameBase.Standard(root).OpenFileSystem();
            ChangedPaths = Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
                .Select(f => VfsPath.Normalize(Path.GetRelativePath(root, f))!)
                .Where(p => p.StartsWith(@"DATA\", System.StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith(@"EXE\", System.StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public override IReadOnlyCollection<string> ChangedPaths { get; }

        public override IReadOnlyList<Section>? Sections(string path, bool withMods)
        {
            if (!withMods)
                return null;
            if (!read.TryGetValue(path, out var sections))
                read[path] = sections = Read(fs, path);
            return sections;
        }

        public override IReadOnlyList<string> Folders(string path) => fs.GetDirectories(path);
    }
}

/// <summary>The content checks on the retail 1.0 data. They skip without the game data.</summary>
public sealed class TargetCheckRetailTests(ITestOutputHelper output)
{
    static TargetCheckRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheRetailDataGivesNoTargetFinding() =>
        // Four capital ships of the story missions have no engine.
        TargetCheckRetail.AssertOnlyScriptedCases(GameDataPaths.RequireBase(), 4, output);

    [Fact]
    public void TheSampleAddsNoTargetFindingOnRetail() =>
        TargetCheckRetail.AssertTheSampleAddsNoFinding(GameDataPaths.RequireBase(), output);
}

/// <summary>The content checks on HD Edition 0.7.1. The tests need <c>OPENLANCER_HDE</c>, a copy of an HD Edition
/// install, and only read it. They skip without it.</summary>
public sealed class HdeTargetCheckRetailTests(ITestOutputHelper output)
{
    static HdeTargetCheckRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheHdDataGivesNoTargetFinding() =>
        // HD Edition 0.7.1 has eleven NPC ships without an engine: capital ships and a liner of the scripted scenes.
        TargetCheckRetail.AssertOnlyScriptedCases(GameDataPaths.RequireHde(), 11, output);

    [Fact]
    public void TheSampleAddsNoTargetFindingOnHd() =>
        TargetCheckRetail.AssertTheSampleAddsNoFinding(GameDataPaths.RequireHde(), output);
}
