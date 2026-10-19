using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The origins of the engine lines of a mod on HD Edition (test 10 of the console filter design). A mod in a temporary
/// folder has a synthetic patch. The test finds its sections at run time from the data, not from names in the code. The
/// test needs <c>OPENLANCER_HDE</c>, a copy of an HD Edition install. It only reads it, and it skips without it.
/// </summary>
public sealed class ModOriginRetailTests(ITestOutputHelper output)
{
    private const string ModId = "console-check";

    static ModOriginRetailTests() => AppHandler.ConsoleInit(silent: true);

    // The counts of the base run of the HD Edition copy, made once for the tests of the class.
    private static readonly Lazy<IReadOnlyDictionary<string, int>> BaseCounts =
        new(() => BaseEngineLines.Count(BaseEngineLines.Run(GameDataPaths.RequireHde()).All));

    /// <summary>One new object at the end of the first system file: each load line is a base-game line, also the lines
    /// of the sections of the file that the mod does not change.</summary>
    [Fact]
    public void OneNewObjectGivesNoModLine()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireHde(), ModId);
        var (path, archetype) = FirstSystem(workspace);
        WritePatch(workspace, path, NewObject("ol_check_a", archetype));

        var (lines, resolved) = Load(workspace);

        Assert.NotEmpty(lines);
        Assert.All(resolved, r => Assert.Equal(MessageOrigin.BaseGame, r.Origin));
    }

    /// <summary>A second new object with a duplicate key gives exactly one mod line, on the section of the new
    /// object.</summary>
    [Fact]
    public void ASecondNewObjectWithADuplicateKeyGivesOneModLine()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireHde(), ModId);
        var (path, archetype) = FirstSystem(workspace);
        WritePatch(workspace, path, NewObject("ol_check_a", archetype) + "\n" +
                                    NewObject("ol_check_b", archetype) + "ids_info = 0\nids_info = 0\n");

        var (lines, resolved) = Load(workspace);

        var mod = Assert.Single(resolved.Select((r, i) => (Resolved: r, Line: lines[i])), p => !p.Resolved.Origin.IsBaseGame);
        output.WriteLine(mod.Line.Normalized);
        Assert.Equal(MessageOrigin.Mod(ModId, ModEvidence.Section), mod.Resolved.Origin);
        Assert.Equal(new SectionIdentity("Object", "ol_check_b"), mod.Resolved.Target!.Id);
        Assert.StartsWith("Duplicate of ids_info", mod.Line.Core, StringComparison.Ordinal);
    }

    /// <summary>Review finding CF1: the mod deletes a gun block that a pilot names. The pilot does not change, but each
    /// line about it is a mod line, with the pilot as its section.</summary>
    [Fact]
    public void ADeletedGunBlockThatAPilotNamesGivesModLinesForThePilot()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireHde(), ModId);
        var (path, block) = NamedGunBlock(workspace);
        WritePatch(workspace, path, $"[GunBlock]\nnickname = {block}\nol.delete = true\n");

        var (lines, resolved) = Load(workspace);

        var mods = resolved.Select((r, i) => (Resolved: r, Line: lines[i])).Where(p => !p.Resolved.Origin.IsBaseGame).ToList();
        foreach (var (_, line) in mods)
            output.WriteLine(line.Normalized);
        Assert.NotEmpty(mods);
        Assert.All(mods, p =>
        {
            Assert.Equal((OriginKind.Mod, ModId), (p.Resolved.Origin.Kind, p.Resolved.Origin.ModId));
            Assert.Equal("Pilot", p.Line.Component);
            Assert.EndsWith($"Unable to find GunBlock '{block}'", p.Line.Core, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Pilot", p.Resolved.Target!.Id.Type);
        });
    }

    /// <summary>The engine lines of the data load of the edit view of the profile, and their origins.</summary>
    private static (IReadOnlyList<EngineLine> Lines, IReadOnlyList<ResolvedLine> Resolved) Load(RetailWorkspace workspace)
    {
        var session = workspace.Open();
        Assert.False(session.EditView.HasErrors, string.Join("\n", session.EditView.Diagnostics));
        var lines = BaseRunRetailTests.EditorLoad(session.EditView.CreateFileSystem());
        var origins = new EngineLineOrigins(session);
        Assert.True(origins.NeedsBaseRun);
        origins.BaseRunEnded(BaseCounts.Value);
        return (lines, origins.Resolve(lines));
    }

    /// <summary>The first system file of the universe, and the archetype of its first object.</summary>
    private static (string Path, string Archetype) FirstSystem(RetailWorkspace workspace)
    {
        var session = workspace.Open();
        try
        {
            var path = session.Systems.Files[0];
            var archetype = session.OpenDocument(path)!.Working
                .Where(s => s.Name.Equals("Object", StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s).First(e => e.Name.Equals("archetype", StringComparison.OrdinalIgnoreCase))[0].ToString();
            return (path, archetype);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>A pilot file with a gun block that a pilot names, and the nickname of the block.</summary>
    private static (string Path, string Block) NamedGunBlock(RetailWorkspace workspace)
    {
        var session = workspace.Open();
        try
        {
            var files = session.DataFiles.Paths("pilots").Select(p => (Path: p, Sections: session.OpenDocument(p)!.Working)).ToList();
            var named = files.SelectMany(f => f.Sections)
                .Where(s => s.Name.Equals("Pilot", StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s.Where(e => e.Name.Equals("gun_id", StringComparison.OrdinalIgnoreCase) && e.Count > 0))
                .Select(e => e[0].ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, sections) in files)
            {
                foreach (var section in sections.Where(s => s.Name.Equals("GunBlock", StringComparison.OrdinalIgnoreCase)))
                {
                    var nickname = section.FirstOrDefault(e => e.Name.Equals("nickname", StringComparison.OrdinalIgnoreCase))?[0]
                        .ToString();
                    if (nickname != null && named.Contains(nickname))
                        return (path, nickname);
                }
            }
            throw new InvalidOperationException("No pilot names a gun block.");
        }
        finally
        {
            session.Dispose();
        }
    }

    private static string NewObject(string nickname, string archetype) =>
        $"[Object]\nnickname = {nickname}\nol.new = true\npos = 0, 0, 0\narchetype = {archetype}\n";

    private static void WritePatch(RetailWorkspace workspace, string installPath, string text)
    {
        var file = Path.Combine(workspace.ModFolder, "patches", installPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
}
