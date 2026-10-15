using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The table of live actions has one row for each key of the upstream schema of each section type that the scene
/// shows. So a new upstream key fails the test until someone decides its effect.
/// </summary>
public sealed class SceneFieldsTests
{
    /// <summary>The schema of each scene section: the section types of a system file and the [system] of
    /// universe.ini.</summary>
    private static Dictionary<SceneSection, SectionSchema> Schemas()
    {
        var schemas = new Dictionary<SceneSection, SectionSchema>();
        foreach (var (ini, type, schema) in SchemaCatalog.All.Where(s => s.Ini == typeof(StarSystem)))
        {
            if (SceneFields.SectionOf(type, universe: false) is { } section)
                schemas.Add(section, schema);
        }
        schemas.Add(SceneFields.SectionOf("system", universe: true)!.Value,
            SchemaCatalog.SectionClass(typeof(StarSystem), "system"));
        return schemas;
    }

    [Fact]
    public void EachSchemaKeyOfEachSceneSectionHasExactlyOneRow()
    {
        var schemas = Schemas();

        Assert.Equal(Enum.GetValues<SceneSection>().Order(), schemas.Keys.Order());
        foreach (var (section, schema) in schemas)
        {
            var keys = schema.Keys.Select(k => k.Name.ToLowerInvariant()).Order(StringComparer.Ordinal).ToList();
            var rows = SceneFields.Rows.Where(r => r.Section == section)
                .Select(r => r.Key.ToLowerInvariant()).Order(StringComparer.Ordinal).ToList();
            Assert.True(keys.SequenceEqual(rows),
                $"{section}: schema keys [{string.Join(", ", keys)}], rows [{string.Join(", ", rows)}]");
        }
        Assert.Equal(SceneFields.Rows.Count, schemas.Values.Sum(s => s.Keys.Count));
    }

    [Fact]
    public void EachAliasHasItsOwnRowAndAKeyOutsideTheSchemaHasNone()
    {
        Assert.Equal(SceneEffect.Data, SceneFields.Effect(SceneSection.Object, "ambient"));
        Assert.Equal(SceneEffect.Data, SceneFields.Effect(SceneSection.Object, "ambient_color"));
        Assert.Single(SceneFields.Rows, r => r.Section == SceneSection.Dust && r.Key == "dust");
        Assert.Single(SceneFields.Rows, r => r.Section == SceneSection.Dust && r.Key == "spacedust");
        // Retail has a name line in 31 [SystemInfo] sections, but the schema does not know the key.
        Assert.DoesNotContain(SceneFields.Rows, r => r.Section == SceneSection.SystemInfo && r.Key == "name");
        Assert.Equal(SceneEffect.None, SceneFields.Effect(SceneSection.SystemInfo, "name"));
        Assert.Equal(SceneEffect.None, SceneFields.Effect(SceneSection.LightSource, "color_curve"));
    }

    /// <summary>
    /// The effect and the lock of each row, from the tables of the design (sections 7.2 to 7.5). The applier runs these
    /// effects, so a changed effect fails this test until the design changes too. The locked rows are the key entries,
    /// the linked group, the file of a system and the preloads.
    /// </summary>
    [Fact]
    public void EachRowHasTheEffectAndTheLockOfTheDesign()
    {
        const SceneEffect none = SceneEffect.None, transform = SceneEffect.Transform, data = SceneEffect.Data;
        const SceneEffect label = SceneEffect.Label, roles = SceneEffect.Roles, lanes = SceneEffect.Lanes;
        const SceneEffect reinit = SceneEffect.Reinit;
        var expected = new List<string>();
        void Rows(SceneSection section, SceneEffect effect, bool locked, params string[] keys) =>
            expected.AddRange(keys.Select(k => Describe(section, k, effect, locked)));

        // 7.2 [Object]
        Rows(SceneSection.Object, none, true, "nickname");
        Rows(SceneSection.Object, transform, false, "pos", "rotate");
        Rows(SceneSection.Object, data | label | lanes | reinit, false, "archetype");
        Rows(SceneSection.Object, data | reinit, false, "loadout", "star", "spin", "dock_with", "goto");
        Rows(SceneSection.Object, data | lanes | reinit, false, "prev_ring", "next_ring");
        Rows(SceneSection.Object, data | label, false, "tradelane_space_name", "ids_name");
        Rows(SceneSection.Object, data, false, "ids_info", "base", "reputation", "faction", "pilot", "behavior",
            "difficulty_level", "msg_id_prefix", "parent", "voice", "space_costume", "jump_effect", "comment",
            "atmosphere_range", "burn_color", "ambient_color", "ambient", "visit");
        Rows(SceneSection.Object, data | roles, false, "ring");
        Rows(SceneSection.Object, none, false, "size");
        // 7.3 [Zone]
        Rows(SceneSection.Zone, none, true, "nickname");
        Rows(SceneSection.Zone, transform, false, "pos", "rotate");
        Rows(SceneSection.Zone, transform | SceneEffect.Volume, false, "size");
        Rows(SceneSection.Zone, transform | roles | SceneEffect.Volume, false, "shape");
        Rows(SceneSection.Zone, data | roles, true, "encounter", "faction", "faction_weight");
        Rows(SceneSection.Zone, data | roles, false, "path_label", "lane_id");
        Rows(SceneSection.Zone, data | label, false, "ids_name");
        Rows(SceneSection.Zone, data, false, "ids_info", "edge_fraction", "property_flags", "property_fog_color", "visit",
            "sort", "music", "spacedust", "spacedust_maxparticles", "interference", "power_modifier", "drag_modifier",
            "comment", "tradelane_attack", "tradelane_down", "damage", "toughness", "density", "population_additive",
            "mission_eligible", "max_battle_size", "pop_type", "relief_time", "repop_time", "attack_ids", "mission_type",
            "usage", "vignette_type", "density_restriction");
        Rows(SceneSection.Zone, none, false, "zone_creation_distance", "spin", "reputation");
        // 7.4 [LightSource]: the Transform effect of a light sets its position and loads the lights again.
        Rows(SceneSection.LightSource, none, true, "nickname");
        Rows(SceneSection.LightSource, transform, false, "pos");
        Rows(SceneSection.LightSource, SceneEffect.Light, false, "color", "range", "type", "attenuation", "direction",
            "atten_curve");
        Rows(SceneSection.LightSource, none, false, "rotate", "ids_name");
        // 7.5 The system sections and universe.ini
        Rows(SceneSection.SystemInfo, SceneEffect.Environment, false, "space_color");
        Rows(SceneSection.SystemInfo, data, false, "local_faction");
        Rows(SceneSection.SystemInfo, none, false, "space_farclip", "rpop_solar_detection");
        Rows(SceneSection.Ambient, SceneEffect.Environment, false, "color");
        Rows(SceneSection.Background, SceneEffect.Starsphere, false, "basic_stars", "complex_stars", "nebulae");
        Rows(SceneSection.Dust, none, false, "spacedust", "dust", "spacedust_maxparticles");
        Rows(SceneSection.Music, none, false, "space", "danger", "battle");
        Rows(SceneSection.TexturePanels, none, false, "file");
        Rows(SceneSection.Preloads, none, true, "ship", "simple", "solar", "equipment", "snd", "voice");
        Rows(SceneSection.EncounterParameters, none, true, "nickname");
        Rows(SceneSection.EncounterParameters, none, false, "filename");
        Rows(SceneSection.UniverseSystem, none, true, "nickname", "file");
        Rows(SceneSection.UniverseSystem, data | label, false, "strid_name");
        Rows(SceneSection.UniverseSystem, data, false, "navmapscale");
        Rows(SceneSection.UniverseSystem, none, false, "ids_info", "pos", "msg_id_prefix", "visit", "name");

        Assert.Equal(expected.Order(StringComparer.Ordinal),
            SceneFields.Rows.Select(r => Describe(r.Section, r.Key, r.Effect, r.Locked)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EachRowHasEvidence() =>
        Assert.All(SceneFields.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Evidence), $"{r.Section} {r.Key}"));

    [Fact]
    public void TheLookupIgnoresLetterCaseAndEachDeferredEffectComesWithAnEffectThatRunsAtOnce()
    {
        Assert.Equal(SceneEffect.Transform, SceneFields.Effect(SceneSection.Object, "POS"));
        // Each deferred effect comes with an effect that runs at once, so the scene item holds the new values first.
        Assert.All(SceneFields.Rows.Where(r => (r.Effect & SceneEffect.Reinit) != 0),
            r => Assert.True((r.Effect & SceneEffect.Data) != 0, r.Key));
        Assert.All(SceneFields.Rows.Where(r => (r.Effect & SceneEffect.Volume) != 0),
            r => Assert.True((r.Effect & SceneEffect.Transform) != 0, r.Key));
        Assert.Equal(SceneEffect.Starsphere, SceneFields.All(SceneSection.Background));
        Assert.Equal(SceneEffect.None, SceneFields.All(SceneSection.Music));
    }

    [Fact]
    public void ASectionTypeMapsToItsSceneSectionOnlyInItsFile()
    {
        Assert.Equal(SceneSection.Zone, SceneFields.SectionOf("zone", universe: false));
        Assert.Equal(SceneSection.Preloads, SceneFields.SectionOf("Archetype", universe: false));
        Assert.Equal(SceneSection.UniverseSystem, SceneFields.SectionOf("System", universe: true));
        Assert.Null(SceneFields.SectionOf("system", universe: false));
        Assert.Null(SceneFields.SectionOf("Object", universe: true));
        // Field and nebula files are later work.
        Assert.Null(SceneFields.SectionOf("Nebula", universe: false));
        Assert.Null(SceneFields.SectionOf("Asteroids", universe: false));
    }

    private static string Describe(SceneSection section, string key, SceneEffect effect, bool locked) =>
        $"{section} {key}: {effect}{(locked ? ", locked" : "")}";
}
