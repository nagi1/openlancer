using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing.Lists;
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
        var baseFiles = EditView.Base.OpenFileSystem();
        var names = WorldLists.ToDictionary(l => l.Kind, l => (IEnumerable<string>)ListNames(l.List));
        names[MissionValueKind.SystemObject] = Nicknames.All.Where(n => !n.IsZone).Select(n => n.Name).ToList();
        var ini = FreelancerIni;
        var world = new MissionWorld(names, relative =>
            DataPaths.Resolve(ini, relative) is { } path && files.FileExists(path));
        var removed = RemovedNames(baseFiles, world);
        var targets = ActiveMod?.Manifest.Targets;
        return new MissionInputs
        {
            EditView = files,
            BaseView = baseFiles,
            FreelancerIni = ini,
            World = world,
            BaseWorld = removed.Count > 0 ? world.WithRemoved(removed) : null,
            Ledger = new LedgerView(EditView.Ledger),
            Coverage = coverage,
            ActiveModId = ActiveMod?.Id,
            Options = new MissionCheckOptions(targets?.Contains(ModTargets.OpenLancer) == true),
            Generation = ++missionGeneration,
        };
    }

    /// <summary>The kinds of the checks whose names come from a data list of the edit view, with the id of the
    /// list.</summary>
    private static readonly (MissionValueKind Kind, string List)[] WorldLists =
    [
        (MissionValueKind.System, "system"), (MissionValueKind.Base, "base"), (MissionValueKind.Faction, "faction"),
        (MissionValueKind.ShipArch, "ship"), (MissionValueKind.Loadout, "loadout"), (MissionValueKind.Pilot, "pilot"),
        (MissionValueKind.Voice, "voice"), (MissionValueKind.Good, "good"), (MissionValueKind.Equipment, "equipment"),
        (MissionValueKind.Head, "head"), (MissionValueKind.Body, "body"), (MissionValueKind.Accessory, "accessory"),
        (MissionValueKind.NpcShipArch, "npc_ship_arch"), (MissionValueKind.FormationShape, "formation_shape"),
        (MissionValueKind.CommLine, "comm_line"), (MissionValueKind.Fuse, "fuse"),
    ];

    /// <summary>
    /// The names that the base folder without mods (B) has and the edit view does not have, by kind, each with the last
    /// mod that changes its file (decision D551 of the M6a design). A mod removes or renames such a name, so a mission
    /// line that names it has a problem only with the mods. Only the files that the mods change can lose a name, so a
    /// profile without mods reads no file.
    /// </summary>
    private Dictionary<MissionValueKind, IReadOnlyDictionary<string, string>> RemovedNames(FileSystem baseFiles,
        MissionWorld world)
    {
        var result = new Dictionary<MissionValueKind, IReadOnlyDictionary<string, string>>();
        if (EditView.Mods.Count == 0)
            return result;
        var ledger = EditView.Ledger;
        string? ModOf(string path) => ledger.ModsThatChangeFile(VfsPath.Normalize(path) ?? path) is [.., var last] ? last : null;
        foreach (var (kind, list) in WorldLists)
        {
            if (Lists.NamesOnlyInBase(list, baseFiles, ModOf) is { Count: > 0 } only)
                result[kind] = only;
        }
        // The objects of the system files, as the nickname index reads them: the [Object] sections.
        var objects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Systems.Files)
        {
            if (ModOf(path) is not { } mod || DataLists.BaseSections(baseFiles, path) is not { } sections)
                continue;
            foreach (var section in sections.Where(s => s.Name.Equals("Object", StringComparison.OrdinalIgnoreCase)))
            {
                if (ItemIndex.First(section, "nickname") is { } nickname &&
                    !world.Has(MissionValueKind.SystemObject, nickname))
                    objects.TryAdd(nickname.Trim(), mod);
            }
        }
        if (objects.Count > 0)
            result[MissionValueKind.SystemObject] = objects;
        return result;
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
