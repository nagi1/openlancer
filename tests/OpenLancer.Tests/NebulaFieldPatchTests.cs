using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// Milestone M25: the key rules of the section types of the nebula and asteroid field files, and the exclusion line
/// group. A patch section finds its target by these rules, in the merge and in the editor.
/// </summary>
public sealed class NebulaFieldPatchTests
{
    private const string NebulaPath = @"DATA\SOLAR\NEBULA\crow.ini";
    private const string FieldPath = @"DATA\SOLAR\ASTEROIDS\field.ini";

    private static readonly string Nebula = """
        [TexturePanels]
        file = solar\nebula\crow_shapes.ini

        [Fog]
        fog_enabled = 1
        near = 0
        distance = 1600
        color = 0, 100, 160

        [properties]
        flag = nebula

        [Exclusion Zones]
        exclusion = Zone_a
        fog_far = 5000
        zone_shell = solar\nebula\crow_exclusion.3db
        shell_scalar = 1
        max_alpha = 0.5
        exclusion_tint = 255, 255, 255
        exclusion = Zone_b
        fog_far = 2000
        exclusion = Zone_c

        [NebulaLight]
        ambient = 50, 80, 100

        [Clouds]
        puff_count = 20
        """.ReplaceLineEndings("\n");

    private static readonly string Field = """
        [Field]
        cube_size = 500
        fill_dist = 1200

        [Cube]
        asteroid = rock1, 0.1, 0.2, 0.3, 10, 20, 30

        [Band]
        render_parts = 8

        [Exclusion Zones]
        exclusion = Zone_x
        exclude_billboards = 1
        exclusion = Zone_y

        [DynamicAsteroids]
        asteroid = dasteroid_a
        count = 10

        [DynamicAsteroids]
        asteroid = dasteroid_b
        count = 5

        [LootableZone]
        zone = Zone_mine
        dynamic_loot_count = 1, 2

        [LootableZone]
        dynamic_loot_count = 3, 4
        """.ReplaceLineEndings("\n");

    [Theory]
    [InlineData("Fog")]
    [InlineData("Exterior")]
    [InlineData("NebulaLight")]
    [InlineData("Clouds")]
    [InlineData("BackgroundLightning")]
    [InlineData("DynamicLightning")]
    [InlineData("Cube")]
    [InlineData("Field")]
    [InlineData("Band")]
    [InlineData("ExclusionBand")]
    [InlineData("AsteroidBillboards")]
    [InlineData("properties")]
    [InlineData("Exclusion Zones")]
    [InlineData("TexturePanels")]
    public void EachTypeThatTheEngineReadsOnceIsASingleton(string type) =>
        Assert.Equal(SectionMatchKind.Singleton, SectionKeys.For(type).Kind);

    [Fact]
    public void TheRepeatedTypesHaveAKey()
    {
        Assert.Equal(new SectionMatch(SectionMatchKind.Keyed, "asteroid"), SectionKeys.For("dynamicasteroids"));
        Assert.Equal(new SectionMatch(SectionMatchKind.Keyed, "zone", KeyOptional: true), SectionKeys.For("LootableZone"));
        Assert.Equal(["exclusion", "exclude", "fog_far", "fog_near", "zone_shell", "shell_scalar", "max_alpha", "color",
                "exclusion_tint", "exclude_billboards", "exclude_dynamic_asteroids", "empty_cube_frequency", "billboard_count"],
            Assert.Single(SectionKeys.LinkedGroups("exclusion zones")));
    }

    [Fact]
    public void APatchChangesTheFogAndTheLightOfANebula()
    {
        var merged = Merge(NebulaPath, Nebula, "[Fog]\ndistance = 2400\n[NebulaLight]\nambient = 10, 20, 30\n", out var problems);

        Assert.Empty(problems);
        var fog = merged.Single(s => s.Name == "Fog");
        Assert.Equal(["fog_enabled", "near", "distance", "color"], fog.Select(e => e.Name));
        Assert.Equal(2400, fog["distance"]![0].ToInt32());
        Assert.Equal("10, 20, 30", Values(merged.Single(s => s.Name == "NebulaLight")["ambient"]!));
    }

    /// <summary>The engine reads only the first [NebulaLight]. Two retail files have two, so a patch cannot find
    /// one of them.</summary>
    [Fact]
    public void APatchCannotFindOneOfTwoSectionsOfASingletonType()
    {
        Merge(NebulaPath, Nebula + "\n[NebulaLight]\nambient = 1, 1, 1\n", "[NebulaLight]\nambient = 10, 20, 30\n",
            out var problems);

        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error &&
                                       p.Message.Contains("[NebulaLight]: matches 2 sections"));
    }

    /// <summary>The exclusion line group: a patch that changes one line of an excluded zone lists all lines of the
    /// section, so each line stays with its zone. The merge writes the lines in the order of the patch.</summary>
    [Fact]
    public void AnExclusionPatchListsTheWholeGroup()
    {
        Merge(NebulaPath, Nebula, "[Exclusion Zones]\nexclusion = Zone_b\nfog_far = 2500\n", out var partial);
        var merged = Merge(NebulaPath, Nebula, """
            [Exclusion Zones]
            exclusion = Zone_a
            fog_far = 5000
            zone_shell = solar\nebula\crow_exclusion.3db
            shell_scalar = 1.2
            max_alpha = 0.5
            exclusion_tint = 255, 255, 255
            exclusion = Zone_c
            fog_far = 3000
            """, out var problems);

        var error = Assert.Single(partial);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("the exclusion, exclude, fog_far, fog_near, zone_shell, shell_scalar, max_alpha, color, " +
                        "exclusion_tint, exclude_billboards, exclude_dynamic_asteroids, empty_cube_frequency, " +
                        "billboard_count lines belong together. The patch changes exclusion, fog_far, so it must also " +
                        "list zone_shell, shell_scalar, max_alpha, exclusion_tint.", error.Message);
        Assert.Empty(problems);
        // Zone_b and its line go, and Zone_c gets a fog_far line.
        Assert.Equal(["exclusion = Zone_a", "fog_far = 5000", @"zone_shell = solar\nebula\crow_exclusion.3db",
                "shell_scalar = 1.2", "max_alpha = 0.5", "exclusion_tint = 255, 255, 255", "exclusion = Zone_c",
                "fog_far = 3000"],
            merged.Single(s => s.Name == "Exclusion Zones").Select(e => $"{e.Name} = {Values(e)}"));
    }

    [Fact]
    public void APatchFindsADynamicAsteroidsSectionByItsAsteroid()
    {
        var merged = Merge(FieldPath, Field, "[DynamicAsteroids]\nasteroid = DASTEROID_B\ncount = 7\n", out var problems);

        Assert.Empty(problems);
        Assert.Equal([10, 7], merged.Where(s => s.Name == "DynamicAsteroids").Select(s => s["count"]![0].ToInt32()));
    }

    /// <summary>A [LootableZone] without a zone line gives the loot of the whole field. A patch section without a zone
    /// line finds it, and a patch section with a zone line finds the section of that zone.</summary>
    [Fact]
    public void APatchFindsTheLootOfTheWholeFieldWithoutAZoneLine()
    {
        var merged = Merge(FieldPath, Field,
            "[LootableZone]\ndynamic_loot_count = 5, 6\n[LootableZone]\nzone = zone_MINE\ndynamic_loot_count = 7, 8\n",
            out var problems);

        Assert.Empty(problems);
        Assert.Equal(["7, 8", "5, 6"],
            merged.Where(s => s.Name == "LootableZone").Select(s => Values(s["dynamic_loot_count"]!)));
    }

    [Fact]
    public void AFieldHasOneLootSectionWithoutAZoneLine()
    {
        Merge(FieldPath, Field, "[LootableZone]\nol.new = true\ndynamic_loot_count = 1, 1\n", out var added);
        Merge(FieldPath, Field.Replace("[LootableZone]\nzone = Zone_mine\n", "[LootableZone]\n"),
            "[LootableZone]\ndynamic_loot_count = 1, 1\n", out var ambiguous);
        var deleted = Merge(FieldPath, Field, "[LootableZone]\nol.delete = true\n", out var deleteProblems);

        Assert.Equal("[LootableZone] without zone: ol.new is set, but the section already exists.",
            Assert.Single(added).Message);
        Assert.Contains("[LootableZone] without zone: matches 2 sections", Assert.Single(ambiguous).Message);
        Assert.Empty(deleteProblems);
        Assert.Equal("Zone_mine", Assert.Single(deleted, s => s.Name == "LootableZone")["zone"]![0].ToString());
    }

    [Fact]
    public void ALootPatchWithTwoZoneLinesIsRefused()
    {
        Merge(FieldPath, Field, "[LootableZone]\nzone = a\nzone = b\n", out var problems);

        Assert.Equal("[LootableZone] needs exactly one 'zone' entry with one value to find its target, or no 'zone' " +
                     "entry for the section without one.", Assert.Single(problems).Message);
    }

    // The editor writes these patches.

    [Fact]
    public void TheEditorWritesAFogChange()
    {
        var patch = EditorPatch(Nebula, Nebula.Replace("distance = 1600", "distance = 2000"));

        Assert.Equal(PatchWriterTests.Lines("[Fog]", "distance = 2000"), patch);
    }

    [Fact]
    public void TheEditorWritesTheWholeExclusionGroupForOneChangedLine()
    {
        var patch = EditorPatch(Nebula, Nebula.Replace("fog_far = 2000", "fog_far = 2500"));

        Assert.Equal(PatchWriterTests.Lines("[Exclusion Zones]", "exclusion = Zone_a", "fog_far = 5000",
            @"zone_shell = solar\nebula\crow_exclusion.3db", "shell_scalar = 1", "max_alpha = 0.5",
            "exclusion_tint = 255, 255, 255", "exclusion = Zone_b", "fog_far = 2500", "exclusion = Zone_c"), patch);
    }

    [Fact]
    public void TheEditorWritesTheLootOfTheWholeFieldWithoutAZoneLine()
    {
        var changed = EditorPatch(Field, Field.Replace("dynamic_loot_count = 3, 4", "dynamic_loot_count = 9, 9"));
        var deleted = EditorPatch(Field, Field.Replace("\n[LootableZone]\ndynamic_loot_count = 3, 4", ""));
        var noLoot = Field.Replace("\n[LootableZone]\ndynamic_loot_count = 3, 4", "");
        var added = EditorPatch(noLoot, noLoot + "\n[LootableZone]\ndynamic_loot_count = 2, 2\n");

        Assert.Equal(PatchWriterTests.Lines("[LootableZone]", "dynamic_loot_count = 9, 9"), changed);
        Assert.Equal(PatchWriterTests.Lines("[LootableZone]", "ol.delete = true"), deleted);
        Assert.Equal(PatchWriterTests.Lines("[LootableZone]", "ol.new = true", "dynamic_loot_count = 2, 2"), added);
        Assert.Equal("[LootableZone] without zone",
            new SectionIdentity("LootableZone", null).ToString());
    }

    [Fact]
    public void TheEditorCannotChangeOneOfTwoLootSectionsWithoutAZoneLine()
    {
        var twice = Field.Replace("[LootableZone]\nzone = Zone_mine\n", "[LootableZone]\n");

        var result = PatchWriter.Write(FieldPath, PatchWriterTests.Text(twice),
            PatchWriterTests.Text(twice.Replace("dynamic_loot_count = 3, 4", "dynamic_loot_count = 9, 9")).Sections, _ => []);

        Assert.Equal("[LootableZone] without zone #2: a patch cannot change this section, because the file has more " +
                     "than one [LootableZone] section without a 'zone' entry.", Assert.Single(result.Problems).Message);
    }

    private static string EditorPatch(string b, string w)
    {
        var result = PatchWriter.Write(FieldOrNebula(b), PatchWriterTests.Text(b), PatchWriterTests.Text(w).Sections, _ => []);
        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        return result.Text;
    }

    private static string FieldOrNebula(string text) => text.Contains("[Fog]") ? NebulaPath : FieldPath;

    /// <summary>The merged sections of a file of the base data with one patch.</summary>
    private static List<Section> Merge(string path, string baseText, string patch, out List<Diagnostic> problems)
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(path, baseText);
        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\" + path] = patch }));
        problems = overlay.Diagnostics.ToList();
        return TestWorkspace.Sections(overlay, path);
    }

    private static string Values(Entry entry) => string.Join(", ", entry.Select(v => v.ToString()));
}
