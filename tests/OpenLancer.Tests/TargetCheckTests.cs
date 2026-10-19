using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Mods;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Data.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The content checks of a retail target (section 9.1 of the M11 design): data that the retail game does not accept or
/// does not read. Each test triggers one check, and then gives the fixed data. The checks report only what the mods
/// add.
/// </summary>
public sealed class TargetCheckTests : IDisposable
{
    private const string SystemPath = @"DATA\universe\systems\li01\li01.ini";
    private const string UniversePath = @"DATA\universe\universe.ini";
    private const string WeaponsPath = @"DATA\EQUIPMENT\weapon_equip.ini";
    private const string EnginesPath = @"DATA\EQUIPMENT\engine_equip.ini";
    private const string LoadoutsPath = @"DATA\SHIPS\loadouts.ini";
    private const string LootPath = @"DATA\MISSIONS\lootprops.ini";
    private const string NpcShipsPath = @"DATA\MISSIONS\npcships.ini";
    private const string MissionPath = @"DATA\MISSIONS\M01\m01.ini";
    private const string MissionShipsPath = @"DATA\MISSIONS\M01\npcships.ini";

    private const string Loadouts = """
        [Loadout]
        nickname = npc_lo
        equip = eng1
        equip = msl_gun

        [Loadout]
        nickname = msn_lo
        equip = eng1
        """;

    private readonly ExportGame game = new();

    public TargetCheckTests()
    {
        game.Game.Write(@"EXE\freelancer.ini", """
            [Freelancer]
            data path = ..\data

            [Data]
            universe = universe\universe.ini
            equipment = equipment\weapon_equip.ini
            equipment = equipment\engine_equip.ini
            loadouts = ships\loadouts.ini
            goods = equipment\goods.ini
            markets = equipment\market_ships.ini

            [Resources]
            DLL = InfoCards.dll ; the infocards
            """);
        game.Game.Write(SystemPath, System(9));
        game.Game.Write(WeaponsPath, """
            [Munition]
            nickname = msl_ammo
            requires_ammo = true
            force_gun_ori = false

            [Gun]
            nickname = msl_gun
            projectile_archetype = msl_ammo
            """);
        game.Game.Write(EnginesPath, "[Engine]\nnickname = eng1\nmax_force = 1000\n");
        game.Game.Write(LoadoutsPath, Loadouts);
        game.Game.Write(LootPath, """
            [mLootProps]
            nickname = msl_ammo
            drop_properties = 50, 1, 1, 1, 1, 1

            [mLootProps]
            nickname = msl_gun
            drop_properties = 10, 1, 1, 1, 1, 1
            """);
        game.Game.Write(NpcShipsPath, "[NPCShipArch]\nnickname = npc1\nloadout = npc_lo\n");
        game.Game.Write(MissionPath, """
            [Mission]
            npc_ship_file = missions\m01\npcships.ini

            [Trigger]
            nickname = t1
            Cnd_True = no_params
            Act_ActTrig = t1
            """);
        game.Game.Write(MissionShipsPath, "[NPCShipArch]\nnickname = msn_npc1\nloadout = msn_lo\n");
    }

    public void Dispose() => game.Dispose();

    /// <summary>A system file with a sun, jump gates, a zone and an asteroid field in the zone.</summary>
    private static string System(int gates, string fieldZone = "zone_li01_field") =>
        "[SystemInfo]\nname = li01\n\n[Object]\nnickname = li01_sun\narchetype = sun_1000\n\n" +
        string.Concat(Enumerable.Range(1, gates).Select(Gate)) +
        "[Zone]\nnickname = zone_li01_field\npos = 0, 0, 0\n\n" +
        $"[Asteroids]\nfile = solar\\asteroids\\li01_field.ini\nzone = {fieldZone}\n";

    private static string Gate(int number) =>
        $"[Object]\nnickname = li01_gate_{number}\narchetype = jumpgate\ngoto = li0{number}, li0{number}_to_li01, gate_tunnel\n\n";

    /// <summary>A patch section that adds a jump gate to the system.</summary>
    private static string NewGate(string nickname) =>
        $"[Object]\nol.new = true\nnickname = {nickname}\narchetype = jumpgate\ngoto = li09, li09_to_li01, gate_tunnel\n\n";

    private int mods;

    /// <summary>A new mod with patches and whole files: the path in the mod folder to the text.</summary>
    private Mod Mod(Dictionary<string, string> files) => game.Mod($"m{++mods}", files);

    /// <summary>A new mod with one patch of the goods.</summary>
    private Mod GoodsMod(string patch) => game.GoodsMod(patch, $"m{++mods}");

    private IReadOnlyList<Diagnostic> Check(Mod mod)
    {
        var files = game.Build(mod);
        Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return TargetChecks.Run(files);
    }

    private IReadOnlyList<Diagnostic> CheckFiles(Dictionary<string, string> files) => Check(Mod(files));

    private static string Patch(string path) => @"patches\" + path;

    private static string Whole(string path) => @"files\" + path;

    [Fact]
    public void AnEleventhJumpGateIsAnError()
    {
        var problem = Assert.Single(CheckFiles(new() { [Patch(SystemPath)] = NewGate("li01_new_1") + NewGate("li01_new_2") }));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("System 'li01' has 11 jump holes and jump gates. The game crashes with more than 10 in one system. " +
                     "Remove 1 of them.", problem.Message);
        Assert.Equal(SystemPath, problem.Source, ignoreCase: true);
        // Ten gates are allowed.
        Assert.Empty(CheckFiles(new() { [Patch(SystemPath)] = NewGate("li01_new_1") }));
    }

    [Fact]
    public void AGoodWithThePriceZeroIsAnError()
    {
        var problem = Assert.Single(Check(GoodsMod("[Good]\nnickname = g2\nprice = 0\n")));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("Good 'g2': the price is 0. The game can crash when a player buys or sells the good. Write a price " +
                     "above 0.", problem.Message);
        Assert.Equal(new SectionRef(ExportGame.GoodsPath, new SectionIdentity("Good", "g2")), problem.Target);
        Assert.Equal("price", problem.Key);
        Assert.Single(Check(GoodsMod("[Good]\nnickname = g2\nprice = abc\n")));
        // A ship package has no price line.
        Assert.Empty(Check(GoodsMod("[Good]\nol.new = true\nnickname = p1\ncategory = ship\nhull = lf_hull\n")));
        Assert.Empty(Check(GoodsMod("[Good]\nnickname = g2\nprice = 1\n")));
    }

    [Fact]
    public void ASystemWithoutSolarObjectsIsAnError()
    {
        const string newSystem = "[system]\nol.new = true\nnickname = li02\nfile = systems\\li02\\li02.ini\n";
        var problem = Assert.Single(CheckFiles(new()
        {
            [Patch(UniversePath)] = newSystem,
            [Whole(@"DATA\universe\systems\li02\li02.ini")] = "[SystemInfo]\nname = li02\n",
        }));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("System 'li02' has no solar objects. The game can crash when it loads the system. Add an object, for " +
                     "example a sun or a planet.", problem.Message);
        Assert.Empty(CheckFiles(new()
        {
            [Patch(UniversePath)] = newSystem,
            [Whole(@"DATA\universe\systems\li02\li02.ini")] = "[SystemInfo]\nname = li02\n\n[Object]\nnickname = li02_sun\n",
        }));
    }

    [Fact]
    public void AMunitionThatNeedsAmmoNeedsLootPropsForTheAmmoAndTheGun()
    {
        const string weapons = "[Munition]\nol.new = true\nnickname = torp_ammo\nrequires_ammo = true\n\n" +
                               "[Gun]\nol.new = true\nnickname = torp_gun\nprojectile_archetype = torp_ammo\n";

        var problems = CheckFiles(new() { [Patch(WeaponsPath)] = weapons });

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal(DiagnosticSeverity.Error, p.Severity));
        Assert.Equal("Munition 'torp_ammo' has requires_ammo = true, and lootprops.ini has no [mLootProps] for " +
                     "'torp_ammo'. The game can crash. Add an [mLootProps] section with nickname = torp_ammo to " +
                     "lootprops.ini.", problems[0].Message);
        Assert.EndsWith("no [mLootProps] for 'torp_gun'. The game can crash. Add an [mLootProps] section with " +
                        "nickname = torp_gun to lootprops.ini.", problems[1].Message);
        Assert.Empty(CheckFiles(new()
        {
            [Patch(WeaponsPath)] = weapons,
            [Patch(LootPath)] = "[mLootProps]\nol.new = true\nnickname = torp_ammo\ndrop_properties = 50, 1, 1, 1, 1, 1\n\n" +
                                "[mLootProps]\nol.new = true\nnickname = torp_gun\ndrop_properties = 50, 1, 1, 1, 1, 1\n",
        }));
        // A munition without requires_ammo needs no entry.
        Assert.Empty(CheckFiles(new() { [Patch(WeaponsPath)] = weapons.Replace("requires_ammo = true", "requires_ammo = false") }));
    }

    [Fact]
    public void AMissionTriggerWithoutAConditionIsAnError()
    {
        var problem = Assert.Single(CheckFiles(new()
        {
            [Patch(MissionPath)] = "[Trigger]\nol.new = true\nnickname = t2\nAct_ActTrig = t1\n",
        }));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        // The mission check M01 gives the same text (decision D556 of the M6a design).
        Assert.Equal("Trigger 't2' has no condition. The game stops when this trigger starts. Add one condition, for " +
                     "example Cnd_True = no_params.", problem.Message);
        Assert.Empty(CheckFiles(new()
        {
            [Patch(MissionPath)] = "[Trigger]\nol.new = true\nnickname = t2\nCnd_True = no_params\nAct_ActTrig = t1\n",
        }));
    }

    [Fact]
    public void AConditionBeforeTheNicknameDoesNotCount()
    {
        // The retail game ignores each line before the nickname (TR2), so the trigger has no condition.
        var problem = Assert.Single(CheckFiles(new()
        {
            [Patch(MissionPath)] = "[Trigger]\nol.new = true\nCnd_True = no_params\nnickname = t2\nAct_ActTrig = t1\n",
        }));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal(TriggerRules.NoConditionMessage("t2"), problem.Message);
    }

    [Fact]
    public void AnAsteroidFieldWithoutAZoneOfItsSystemIsAnError()
    {
        var problem = Assert.Single(CheckFiles(new() { [Whole(SystemPath)] = System(9, "zone_missing") }));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("System 'li01': an [Asteroids] section names the zone 'zone_missing', and the system has no such " +
                     "zone. The game crashes when it loads the system. Pick a zone of the system.", problem.Message);
        Assert.Equal("zone", problem.Key);
        Assert.Empty(CheckFiles(new() { [Whole(SystemPath)] = System(9) + "\n[Object]\nnickname = li01_planet\n" }));
    }

    [Fact]
    public void AnNpcLoadoutWithoutAnEngineIsAnError()
    {
        // A new NPC ship whose loadout has no engine.
        var problem = Assert.Single(CheckFiles(new()
        {
            [Patch(LoadoutsPath)] = "[Loadout]\nol.new = true\nnickname = npc_lo2\nequip = msl_gun\n",
            [Patch(NpcShipsPath)] = "[NPCShipArch]\nol.new = true\nnickname = npc2\nloadout = npc_lo2\n",
        }));
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("NPC ship 'npc2': its loadout 'npc_lo2' has no engine. The game crashes when the ship tries to go " +
                     "into cruise. Add an engine to the loadout.", problem.Message);

        // A loadout without its engine, which an NPC ship of a mission file uses.
        var mission = Assert.Single(CheckFiles(new()
        {
            [Whole(LoadoutsPath)] = Loadouts.Replace("nickname = msn_lo\nequip = eng1", "nickname = msn_lo\nequip = msl_gun"),
        }));
        Assert.StartsWith("NPC ship 'msn_npc1': its loadout 'msn_lo' has no engine.", mission.Message);
        Assert.Equal(MissionShipsPath, mission.Source, ignoreCase: true);

        Assert.Empty(CheckFiles(new()
        {
            [Patch(LoadoutsPath)] = "[Loadout]\nol.new = true\nnickname = npc_lo2\nequip = eng1\nequip = msl_gun\n",
            [Patch(NpcShipsPath)] = "[NPCShipArch]\nol.new = true\nnickname = npc2\nloadout = npc_lo2\n",
        }));
    }

    [Fact]
    public void AKeyThatOnlyLibrelancerReadsIsAnError()
    {
        var problems = CheckFiles(new()
        {
            [Patch(@"EXE\freelancer.ini")] = "[Data]\nol.append = true\nnavmap = interface\\navmap.ini\n",
            [Whole(@"DATA\interface\navmap.ini")] = "[Background]\ntexture = x\n",
            [Patch(EnginesPath)] = "[Engine]\nnickname = eng1\ncruise_speed = 400\n",
        });

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal(DiagnosticSeverity.Error, p.Severity));
        Assert.Contains(problems, p => p.Message == @"[Data] navmap = interface\navmap.ini: the game does not read this " +
                                                    "key. Only OpenLancer reads it.");
        Assert.Contains(problems, p => p.Message == "[Engine] nickname = eng1 cruise_speed: the game does not read this " +
                                                    "key. Only OpenLancer reads it.");

        var extended = Assert.Single(CheckFiles(new()
        {
            [Whole(@"EXE\freelancer.ini")] = File.ReadAllText(game.Game.PathOf(@"EXE\freelancer.ini")) +
                                             "\n[Extended]\ndataversion = 2\n",
        }));
        Assert.Equal("[Extended]: the game does not read this section. Only OpenLancer reads it.", extended.Message);
        // A [Data] key that the retail game reads gives no finding.
        Assert.Empty(CheckFiles(new()
        {
            [Patch(@"EXE\freelancer.ini")] = "[Data]\nol.append = true\nequipment = equipment\\new_equip.ini\n",
            [Whole(@"DATA\EQUIPMENT\new_equip.ini")] = "[Engine]\nnickname = eng2\n",
        }));
    }

    [Fact]
    public void ABooleanWithAnotherValueIsAWarningOrACrashError()
    {
        var warning = Assert.Single(Check(GoodsMod("[Good]\nnickname = g2\ncombinable = yes\n")));

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        // The mission check M14 gives the same text after the place (decision D556 of the M6a design).
        Assert.Equal("[Good] nickname = g2 combinable: 'yes' is not true or false. The game reads only true, 1, false " +
                     "and 0. For another value, it uses false. Pick true or false.", warning.Message);

        var crash = Assert.Single(CheckFiles(new() { [Patch(WeaponsPath)] = "[Munition]\nnickname = msl_ammo\nforce_gun_ori = no\n" }));
        Assert.Equal(DiagnosticSeverity.Error, crash.Severity);
        Assert.EndsWith("force_gun_ori = no: the game reads only true, false, 1 and 0 here. The game can crash with " +
                        "another value.", crash.Message);

        Assert.Empty(Check(GoodsMod("[Good]\nnickname = g2\ncombinable = TRUE\n")));
        Assert.Empty(Check(GoodsMod("[Good]\nnickname = g2\ncombinable = 0\n")));

        // Retail reads this key as a lowercase word. The retail encounter files write yes.
        const string encounter = @"DATA\MISSIONS\ENCOUNTERS\test.ini";
        Assert.Empty(CheckFiles(new() { [Whole(encounter)] = "[EncounterFormation]\nallow_simultaneous_creation = yes\n" }));
        var word = Assert.Single(CheckFiles(new() { [Whole(encounter)] = "[EncounterFormation]\nallow_simultaneous_creation = 1\n" }));
        Assert.Equal(DiagnosticSeverity.Warning, word.Severity);
        Assert.EndsWith("allow_simultaneous_creation = 1: the game reads only yes, true, no and false here, in lowercase. It " +
                        "keeps the default value.", word.Message);
    }

    [Fact]
    public void AProblemOfTheGameIsNotReported()
    {
        // The game already has 11 gates and a good with the price 0. A mod that changes other lines adds no problem.
        game.Game.Write(SystemPath, System(11));
        game.Game.Write(ExportGame.GoodsPath, ExportGame.Goods + "\n\n[Good]\nnickname = g3\nprice = 0\n");

        Assert.Empty(CheckFiles(new()
        {
            [Patch(SystemPath)] = "[Object]\nnickname = li01_sun\narchetype = sun_2000\n",
            [Patch(ExportGame.GoodsPath)] = "[Good]\nnickname = g3\nids_name = 5\n",
        }));
    }

    [Fact]
    public void TheFixedDataGivesNoFinding()
    {
        var mod = Mod(new()
        {
            [Patch(SystemPath)] = NewGate("li01_new_1"),
            [Patch(ExportGame.GoodsPath)] = "[Good]\nnickname = g2\nprice = 300\ncombinable = true\n",
            [Patch(WeaponsPath)] = "[Munition]\nnickname = msl_ammo\nforce_gun_ori = true\n",
            [Patch(LoadoutsPath)] = "[Loadout]\nol.new = true\nnickname = npc_lo2\nequip = eng1\n",
            [Patch(NpcShipsPath)] = "[NPCShipArch]\nol.new = true\nnickname = npc2\nloadout = npc_lo2\n",
            [Patch(MissionPath)] = "[Trigger]\nol.new = true\nnickname = t2\nCnd_True = no_params\n",
        });

        var files = game.Build(mod);

        Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(TargetChecks.Run(files));
        Assert.True(TargetChecks.ChangesGameplay(files));
    }

    [Fact]
    public void AContentErrorStopsTheExportAndPlay()
    {
        var mod = GoodsMod("[Good]\nnickname = g2\nprice = 0\n");

        var export = ModExport.Run(game.Request(mod));
        var play = PlayBuild.Run(game.Report(), [mod], TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

        Assert.False(export.Succeeded);
        Assert.Contains(export.Problems, p => p.Message.StartsWith("Good 'g2': the price is 0."));
        Assert.Contains(export.Problems, p => p.Message == ModExport.Stopped);
        Assert.True(play.HasErrors);
        Assert.Contains(play.Problems, p => p.Message.StartsWith("Good 'g2': the price is 0."));
    }
}

/// <summary>The check of the lines that HD Edition changed (section 9.1 of the M11 design).</summary>
public sealed class HdLineCheckTests
{
    [Fact]
    public void ALineThatHdEditionChangedGivesAWarning()
    {
        using var hd = new ExportGame(hd: true);
        using var retail = new ExportGame();
        // HD Edition has another price of lf_hull than the retail game. The price of g2 is the same.
        retail.Game.Write(ExportGame.GoodsPath, ExportGame.Goods.Replace("price = 6600", "price = 7000"));
        var mod = hd.GoodsMod("[Good]\nnickname = lf_hull\nprice = 5600\n\n[Good]\nnickname = g2\nprice = 250\n");

        var files = hd.Build(mod);
        var problem = Assert.Single(TargetChecks.HdEditionLines(files, retail.Base));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal("The mod changes 1 line that HD Edition changed: [Good] nickname = lf_hull price. Make sure that " +
                     "the mod keeps the fixes of HD Edition.", problem.Message);
        Assert.Equal(ExportGame.GoodsPath, problem.Source);
        Assert.Empty(TargetChecks.HdEditionLines(files, hd.Base));
    }

    [Fact]
    public void TheHdExportListsTheLinesWhenAFreelancerGameIsLinked()
    {
        using var hd = new ExportGame(hd: true);
        using var retail = new ExportGame();
        retail.Game.Write(ExportGame.GoodsPath, ExportGame.Goods.Replace("price = 6600", "price = 7000"));
        var mod = hd.GoodsMod("[Good]\nnickname = lf_hull\nprice = 5600\n");
        var hdReport = hd.Report();
        var retailReport = retail.Report();
        var request = hd.Request(mod) with
        {
            BaseOf = t => t == ExportTarget.HdEdition ? hdReport : t == ExportTarget.Vanilla ? retailReport : null,
        };

        var result = ModExport.Run(request);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Warning &&
                                              p.Message.Contains("that HD Edition changed: [Good] nickname = lf_hull price."));
        // Without a linked Freelancer game, the export cannot tell the lines of HD Edition.
        var alone = ModExport.Run(hd.Request(mod) with
        {
            BaseOf = t => t == ExportTarget.HdEdition ? hdReport : null,
            OutputFolder = Path.Combine(hd.Game.Folder, "exports", "alone"),
        });
        Assert.True(alone.Succeeded, string.Join("\n", alone.Problems));
        Assert.DoesNotContain(alone.Problems, p => p.Message.Contains("HD Edition changed"));
    }
}
