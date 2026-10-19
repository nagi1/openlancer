using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    // The scripts and the files of the NPC ship builds of the last run of the mission checks, or null before the first
    // run. The roles 'mission' and 'mission_ships' read them.
    private IReadOnlyList<string>? missionScripts;
    private IReadOnlyList<string>? missionShipFiles;
    private int missionGeneration;

    /// <summary>The mission scripts of the edit view for the role <c>mission</c>: the scripts of the last run of the
    /// mission checks, or the slot scripts before the first run (section 14.1 of the M6a design).</summary>
    internal IReadOnlyList<string> MissionScriptPaths => missionScripts ??= SlotScriptPaths();

    /// <summary>The files of the NPC ship builds for the role <c>mission_ships</c>: the files of the last run, or the
    /// files of the folder rules before the first run.</summary>
    internal IReadOnlyList<string> MissionShipPaths => missionShipFiles ??= NpcShipFiles.Find(
        relative => DataPaths.Resolve(FreelancerIni, relative), EditViewFolders, []);

    /// <summary>
    /// Makes the snapshot of a run of the mission checks (section 12.3 of the M6a design). Call it on the UI thread: it
    /// reads the data lists. The run gets a new generation, so the result of an older run is dropped.
    /// </summary>
    /// <param name="coverage">The OpenLancer marks of the engine.</param>
    public MissionInputs MissionInputs(IEngineCoverage coverage)
    {
        var files = EditView.CreateFileSystem();
        var names = new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ListNames("system"),
            [MissionValueKind.Base] = ListNames("base"),
            [MissionValueKind.SystemObject] = Nicknames.All.Where(n => !n.IsZone).Select(n => n.Name).ToList(),
            [MissionValueKind.Faction] = ListNames("faction"),
            [MissionValueKind.ShipArch] = ListNames("ship"),
            [MissionValueKind.Loadout] = ListNames("loadout"),
            [MissionValueKind.Pilot] = ListNames("pilot"),
            [MissionValueKind.Voice] = ListNames("voice"),
            [MissionValueKind.Good] = ListNames("good"),
            [MissionValueKind.Equipment] = ListNames("equipment"),
            [MissionValueKind.Head] = ListNames("head"),
            [MissionValueKind.Body] = ListNames("body"),
            [MissionValueKind.Accessory] = ListNames("accessory"),
            [MissionValueKind.NpcShipArch] = ListNames("npc_ship_arch"),
            [MissionValueKind.FormationShape] = ListNames("formation_shape"),
            [MissionValueKind.CommLine] = ListNames("comm_line"),
            [MissionValueKind.Fuse] = ListNames("fuse"),
        };
        var ini = FreelancerIni;
        var world = new MissionWorld(names, relative =>
            DataPaths.Resolve(ini, relative) is { } path && files.FileExists(path));
        var targets = ActiveMod?.Manifest.Targets;
        return new MissionInputs
        {
            EditView = files,
            BaseView = EditView.Base.OpenFileSystem(),
            FreelancerIni = ini,
            World = world,
            Ledger = new LedgerView(EditView.Ledger),
            Coverage = coverage,
            ActiveModId = ActiveMod?.Id,
            Options = new MissionCheckOptions(targets?.Contains(ModTargets.OpenLancer) == true),
            Generation = ++missionGeneration,
        };
    }

    /// <summary>The install path of a path relative to the data folder of the edit view, or null.</summary>
    public string? DataPath(string relative) => DataPaths.Resolve(FreelancerIni, relative);

    /// <summary>True when the active mod has a <c>targets</c> list without OpenLancer. The editor then hides the
    /// OpenLancer marks (AGENTS.md UI rule 4).</summary>
    public bool HidesOpenLancer => ActiveMod?.Manifest.Targets is { } targets && !targets.Contains(ModTargets.OpenLancer);

    /// <summary>Takes the result of a run of the mission checks: its scripts and files of NPC ship builds feed the
    /// roles. Returns false and takes nothing when a newer run started after the run of the result.</summary>
    public bool TakeMissionRun(MissionRunResult result)
    {
        if (result.Generation != missionGeneration)
            return false;
        missionScripts = result.Scripts.Where(s => s.State != MissionScriptState.Missing).Select(s => s.Path).ToList();
        missionShipFiles = result.ShipFiles;
        return true;
    }

    /// <summary>The install paths of the slot scripts that the edit view has.</summary>
    private IReadOnlyList<string> SlotScriptPaths()
    {
        var files = EditView.CreateFileSystem();
        return MissionSchema.Retail.StorySlots.Select(s => MissionFiles.SlotPath(FreelancerIni, s))
            .OfType<string>().Where(files.FileExists).ToList();
    }

    private IReadOnlyList<string> EditViewFolders(string path) => EditView.CreateFileSystem().GetDirectories(path);

    /// <summary>The values of a list of the edit view, or none when the list does not exist.</summary>
    private List<string> ListNames(string id) => Lists.List(id)?.Entries.Select(e => e.Value).ToList() ?? [];

    /// <summary>The ledger of the edit view for the worker of the mission checks. The ledger of an edit view does not
    /// change, so the worker can read it.</summary>
    private sealed class LedgerView(PatchLedger ledger) : IMissionLedger
    {
        public IReadOnlyList<string> ModsThatChangeFile(string path) =>
            ledger.ModsThatChangeFile(VfsPath.Normalize(path) ?? path);

        public IReadOnlyList<string> ModsThatChangeSection(string path, SectionIdentity section) =>
            ledger.ModsThatChangeSection(VfsPath.Normalize(path) ?? path, section);

        public string? ReplacedBy(string path) => ledger.ReplacedBy(VfsPath.Normalize(path) ?? path);
    }
}
