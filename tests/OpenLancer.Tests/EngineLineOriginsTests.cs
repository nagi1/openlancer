using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The origin of the engine lines of a load (sections 3.3 to 3.5 of the console filter design): a comparison with the
/// base run, and the section rule that names the mod. The base folder and the mods are synthetic. The base counts come
/// from synthetic engine lines, because the engine load needs a full game folder. The retail tests check the base run
/// itself.
/// </summary>
public sealed class EngineLineOriginsTests
{
    private const string Li01 = @"EXE\..\data\universe\systems\li01\li01.ini";
    private const string Pilots = @"DATA\MISSIONS\pilots_population.ini";

    private const string PilotsText = """
        [GunBlock]
        nickname = guns_a
        [GunBlock]
        nickname = guns_b
        [Pilot]
        nickname = pilot_a
        gun_id = guns_a
        """;

    /// <summary>The load lines of a profile without a mod file are base-game lines at once. No base run is
    /// needed.</summary>
    [Fact]
    public void AProfileWithoutModFilesGivesTheBaseGameForEachLine()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        var origins = new EngineLineOrigins(session);

        Assert.False(origins.NeedsBaseRun);
        Assert.True(origins.CanResolve);
        Assert.All(origins.Resolve([Duplicate(3), Duplicate(9), Ship("ship1")]),
            r => Assert.Equal(MessageOrigin.BaseGame, r.Origin));
    }

    /// <summary>A line that the base run gives as often is a base-game line, also in a section that a mod changes.</summary>
    [Fact]
    public void ALineThatTheBaseRunAlsoGivesIsBaseGameAlsoInAChangedSection()
    {
        using var ws = ListWorkspace.Create(activeFiles: new() { [Patch(ListWorkspace.Li01Path)] = ChangeLi01 });
        var session = ws.Session("act");
        var origins = Origins(session, Duplicate(1));
        var line = Duplicate(LineOf(session, ListWorkspace.Li01Path, "Li01_01"));

        Assert.True(origins.NeedsBaseRun);
        Assert.Equal(MessageOrigin.BaseGame, Assert.Single(origins.Resolve([line])).Origin);
    }

    /// <summary>
    /// Review finding CF1: a mod deletes a gun block, and an unchanged pilot names it. The base run does not give the line
    /// of the pilot, so it shows with the mod of the file. A click selects the pilot.
    /// </summary>
    [Fact]
    public void ALineAboutAnUnchangedSectionThatAModBreaksShowsWithTheMod()
    {
        using var ws = ListWorkspace.Create(activeFiles: new()
        {
            [Patch(Pilots)] = "[GunBlock]\nnickname = guns_a\nol.delete = true\n",
        });
        ws.WriteBase(Pilots, PilotsText);
        var session = ws.Session("act");
        var origins = Origins(session);
        var line = EngineLine.Parse("[Warning: 12:00:00] Pilot: pilot_a: Unable to find GunBlock 'guns_a'",
            LogSeverity.Warning);

        var resolved = Assert.Single(origins.Resolve([line]));

        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.File), resolved.Origin);
        Assert.Equal(new SectionRef(Pilots, new SectionIdentity("Pilot", "pilot_a")), resolved.Target);
    }

    /// <summary>
    /// Three lines of one key, and the base run gives two: the line of the changed section shows, with the mod of the
    /// section, and the two others are base-game lines. The line of the changed section comes first, so the sort and
    /// not the arrival order decides.
    /// </summary>
    [Fact]
    public void TheLineOfTheChangedSectionShowsWhenTheBaseRunGivesFewerLines()
    {
        using var ws = ListWorkspace.Create(activeFiles: new() { [Patch(ListWorkspace.Li01Path)] = ChangeLi01 });
        var session = ws.Session("act");
        var origins = Origins(session, Duplicate(1), Duplicate(2));
        var changed = Duplicate(LineOf(session, ListWorkspace.Li01Path, "Li01_01"));
        var lines = new[]
        {
            changed,
            Duplicate(LineOf(session, ListWorkspace.Li01Path, "ring1")),
            Duplicate(LineOf(session, ListWorkspace.Li01Path, "ring2")),
        };

        var resolved = origins.Resolve(lines);

        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.Section), resolved[0].Origin);
        Assert.Equal(ListWorkspace.Li01("Li01_01"), resolved[0].Target);
        Assert.Equal([MessageOrigin.BaseGame, MessageOrigin.BaseGame], resolved.Skip(1).Select(r => r.Origin));
    }

    /// <summary>A line of a base file that no mod changes, for example a file that only a mod makes the game load,
    /// shows when the base run does not give it. The only mod that changes files gets it.</summary>
    [Fact]
    public void ALineOfABaseFileThatOnlyAModLoadsShowsWithTheOnlyMod()
    {
        using var ws = ListWorkspace.Create(activeFiles: new() { [Patch(ListWorkspace.Li01Path)] = ChangeLi01 });
        var session = ws.Session("act");
        var origins = Origins(session);
        var line = EngineLine.Parse(
            @"[Warning: 12:00:00] Ini: Unknown entry x at section Solar: EXE\..\data\solar\solararch.ini, line 2",
            LogSeverity.Warning);

        var resolved = Assert.Single(origins.Resolve([line]));

        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.OnlyMod), resolved.Origin);
        Assert.Equal(new SectionRef(ListWorkspace.SolarPath, new SectionIdentity("Solar", "station1")), resolved.Target);
    }

    /// <summary>Without a mod of the section, of the file or a single mod with files, the origin is "a mod". A lower mod
    /// that changes the section gets the line.</summary>
    [Fact]
    public void WithTwoModsAndNoModOfTheFileTheOriginIsAMod()
    {
        using var ws = ListWorkspace.Create(lowerFiles: new() { [Patch(ListWorkspace.Li02Path)] = ChangeLi02 },
            activeFiles: new() { [Patch(ListWorkspace.Li01Path)] = ChangeLi01 });
        var session = ws.Session("act");
        var origins = Origins(session);
        var line = EngineLine.Parse("[Warning: 12:00:00] Game: Something new", LogSeverity.Warning);

        Assert.Equal(MessageOrigin.SomeMod, Assert.Single(origins.Resolve([line])).Origin);
        Assert.Equal(MessageOrigin.Mod("lower", ModEvidence.Section), Assert.Single(origins.Resolve(
            [Duplicate(LineOf(session, ListWorkspace.Li02Path, "Li02_to_Li01"), Li02File)])).Origin);
    }

    /// <summary>A ship line maps to its <c>[Ship]</c> section through the item index.</summary>
    [Fact]
    public void AShipLineMapsToItsShipSection()
    {
        using var ws = ListWorkspace.Create(activeFiles: new() { [Patch(ListWorkspace.ShipsPath)] =
            "[Ship]\nnickname = ship1\nhit_pts = 5\n" });
        var session = ws.Session("act");

        var resolved = Assert.Single(Origins(session).Resolve([Ship("ship1")]));

        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.Section), resolved.Origin);
        Assert.Equal(new SectionRef(ListWorkspace.ShipsPath, new SectionIdentity("Ship", "ship1")), resolved.Target);
    }

    /// <summary>A failed base run gives the origin unknown (decision D517). Until the base run ends, no line can
    /// resolve.</summary>
    [Fact]
    public void AFailedBaseRunGivesTheOriginUnknown()
    {
        using var ws = ListWorkspace.Create(activeFiles: new() { [Patch(ListWorkspace.Li01Path)] = ChangeLi01 });
        var session = ws.Session("act");
        var origins = new EngineLineOrigins(session);

        Assert.False(origins.CanResolve);
        origins.BaseRunFailed("oltool did not start.");

        Assert.True(origins.CanResolve);
        Assert.Equal(MessageOrigin.Unknown, Assert.Single(origins.Resolve([Duplicate(1)])).Origin);
        Assert.Equal(MessageOrigin.Unknown, origins.ResolveRunning(Duplicate(1), 1).Origin);
    }

    /// <summary>An engine line about a setup trigger of the test layer of a mission play is about the work of OpenLancer
    /// (section 6.5 of the M6c design): it names a setup trigger as a whole word. A longer name with the same start is
    /// another name.</summary>
    [Fact]
    public void AnEngineLineThatNamesASetupTriggerIsInThePlayLayer()
    {
        var setup = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ol_play_setup", "ol_play_world" };

        Assert.True(EngineLineOrigins.InPlayLayer(Missions("Trigger OL_PLAY_WORLD: no ship ol_wave2"), setup));
        Assert.True(EngineLineOrigins.InPlayLayer(Missions("Act_SpawnShip of 'ol_play_setup' failed"), setup));
        Assert.False(EngineLineOrigins.InPlayLayer(Missions("Trigger ol_play_world_2: no ship ol_wave2"), setup));
        Assert.False(EngineLineOrigins.InPlayLayer(Missions("Trigger t_wave: no ship ol_wave2"), setup));
        Assert.False(EngineLineOrigins.InPlayLayer(Missions("Trigger ol_play_world: no ship"), new HashSet<string>()));
    }

    /// <summary>The Console gives a line of a Play run about a setup trigger the origin OpenLancer and the suffix "(play
    /// start)". Another line of the run keeps its own origin and text.</summary>
    [Fact]
    public void TheConsoleGivesALineOfThePlayLayerTheOriginOpenLancer()
    {
        var log = new OpenLancer.Editor.Logs.ConsoleLog();
        var run = log.BeginGameRun();
        run.Layer = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ol_play_world" };

        log.AddGameLine(run, Missions("Trigger ol_play_world: no ship ol_wave2"), "Play");
        log.AddGameLine(run, Missions("Trigger t_wave: no ship ol_wave2"), "Play");

        Assert.Equal(["Missions: Trigger ol_play_world: no ship ol_wave2 (play start)",
            "Missions: Trigger t_wave: no ship ol_wave2"], log.Entries.Select(e => e.Message));
        Assert.Equal(MessageOrigin.OpenLancer, log.Entries[0].Origin);
    }

    private static EngineLine Missions(string message) =>
        EngineLine.Parse($"[Warning: 12:00:00] Missions: {message}")!;

    /// <summary>A line of a Play run resolves when it arrives: the n-th line of a key is a base-game line when the base
    /// run gives it n times or more.</summary>
    [Fact]
    public void ALineOfAPlayRunResolvesByItsCount()
    {
        using var ws = ListWorkspace.Create(activeFiles: new() { [Patch(ListWorkspace.Li01Path)] = ChangeLi01 });
        var session = ws.Session("act");
        var origins = Origins(session, Duplicate(1));

        Assert.Equal(MessageOrigin.BaseGame, origins.ResolveRunning(Duplicate(4), 1).Origin);
        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.Section),
            origins.ResolveRunning(Duplicate(LineOf(session, ListWorkspace.Li01Path, "Li01_01")), 2).Origin);
    }

    private const string ChangeLi01 = "[Object]\nnickname = Li01_01\nloadout = lo_shared\n";
    private const string ChangeLi02 = "[Object]\nnickname = Li02_to_Li01\ncomment = changed\n";
    private const string Li02File = @"EXE\..\data\universe\systems\li02\li02.ini";

    private static string Patch(string installPath) => @"patches\" + installPath;

    /// <summary>A resolver whose base run gave the lines.</summary>
    private static EngineLineOrigins Origins(EditSession session, params EngineLine[] baseLines)
    {
        var origins = new EngineLineOrigins(session);
        origins.BaseRunEnded(baseLines.CountBy(l => l.Key).ToDictionary(c => c.Key, c => c.Value));
        return origins;
    }

    private static EngineLine Duplicate(int line, string file = Li01) =>
        EngineLine.Parse($"[Warning: 12:00:00] Ini: Duplicate of reputation at section Object: {file}, line {line}",
            LogSeverity.Warning);

    private static EngineLine Ship(string nickname) =>
        EngineLine.Parse($"[Error: 12:00:00] Ship: Unrecognised hp_type hp_x in {nickname}", LogSeverity.Error);

    /// <summary>The line of the nickname of an object in the merged file of the edit view, from 1.</summary>
    private static int LineOf(EditSession session, string path, string nickname)
    {
        var text = Encoding.ASCII.GetString(session.EditView.CreateFileSystem().ReadAllBytes(path));
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim().Equals("nickname", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Trim().Equals(nickname, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        throw new InvalidOperationException($"{path} has no nickname {nickname}.");
    }
}
