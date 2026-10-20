using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A test profile for the mission commands (section 11.1 of the M6b design): a synthetic script with a ship build file
/// and a shared ship build file, and the active mod <c>act</c>. All names are invented.
/// </summary>
internal sealed class MissionEdits : IDisposable
{
    public const string Script = @"DATA\MISSIONS\OL_TEST\ol_test.ini";
    public const string Ships = @"DATA\MISSIONS\OL_TEST\npcships.ini";
    public const string SharedShips = @"DATA\MISSIONS\npcships.ini";

    /// <summary>The script. Trigger ol_wave has its system line first, as 116 retail triggers have.</summary>
    public const string Text = """
        [Mission]
        npc_ship_file = missions\ol_test\npcships.ini

        [NPC]
        nickname = ol_npc_1
        npc_ship_arch = ol_build_1
        affiliation = ol_faction
        voice = ol_voice

        [NPC]
        nickname = ol_npc_shared
        npc_ship_arch = ol_shared_build

        [MsnShip]
        nickname = ol_ship_1
        system = ol_system
        npc = ol_npc_1
        label = ol_wing
        position = 100, 0, -200

        [MsnShip]
        nickname = ol_ship_2
        npc = ol_npc_1
        label = ol_wing

        [MsnShip]
        nickname = ol_ship_3
        npc = ol_npc_shared

        [MsnFormation]
        nickname = ol_form
        position = 0, 0, 0
        formation = ol_shape
        ship = ol_ship_2

        [ObjList]
        nickname = ol_orders
        GotoVec = goto_cruise, 0, 0, 1000, 100

        [Trigger]
        nickname = ol_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = ol_wave
        Act_ActTrig = ol_end

        [Trigger]
        system = ol_system
        nickname = ol_wave
        Cnd_Timer = 3
        Act_SpawnShip = ol_ship_1
        Act_DeactTrig = ol_end
        Act_ActTrig = ol_end

        [Trigger]
        nickname = ol_end
        Cnd_Destroyed = ol_ship_1, -1
        Act_LockDock = Player, ol_station, lock
        Act_ChangeState = SUCCEED
        """;

    public const string ShipsText = """
        [NPCShipArch]
        nickname = ol_build_1
        ship_archetype = ol_arch
        loadout = ol_loadout
        level = d5
        """;

    public const string SharedText = """
        [NPCShipArch]
        nickname = ol_shared_build
        ship_archetype = ol_arch
        loadout = ol_loadout
        """;

    /// <summary>The names of the game data outside the script that the test data does not have as data lists.</summary>
    public static readonly Dictionary<MissionValueKind, string[]> WorldNames = new()
    {
        [MissionValueKind.System] = ["ol_system"],
        [MissionValueKind.Faction] = ["ol_faction"],
        [MissionValueKind.Voice] = ["ol_voice"],
        [MissionValueKind.SystemObject] = ["ol_station"],
        [MissionValueKind.ShipArch] = ["ol_arch"],
        [MissionValueKind.Loadout] = ["ol_loadout", "ol_loadout_2"],
        [MissionValueKind.FormationShape] = ["ol_shape"],
    };

    private MissionEdits(TestWorkspace ws, EditSession session)
    {
        Ws = ws;
        Session = session;
    }

    public TestWorkspace Ws { get; }

    public EditSession Session { get; }

    public MissionEditor Missions => Session.Missions;

    /// <summary>A profile with the script and the ship build files in the base folder.</summary>
    /// <param name="text">The text of the script, or null for <see cref="Text"/>.</param>
    /// <param name="binary">True writes the script as a binary INI file.</param>
    /// <param name="setup">More files before the session opens.</param>
    /// <param name="modFiles">The files of the active mod, by their path in the mod folder, or null for none.</param>
    public static MissionEdits Create(string? text = null, bool binary = false, Action<TestWorkspace>? setup = null,
        string? manifestExtra = null, Dictionary<string, string>? modFiles = null)
    {
        var ws = new TestWorkspace();
        if (binary)
            ws.WriteBaseBini(Script, text ?? Text);
        else
            ws.WriteBase(Script, text ?? Text);
        ws.WriteBase(Ships, ShipsText);
        ws.WriteBase(SharedShips, SharedText);
        setup?.Invoke(ws);
        ws.Mod("act", modFiles ?? new(), manifestExtra);
        ws.WriteProfile("act");
        var session = ws.Session("act");
        session.Missions.UseWorld(new TestMissionWorld(session, WorldNames));
        return new MissionEdits(ws, session);
    }

    public void Dispose() => Ws.Dispose();

    public static TriggerRef T(string name, string script = Script) => new(script, name);

    public static ItemRef Item(string type, string? key, string script = Script) => ItemRef.Of(script, type, key);

    public static RowRef Row(string trigger, int row) => new(T(trigger).Item, row);

    public static ScriptRef TestScript => new(Script);

    /// <summary>The lines of a section of W as "key = values". The test fails when W has no such section.</summary>
    public string[] Lines(ItemRef item)
    {
        var index = IndexOf(item);
        if (index < 0)
            Assert.Fail($"W has no section {item.Section} in {item.Script}.");
        return Session.OpenDocument(item.Script)!.Working[index].Select(Line).ToArray();
    }

    public string[] Lines(string trigger) => Lines(T(trigger).Item);

    /// <summary>True when W has the section.</summary>
    public bool Has(ItemRef item) => IndexOf(item) >= 0;

    public bool Has(string trigger) => Has(T(trigger).Item);

    private int IndexOf(ItemRef item)
    {
        var document = Session.OpenDocument(item.Script)!;
        var index = document.IndexOf(item.Section);
        if (index < 0 && item.Section.Type == "Trigger")
            index = MissionDraftLines.TriggerIndex(document, item.Section.Key!);
        return index;
    }

    /// <summary>A line as "key = values" with the values as the game reads them.</summary>
    public static string Line(Entry entry) =>
        entry.Count == 0 ? $"{entry.Name} =" : $"{entry.Name} = {string.Join(", ", MissionValues.TextsOf(entry))}";

    /// <summary>The engine form of W of the mission files that exist, the text of the active mod and the graph file of the
    /// script: each section as its type and its lines. Two states with the same form are engine-equal and have the same
    /// editor files.</summary>
    public string State() => string.Join("\n---\n", new[] { Script, Ships, SharedShips }
        .Concat(Session.EditedPaths.Where(p => p.StartsWith(@"DATA\MISSIONS", StringComparison.OrdinalIgnoreCase)))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
        .Where(path => Session.OpenDocument(path) is { State.Exists: true })
        .Select(path => $"{path}\n" + string.Join("\n", Session.OpenDocument(path)!.Working.Select(s =>
            $"[{s.Name}]\n" + string.Join("\n", s.Select(e => e.Name.ToLowerInvariant() + " = " +
                string.Join(", ", e.Select(EngineText)))))))
        .Append("text\n" + string.Join("\n", Session.CurrentText.Strings.Select(p => $"{p.Key} = {p.Value}")))
        .Append("graph\n" + Session.ModFileText(GraphFile.PathOf(Script))));

    /// <summary>The text of a value as the engine reads it: numbers by value.</summary>
    private static string EngineText(ValueBase value) => value switch
    {
        Int32Value or SingleValue when value.TryToSingle(out var number) =>
            ((double)number).ToString("R", CultureInfo.InvariantCulture),
        BooleanValue => value.ToBoolean() ? "true" : "false",
        _ => value.ToString() ?? "",
    };

    /// <summary>Runs a command and asserts that it applied.</summary>
    public MissionEditResult Apply(MissionCommand command, MissionEditOptions? options = null)
    {
        var result = Missions.Apply(command, options);
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => $"{e.Detail}: {e.Message}")));
        return result;
    }

    /// <summary>Runs a command and asserts that it refused with a detail code. Nothing changed.</summary>
    public EditError Refuse(MissionCommand command, string detail)
    {
        var before = State();
        var revision = Session.Revision;
        var result = Missions.Apply(command);
        Assert.Equal(EditStatus.Refused, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(detail, error.Detail);
        Assert.Equal(before, State());
        Assert.Equal(revision, Session.Revision);
        return error;
    }
}

/// <summary>A world source of a test: the names of the data lists of the session, with more names that the test data
/// does not have as data lists.</summary>
internal sealed class TestMissionWorld(EditSession session, IReadOnlyDictionary<MissionValueKind, string[]> names)
    : IMissionWorldSource
{
    private (object View, long World, MissionInputs Inputs)? last;

    public MissionInputs Inputs()
    {
        if (last is var (view, world, inputs) && ReferenceEquals(view, session.EditView) && world == session.WorldRevision)
            return inputs;
        var more = names.ToDictionary(p => p.Key, p => (IEnumerable<string>)p.Value);
        var made = session.MissionWorldInputs(FullCoverage.Instance);
        made = made with { World = made.World.WithNames(more), BaseWorld = made.BaseWorld?.WithNames(more) };
        last = (session.EditView, session.WorldRevision, made);
        return made;
    }
}

/// <summary>The trigger lookup of the draft, for the tests.</summary>
internal static class MissionDraftLines
{
    public static int TriggerIndex(EditDocument document, string name) => MissionDraft.TriggerIndex(document, name);
}
