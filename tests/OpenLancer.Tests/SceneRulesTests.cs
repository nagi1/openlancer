using System;
using System.IO;
using System.Linq;
using LibreLancer.Data.Schema.Solar;
using OpenLancer.Data.Editing.Lists;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The rule data of the scene edits of milestone M3: the delete actions, the owner scope and the templates.</summary>
public sealed class SceneRulesTests
{
    private static readonly ListRules Rules = ListRules.Retail;

    /// <summary>What a delete does with each reference (section 7 item 5 of the M3 design). Without a value, a rule with
    /// the effect load-break refuses the delete (decision D124 (a)), and each other rule leaves "Missing".</summary>
    [Fact]
    public void TheDeleteActionsOfTheRulesParse()
    {
        Assert.Equal(DeleteAction.Cascade, Assert.Single(Rules.Rules("system", "Object", "parent")).OnDelete);
        Assert.Equal(DeleteAction.Clear, Assert.Single(Rules.Rules("system", "Object", "ring")).OnDelete);
        Assert.Equal(DeleteAction.Refuse, Assert.Single(Rules.Rules("system", "Object", "next_ring")).OnDelete);
        Assert.Equal(DeleteAction.Refuse, Rules.Rules("system", "Object", "goto")[1].OnDelete);
        Assert.Equal(DeleteAction.Cascade, Assert.Single(Rules.Rules("system", "Nebula", "zone")).OnDelete);
        Assert.Equal(DeleteAction.Cascade, Assert.Single(Rules.Rules("system", "Asteroids", "zone")).OnDelete);
        Assert.Equal(DeleteAction.Refuse, Assert.Single(Rules.Rules("system", "LightSource", "atten_curve")).OnDelete);
        Assert.Equal(DeleteAction.Missing, Assert.Single(Rules.Rules("system", "Object", "loadout")).OnDelete);
        foreach (var role in new[] { "nebula_files", "field_files" })
        {
            var hole = Assert.Single(Rules.Rules(role, "Exclusion Zones", "exclusion"));
            Assert.Equal((DeleteAction.ClearHole, true, true, RuleEffect.Degrade), (hole.OnDelete, hole.OwnerScope, hole.Closed, hole.Effect));
            Assert.Equal(DeleteAction.ClearHole, Assert.Single(Rules.Rules(role, "Exclusion Zones", "exclude")).OnDelete);
        }
        var loot = Assert.Single(Rules.Rules("field_files", "LootableZone", "zone"));
        Assert.Equal((DeleteAction.Cascade, true, false), (loot.OnDelete, loot.OwnerScope, loot.Closed));
        Assert.Equal(("Nebula", "Asteroids"), (Rules.Roles["nebula_files"].FieldFiles, Rules.Roles["field_files"].FieldFiles));

        var bad = Assert.Throws<InvalidDataException>(() => ListRules.Parse(FilesRules(""", "onDelete": "explode" """)));
        Assert.Contains("onDelete value 'explode'", bad.Message);
        var owner = Assert.Throws<InvalidDataException>(() => ListRules.Parse(FilesRules(""", "scope": "owner" """)));
        Assert.Contains("needs a role of nebula or field files", owner.Message);
    }

    /// <summary>The templates of Create (decisions D233 and D253): no template holds a connection type, and each other
    /// archetype type of the engine is in exactly one template.</summary>
    [Fact]
    public void EachArchetypeTypeIsInOneTemplateOrIsAConnection()
    {
        // NONE is the value of an archetype without a type, not a type of the data.
        var types = Enum.GetNames<ArchetypeType>().Where(n => n != nameof(ArchetypeType.NONE))
            .Select(n => n.ToLowerInvariant()).ToList();
        var connections = Rules.ConnectionTypes;

        Assert.Equal(["jump_gate", "jump_hole", "jumphole", "airlock_gate", "tradelane_ring"], connections);
        Assert.All(connections, c => Assert.Contains(c, types));
        foreach (var type in types)
        {
            var count = Rules.SceneTemplates.Count(t => t.Types.Contains(type));
            Assert.True(count == (connections.Contains(type) ? 0 : 1), $"{type} is in {count} templates.");
        }
        Assert.Equal(["station", "planet", "sun", "satellite", "solar"], Rules.SceneTemplates.Select(t => t.Id));
        Assert.Equal(("docking_sphere", "base"), (Rules.SceneTemplates[0].Needs, Rules.SceneTemplates[0].Then));

        var json = FilesRules("").TrimEnd()[..^1] +
                   """, "connectionTypes": ["jump_gate"], "sceneTemplates": [ { "id": "x", "label": "X", "types": ["JUMP_GATE"] } ] }""";
        Assert.Contains("has the connection type 'jump_gate'",
            Assert.Throws<InvalidDataException>(() => ListRules.Parse(json)).Message);
    }

    /// <summary>A minimal rule data with one files list and one rule of nebula files.</summary>
    private static string FilesRules(string ruleFlags) => $$"""
        {
          "roles": { "system": { "systemFiles": true } },
          "lists": [
            { "id": "zone_of_system", "label": "Zone", "kind": "Zone", "source": { "scope": "file", "sections": ["zone"] } }
          ],
          "rules": [
            { "role": "system", "section": "Nebula", "key": "zone", "list": "zone_of_system", "closed": true,
              "effect": "drop", "evidence": "x"{{ruleFlags}} }
          ]
        }
        """;
}
