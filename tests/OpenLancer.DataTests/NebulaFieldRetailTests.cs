using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of milestone M25: the key rules of the nebula and asteroid field files, and the exclusion line group.
/// </summary>
public sealed class NebulaFieldRetailTests(ITestOutputHelper output)
{
    private const string CrowPath = @"DATA\SOLAR\NEBULA\Bw05_Crow_nebula.ini";
    private const string CrowZone = "Zone_Bw05_Crow_nebula";

    static NebulaFieldRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// Each section type of the nebula and field files that the retail systems name has a key rule. A patch can find
    /// each of their sections, except the two [NebulaLight] sections of two edge nebulae. The engine reads only the
    /// first of them.
    /// </summary>
    [Fact]
    public void APatchCanFindEachRetailNebulaAndFieldSectionExceptTwoNebulaLights()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var files = FieldFiles(baseFolder);
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var refused = new List<string>();
        foreach (var file in files)
        {
            var sections = IniDocument.Parse(file, File.ReadAllBytes(Path.Combine(baseFolder, "DATA", file))).Sections;
            var counts = new KeyCounts(sections);
            foreach (var section in sections)
            {
                types.Add(section.Name);
                if (PatchWriter.NotPatchable(section, kept: true, counts, counts, $@"DATA\{file}") is { } reason)
                    refused.Add($"{Path.GetFileName(file)} [{section.Name}]: {reason}");
            }
        }
        var nebulae = files.Count(f => f.StartsWith(@"solar\nebula\", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"{files.Count} files ({nebulae} nebula files), types: {string.Join(", ", types)}");
        foreach (var line in refused)
            output.WriteLine(line);

        Assert.Equal((208, 57), (files.Count, nebulae));
        Assert.Equal(["AsteroidBillboards", "BackgroundLightning", "Band", "Clouds", "Cube", "DynamicAsteroids",
                "DynamicLightning", "Exclusion Zones", "ExclusionBand", "Exterior", "Field", "Fog", "LootableZone",
                "NebulaLight", "properties", "TexturePanels"],
            types, StringComparer.OrdinalIgnoreCase);
        // No type falls back to the nickname of the default rule.
        Assert.All(types, t => Assert.NotEqual(SectionKeys.DefaultKey, SectionKeys.For(t).KeyEntry));
        const string twoLights = "[NebulaLight]: the file has more than one [NebulaLight] section.";
        Assert.Equal([$"Bw06_Kunashir_edge_nebula.ini {twoLights}", $"Bw06_Kunashir_edge_nebula.ini {twoLights}",
                $"Hi01_Siniestre_edge_nebula.ini {twoLights}", $"Hi01_Siniestre_edge_nebula.ini {twoLights}"],
            refused.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The verify step of roadmap section 6.7: a patch of the [Exclusion Zones] of the Crow nebula applies, and the
    /// engine loads it. The patch lists each line of the section (the exclusion line group). It changes the fog_far of
    /// the first hole and drops the last hole. The patch comes from the base file at run time, so no retail section is
    /// in the repo. A patch that lists the lines of one hole only is a merge error.
    /// </summary>
    [Fact]
    public void APatchOfTheCrowExclusionZonesApplies()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var lower = IniDocument.Parse(CrowPath, File.ReadAllBytes(Path.Combine(baseFolder, CrowPath)));
        var holes = Holes(lower.Sections.Single(s => s.Name.Equals("Exclusion Zones", StringComparison.OrdinalIgnoreCase)));
        var kept = holes.SkipLast(1).ToList();
        var patch = new StringBuilder("; M25 retail proof: a wider fog in the first hole, and no last hole.\r\n");
        patch.Append("[Exclusion Zones]\r\n");
        foreach (var (hole, index) in kept.Select((h, i) => (h, i)))
        {
            foreach (var line in hole.Lines)
            {
                var values = index == 0 && line.Name.Equals("fog_far", StringComparison.OrdinalIgnoreCase)
                    ? "7500"
                    : string.Join(", ", line.Select(v => v.ToString()));
                patch.Append($"{line.Name} = {values}\r\n");
            }
        }
        Assert.Contains(holes[0].Lines, l => l.Name.Equals("fog_far", StringComparison.OrdinalIgnoreCase));

        using var workspace = new RetailWorkspace(baseFolder, "crowholes");
        var patchFile = Path.Combine(workspace.ModFolder, "patches", CrowPath);
        Directory.CreateDirectory(Path.GetDirectoryName(patchFile)!);
        File.WriteAllText(patchFile, patch.ToString(), Encoding.ASCII);
        var profile = ModProfile.Load(workspace.ProfilePath);
        // Mod > New mod writes format version 2 with tier 1.
        Assert.Equal(ModManifest.DataTier, profile.Mods[^1].Manifest.Tier);
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance,
            ThornEvaluator.Instance);
        Assert.DoesNotContain(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var db = EngineDataLoader.Load(overlay.CreateFileSystem()).RequireComplete();
        var system = db.Systems.Get("bw05")!;
        var nebula = system.Nebulae.Single(n => n.Zone!.Nickname.Equals(CrowZone, StringComparison.OrdinalIgnoreCase));
        var expected = kept.Select(h => h.Zone)
            .Where(z => system.Zones.Any(s => s.Nickname.Equals(z, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        output.WriteLine($"{holes.Count} holes in the base file, {nebula.ExclusionZones!.Count} after the patch.");
        Assert.Equal(expected, nebula.ExclusionZones.Select(e => e.Zone!.Nickname), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(holes.Count - 1, nebula.ExclusionZones.Count);
        Assert.Equal(7500f, nebula.ExclusionZones[0].FogFar);
        Assert.DoesNotContain(nebula.ExclusionZones, e => e.Zone!.Nickname.Equals(holes[^1].Zone, StringComparison.OrdinalIgnoreCase));

        var problems = ProfileValidator.NewProblems(FileSystem.FromPath(baseFolder), overlay.CreateFileSystem(),
            overlay.CutscenePaths);
        foreach (var line in problems)
            output.WriteLine(line.ToString());
        Assert.DoesNotContain(problems, p => p.Severity == DiagnosticSeverity.Error);

        // The lines of one hole only: the other lines of the group would lose their place.
        File.WriteAllText(patchFile, $"[Exclusion Zones]\r\nexclusion = {holes[0].Zone}\r\nfog_far = 7500\r\n", Encoding.ASCII);
        var partial = ModOverlay.Build(profile.BaseFolder, ModProfile.Load(workspace.ProfilePath).Mods,
            DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        Assert.Contains(partial.Diagnostics, d => d.Severity == DiagnosticSeverity.Error &&
                                                  d.Message.Contains("lines belong together"));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>The holes of an [Exclusion Zones] section as the engine reads them: each exclusion or exclude line
    /// starts a hole, and the lines after it belong to it (upstream ParseHelpers.Chunk).</summary>
    private static List<(string Zone, List<Entry> Lines)> Holes(Section section)
    {
        var holes = new List<(string Zone, List<Entry> Lines)>();
        foreach (var line in section)
        {
            if (line.Name.Equals("exclusion", StringComparison.OrdinalIgnoreCase) ||
                line.Name.Equals("exclude", StringComparison.OrdinalIgnoreCase))
                holes.Add((line[0].ToString(), []));
            holes[^1].Lines.Add(line);
        }
        return holes;
    }

    /// <summary>The nebula and field files that the [Nebula] and [Asteroids] sections of the retail system files name,
    /// as paths below the data folder.</summary>
    private static List<string> FieldFiles(string baseFolder)
    {
        var systems = Path.Combine(baseFolder, "DATA", "UNIVERSE", "SYSTEMS");
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(systems))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.ini", SearchOption.TopDirectoryOnly))
            {
                using var stream = File.OpenRead(file);
                foreach (var section in IniFile.ParseFile(file, stream))
                {
                    if (section.Name.Equals("Nebula", StringComparison.OrdinalIgnoreCase) ||
                        section.Name.Equals("Asteroids", StringComparison.OrdinalIgnoreCase))
                    {
                        if (section.FirstOrDefault(e => e.Name.Equals("file", StringComparison.OrdinalIgnoreCase)) is
                            { Count: > 0 } line)
                            files.Add(line[0].ToString().Replace('/', '\\'));
                    }
                }
            }
        }
        return files.Where(f => File.Exists(Path.Combine(baseFolder, "DATA", f))).ToList();
    }
}
