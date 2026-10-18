using System.Collections.Generic;
using LibreLancer.Data;
using OpenLancer.Data.Editing;

namespace OpenLancer.Tests;

/// <summary>
/// A small synthetic install for the scene edits of milestone M3, on top of <see cref="ListWorkspace"/>: solar archetypes
/// of each template, Li01 with a station that has children and grandchildren, a jump gate pair, a trade lane, a planet
/// with a ring, a nebula and a field on one zone, zones with holes and loot, lights with a repeated nickname, gate locks
/// and story files.
/// </summary>
internal static class SceneWorkspace
{
    public const string Li01Path = ListWorkspace.Li01Path;
    public const string Li02Path = ListWorkspace.Li02Path;
    public const string CloudPath = @"DATA\SOLAR\NEBULA\cloud.ini";
    public const string RocksPath = @"DATA\SOLAR\ASTEROIDS\rocks.ini";
    public const string MissionPath = @"DATA\MISSIONS\M01A\m01a.ini";
    public const string KnowledgeMapPath = @"DATA\INTERFACE\KnowledgeMap.ini";

    public const string Li01Text = """
        [Object]
        nickname = Li01_01
        archetype = station1
        base = Li01_01_Base
        dock_with = Li01_01_Base
        loadout = lo_station
        reputation = li_n_grp
        [Object]
        nickname = Li01_dock
        archetype = station1
        dock_with = Li01_01_Base
        [Object]
        nickname = Li01_child
        archetype = station1
        parent = Li01_01
        [Object]
        nickname = Li01_grandchild
        archetype = buoy
        parent = Li01_child
        [Object]
        nickname = Li01_to_Li02
        archetype = jumpgate
        goto = Li02, Li02_to_Li01, gate_tunnel_bretonia
        [Object]
        nickname = ring1
        archetype = ring
        next_ring = ring2
        [Object]
        nickname = ring2
        archetype = ring
        prev_ring = ring1
        [Object]
        nickname = Li01_planet
        archetype = planet1
        ring = Zone_ring, solar\rings\ring.ini
        [zone]
        nickname = Zone_ring
        pos = 0, 0, 0
        shape = RING
        size = 2000, 1000, 200
        [zone]
        nickname = Zone_cloud
        pos = 0, 0, 0
        shape = SPHERE
        size = 5000
        [zone]
        nickname = Zone_hole
        pos = 100, 0, 0
        shape = SPHERE
        size = 500
        [zone]
        nickname = Zone_Tradelane_a
        pos = 0, 0, 0
        shape = BOX
        size = 10, 10, 1000
        [Nebula]
        file = solar\nebula\cloud.ini
        zone = Zone_cloud
        [Asteroids]
        file = solar\asteroids\rocks.ini
        zone = Zone_cloud
        [LightSource]
        nickname = light1
        pos = 0, 0, 0
        type = POINT
        [LightSource]
        nickname = twin
        pos = 0, 0, 0
        [LightSource]
        nickname = twin
        pos = 1, 0, 0
        """;

    public const string Li02Text = """
        [Object]
        nickname = Li02_to_Li01
        archetype = jumpgate
        goto = Li01, Li01_to_Li02, gate_tunnel_bretonia
        [Object]
        nickname = Li02_01
        archetype = station1
        dock_with = Li01_01_Base
        [zone]
        nickname = Zone_li02
        pos = 0, 0, 0
        shape = SPHERE
        size = 100
        """;

    public const string Solars = """
        [Solar]
        nickname = station1
        type = STATION
        docking_sphere = berth, HpDockMountA, 50
        [Solar]
        nickname = station_nodock
        type = STATION
        [Solar]
        nickname = dock_ring
        type = DOCKING_RING
        docking_sphere = ring, HpDockMountA, 50
        [Solar]
        nickname = jumpgate
        type = JUMP_GATE
        docking_sphere = jump, HpDockMountA, 50
        [Solar]
        nickname = ring
        type = TRADELANE_RING
        [Solar]
        nickname = planet1
        type = PLANET
        [Solar]
        nickname = sun1
        type = SUN
        [Solar]
        nickname = buoy
        type = SATELLITE
        [Solar]
        nickname = depot
        type = DESTROYABLE_DEPOT
        """;

    /// <summary>A workspace with the scene files, the empty mods "lower" and "act", and the profile.</summary>
    public static TestWorkspace Create(Dictionary<string, string>? lowerFiles = null,
        Dictionary<string, string>? activeFiles = null)
    {
        var ws = ListWorkspace.Create(lowerFiles, activeFiles);
        ws.WriteBase(ListWorkspace.SolarPath, Solars);
        ws.WriteBase(ListWorkspace.UniversePath, """
            [system]
            nickname = Li01
            file = systems\Li01\li01.ini
            [system]
            nickname = Li02
            file = systems\Li02\li02.ini
            [Base]
            nickname = Li01_01_Base
            system = Li01
            [Base]
            nickname = Li02_01_Base
            system = Li02
            """);
        ws.WriteBase(Li01Path, Li01Text);
        ws.WriteBase(Li02Path, Li02Text);
        ws.WriteBase(CloudPath, """
            [Fog]
            fog_enabled = 1
            [Exclusion Zones]
            exclusion = Zone_hole
            fog_far = 100
            zone_shell = solar\x.cmp
            exclusion = Zone_ring
            fog_far = 200
            exclusion = Zone_hole
            max_alpha = 0.5
            """);
        ws.WriteBase(RocksPath, """
            [Field]
            fill_dist = 1250
            [Exclusion Zones]
            exclusion = Zone_hole
            [LootableZone]
            zone = Zone_hole
            dynamic_loot_count = 1, 2
            [LootableZone]
            dynamic_loot_count = 3, 4
            """);
        ws.WriteBase(ListWorkspace.GroupsPath, $"""
            [Group]
            nickname = li_n_grp
            rep = 0.5, li_p_grp
            [Group]
            nickname = li_p_grp
            [locked_gates]
            locked_gate = {FLHash.CreateID("Li01_to_Li02")}
            npc_locked_gate = {FLHash.CreateID("Li02_to_Li01")}
            npc_locked_gate = {FLHash.CreateID("Li01_to_Li02")}
            """);
        ws.WriteBase(MissionPath, "[Trigger]\nnickname = t1\nCnd_SpaceEnter = Li01_01\nAct_Spawn = x, Li01_01, Li01_01_Base\n");
        ws.WriteBase(KnowledgeMapPath, "[KnowledgeMapTable]\nmap = 1234, Li01_01, Li01_child\n");
        return ws;
    }

    public static SectionRef Object(string nickname) => Li01("Object", nickname);

    public static SectionRef Zone(string nickname) => Li01("zone", nickname);

    public static SectionRef Li01(string type, string? key) => new(Li01Path, new SectionIdentity(type, key));

    public static SectionRef Li02(string type, string? key) => new(Li02Path, new SectionIdentity(type, key));
}
