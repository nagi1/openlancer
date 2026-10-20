using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;
using OpenLancer.Runtime.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The mission scripts of the open profile and the open mission (section 4 of the M6a design): the last run of the
/// mission checks, the open mission, the selection of the Mission tab, and the requests of the raw view and of the
/// Mission page. One mission is open at a time. The edits of mission files go through the mission editor of the session
/// (section 4 of the M6b design).
/// </summary>
/// <remarks>
/// <para>A run of the checks starts after each load of the game data. The UI thread makes the snapshot, a worker task runs
/// the checks, and the UI thread takes the result. A run that a newer run replaced drops its result and its error
/// (section 12.3 of the M6a design). The mission editor takes the analyses of the run into its model cache.</para>
/// <para>After each change of a script, also an Undo or a Redo, the result takes the model of the script from the
/// mission editor, and the Console gets the problems again (section 6.11 of the M6b design). A new or deleted script, and
/// a change of a ship build file, start a new run.</para>
/// <para>The raw text of a script needs a parse of each section for the round trip check. A worker task makes it at the
/// first use. So the open of a mission does not stop the UI thread.</para>
/// </remarks>
internal sealed class MissionWorkspace(EditorWindow window)
{
    /// <summary>The id of the Mission tab.</summary>
    public const string PanelId = "mission";

    /// <summary>The id of the Missions tab.</summary>
    public const string BrowserId = "missions";

    private static readonly Lazy<IniVocabulary> MissionVocabulary = new(() => new IniVocabulary(
        MissionSchema.Retail.Conditions.Concat(MissionSchema.Retail.Actions).Select(n => n.Name).ToList(),
        MissionSchema.Retail.Sections.SelectMany(s => s.Keys).Select(k => k.Key)
            .Concat(MissionSchema.Retail.Directives.Select(d => d.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

    private readonly Dictionary<string, Task<RawText>> rawTexts = new(VfsPath.Comparer);
    private readonly Dictionary<string, (RawText Text, IReadOnlyList<RawMark> Marks)> marks = new(VfsPath.Comparer);
    private readonly Dictionary<string, MissionRawDraft> drafts = new(StringComparer.OrdinalIgnoreCase);
    private bool marksShowBaseGame;
    private int latestRun;

    // The scripts that changed while the last run of the checks ran. Its result is older for them.
    private readonly HashSet<string> changedDuringRun = new(VfsPath.Comparer);

    // The edit session whose changes the workspace follows.
    private EditSession? followed;

    /// <summary>The names that the raw INI editors of mission scripts color: the conditions and actions, and the keys and
    /// orders of the section types.</summary>
    public static IniVocabulary Vocabulary => MissionVocabulary.Value;

    /// <summary>The result of the last run of the mission checks, or null before the first run.</summary>
    public MissionRunResult? Result { get; private set; }

    /// <summary>The install path of the open mission, or null.</summary>
    public string? OpenPath { get; private set; }

    /// <summary>The analysis of the open mission, or null.</summary>
    public MissionAnalysis? Open => OpenPath != null ? Result?.Of(OpenPath) : null;

    /// <summary>The selection of the open mission when nothing in it is selected, or null.</summary>
    public MissionRef? Root => OpenPath != null ? MissionRef.Of(OpenPath) : null;

    /// <summary>A line of a script that the raw view goes to when its text shows, or null. It is a section and the index
    /// of a line, or -1 for the header.</summary>
    public (SectionRef Target, int Entry)? PendingLine { get; set; }

    /// <summary>The key of a section of the Mission page that the page opens and scrolls to at its next draw, or
    /// null.</summary>
    public (string Script, string Section)? PendingSection { get; set; }

    /// <summary>True while the Mission tab shows the raw INI of the whole script in place of the graph. The view switch
    /// "Graph" and "Raw INI" sets it (section 11.5 of the M6a design).</summary>
    public bool ShowRaw { get; set; }

    /// <summary>True when the graph moves its view to the selection at its next draw. An example is a click on a
    /// Console message (section 12.4 of the M6a design).</summary>
    public bool PendingGraphFrame { get; set; }

    /// <summary>The item whose world items the Scene part frames at its next draw, or null (section 7.3 of the M6a
    /// design).</summary>
    public MissionRef? PendingSceneFrame { get; set; }

    /// <summary>Asks the Scene part to frame the world items of a mission item, for example after a double-click on a
    /// node.</summary>
    public void FrameInScene(MissionRef item) => PendingSceneFrame = item;

    // The systems that the user picked for items whose system the index cannot find. The Hierarchy menu "Show in
    // system..." picks them (section 9 of the M6a design). The choice lasts until the editor closes and writes nothing.
    private readonly Dictionary<SectionRef, string> systemOverrides = [];

    /// <summary>A number that changes with each choice of "Show in system...", so the mission layer reads its items
    /// again.</summary>
    public int OverridesVersion { get; private set; }

    /// <summary>The system that the user picked for an item, or null.</summary>
    public string? SystemOverride(SectionRef section) => systemOverrides.GetValueOrDefault(section);

    /// <summary>Shows an item of the group "System unknown" in a system, for the session only.</summary>
    public void ShowInSystem(SectionRef section, string system)
    {
        systemOverrides[section] = system;
        OverridesVersion++;
    }

    /// <summary>True while the base-game messages show (decision D527). One setting for the Console and the mission
    /// views.</summary>
    public bool ShowBaseGame => window.Settings.ShowBaseGameMessages;

    /// <summary>Starts a run of the mission checks for the open profile.</summary>
    public void Run()
    {
        if (window.Session is not { } current)
        {
            return;
        }

        MissionInputs inputs;
        try
        {
            Follow(current.Edits);
            current.Edits.Missions.Coverage = EngineCoverage.Instance;
            inputs = current.Edits.Missions.RunInputs();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, $"The mission checks did not start: {e.Message}");
            return;
        }

        latestRun = inputs.Generation;
        changedDuringRun.Clear();
        Task.Run(() =>
        {
            MissionRunResult? result = null;
            string? error = null;
            try
            {
                result = MissionRun.Execute(inputs);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                error = e.Message;
            }

            window.QueueUIThread(() => Done(current, inputs, result, error));
        });
    }

    /// <summary>Takes the result of a run on the UI thread. A result or an error of a closed session or of a replaced
    /// run is dropped.</summary>
    private void Done(EditorSession current, MissionInputs inputs, MissionRunResult? result, string? error)
    {
        if (window.Session != current || inputs.Generation != latestRun)
        {
            return;
        }

        if (result == null)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, $"The mission checks stopped: {error}");
            return;
        }

        if (!current.Edits.TakeMissionRun(result))
        {
            return;
        }

        var missions = current.Edits.Missions;
        missions.TakeRun(inputs, result);
        if (WithNewerModels(result, changedDuringRun, missions.ScriptPaths(), p => missions.Model(p).Analysis) is not { } newer)
        {
            // A new or deleted script needs the rows of a new run.
            Run();
            return;
        }

        changedDuringRun.Clear();
        Result = newer;
        rawTexts.Clear();
        marks.Clear();
        window.Log.ReplaceSet(MissionChecks.Source, Result.Problems.ToList());
    }

    /// <summary>
    /// The result of a run with the model of each script that changed while the run ran (section 6.11 of the M6b
    /// design). That model is newer than the analysis of the run. Null when such a script was added or deleted: then a
    /// new run must give the rows of the Missions browser.
    /// </summary>
    /// <param name="scripts">The scripts of W now.</param>
    /// <param name="model">Gives the analysis of the model of a script now.</param>
    internal static MissionRunResult? WithNewerModels(MissionRunResult result, IReadOnlyCollection<string> changed,
        IReadOnlyList<string> scripts, Func<string, MissionAnalysis> model)
    {
        if (changed.Any(p => result.IsScript(p) != scripts.Contains(p, VfsPath.Comparer)))
        {
            return null;
        }

        var analyses = new Dictionary<string, MissionAnalysis>(result.Analyses, VfsPath.Comparer);
        foreach (var path in changed.Where(result.IsScript))
        {
            analyses[path] = model(path);
        }

        return result with { Analyses = analyses };
    }

    /// <summary>Follows the changes of the mission scripts of a session.</summary>
    private void Follow(EditSession edits)
    {
        if (ReferenceEquals(followed, edits))
        {
            return;
        }

        Unfollow();
        edits.Missions.ScriptsChanged += ScriptsChanged;
        edits.Changed += FilesChanged;
        followed = edits;
    }

    private void Unfollow()
    {
        if (followed != null)
        {
            followed.Missions.ScriptsChanged -= ScriptsChanged;
            followed.Changed -= FilesChanged;
            followed = null;
        }
    }

    /// <summary>Takes the model of each changed script into the result, and gives the Console its problems
    /// again.</summary>
    private void ScriptsChanged(MissionScriptsChange change)
    {
        changedDuringRun.UnionWith(change.Versions.Keys);
        if (Result is not { } result || followed is not { } edits)
        {
            return;
        }

        var scripts = edits.Missions.ScriptPaths();
        var analyses = new Dictionary<string, MissionAnalysis>(result.Analyses, VfsPath.Comparer);
        foreach (var path in change.Versions.Keys)
        {
            rawTexts.Remove(path);
            marks.Remove(path);
            if (!result.IsScript(path) || !scripts.Contains(path, VfsPath.Comparer))
            {
                // The browser rows come from a run, so a new or deleted script needs one.
                Run();
                return;
            }

            analyses[path] = edits.Missions.Model(path).Analysis;
        }

        Result = result with { Analyses = analyses };
        window.Log.ReplaceSet(MissionChecks.Source, Result.Problems.ToList());
        // A rename, also its Undo and its Redo, keeps the selection on the renamed trigger (section 8.10 of the M6b
        // design).
        if (Followed(window.Selection, change.Renames.Select(r => (r.From.ToString(), r.To.ToString())).ToList(), []) is
            { } next)
        {
            window.Selection = next;
        }
    }

    /// <summary>Starts a run after a change of a ship build file, because the run reads the ship builds of all
    /// scripts.</summary>
    private void FilesChanged(EditChange change)
    {
        if (followed is { } edits && change.Files.Any(f => !IsScript(f) && edits.Missions.IsMissionFile(f)))
        {
            Run();
        }
    }

    /// <summary>
    /// Runs a raw edit of the whole text of a mission file, or of the INI block of a page (section 8.8 of the M6b
    /// design). A problem keeps nothing: the Console line holds the problem and the draft text, so the user can copy it
    /// back (decision D114).
    /// </summary>
    /// <param name="sections">The sections of the block in the file, or null for the whole file.</param>
    public void CommitRaw(string path, IReadOnlyList<SectionIdentity>? sections, string start, string end)
    {
        if (window.Session?.Edits is not { } edits)
        {
            return;
        }

        var plan = sections == null
            ? RawMissionEdit.PlanFile(edits, path, start, end)
            : RawMissionEdit.PlanBlock(edits, path, sections, start, end);
        if (plan.Problems.Count > 0)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, RawRefusal(plan.Problems.Select(p => p.ToString()), end));
            return;
        }

        foreach (var note in plan.Notes)
        {
            window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, note);
        }

        if (plan.Batch != null)
        {
            window.Edits.Apply(plan.Batch);
        }
    }

    /// <summary>
    /// Runs the batch of a gesture of the studio through the mission editor of the session (section 4.0 of the M6b
    /// design). After a gesture that adds an item, the studio selects the item. Else the selection follows the renames
    /// and the row moves of the result (section 8.10 of the M6b design). A null batch does nothing.
    /// </summary>
    /// <returns>True when the batch changed W.</returns>
    public bool Run(MissionBatch? batch)
    {
        if (batch == null || window.Edits.Apply(batch) is not { Status: EditStatus.Applied } result)
        {
            return false;
        }

        var selection = window.Selection;
        var next = result.Select is { } select && Result?.Of(ScriptOf(select) ?? "") is { } analysis
            ? SelectionOf(analysis, select)
            : null;
        next ??= Followed(selection, result.Renames.Select(r => (r.From, r.To)).ToList(), result.RowMoves);
        if (next != null && !Equals(next, selection))
        {
            window.Selection = next;
        }

        return true;
    }

    /// <summary>The install path of a reference of a mission item, or null.</summary>
    private static string? ScriptOf(string reference)
    {
        try
        {
            return ItemRefs.Parse(reference).Path;
        }
        catch (EditException)
        {
            return null;
        }
    }

    /// <summary>
    /// The selection of a reference of a result (section 4.0.7 of the M6b design) in an analysis: a trigger, a section,
    /// a line of a trigger or the script. Null for a reference that the analysis does not have.
    /// </summary>
    internal static MissionRef? SelectionOf(MissionAnalysis analysis, string reference)
    {
        ItemReference parsed;
        try
        {
            parsed = ItemRefs.Parse(reference);
        }
        catch (EditException)
        {
            return null;
        }

        var script = analysis.Script.Path;
        if (parsed.Path is not { } path || !VfsPath.Comparer.Equals(path, script))
        {
            return null;
        }

        switch (parsed.Kind)
        {
            case ItemRefKind.Script:
                return MissionRef.Of(script);
            case ItemRefKind.Trigger when analysis.Index.Trigger(parsed.Name) is { } trigger:
                return new MissionRef(script, MissionItemKind.Trigger, trigger.Section.Ref);
            case ItemRefKind.Section or ItemRefKind.Line when parsed.Section is { } id &&
                                                                analysis.Document.Find(new SectionRef(script, id)) is { } section:
                return MissionRef.OfSection(section.Ref, parsed.Kind == ItemRefKind.Line ? parsed.Row ?? -1 : -1);
            case ItemRefKind.Label:
                return new MissionRef(script, MissionItemKind.Label, Key: parsed.Name);
            default:
                return null;
        }
    }

    /// <summary>
    /// A selection after renames and row moves: a selected item that a rename renamed gets its new name, and a selected
    /// line of a trigger keeps its line when the rows before it moved (section 8.10 of the M6b design). Null when nothing
    /// changes it.
    /// </summary>
    /// <param name="renames">The old and the new references of each renamed item.</param>
    internal static MissionRef? Followed(object? selection, IReadOnlyList<(string From, string To)> renames,
        IReadOnlyList<RowMove> moves)
    {
        if (selection is not MissionRef { Section: { } section } item)
        {
            return null;
        }

        foreach (var (from, to) in renames)
        {
            if (Identity(from) is not { } old || Identity(to) is not { } renamed ||
                !VfsPath.Comparer.Equals(old.Path, section.Path) || !old.Id.Equals(section.Id))
            {
                continue;
            }

            item = item with { Section = renamed };
            section = renamed;
        }

        if (item.Kind == MissionItemKind.Row && item.Row >= 0)
        {
            var move = moves.FirstOrDefault(m => VfsPath.Comparer.Equals(m.Trigger.Script, section.Path) &&
                                                 MissionIndex.Is(m.Trigger.Name, section.Id.Key ?? "") && m.From == item.Row);
            if (move != null)
            {
                item = item with { Row = move.To };
            }
        }

        return Equals(item, selection) ? null : item;
    }

    /// <summary>The section of a reference of a trigger or a section, or null.</summary>
    private static SectionRef? Identity(string reference)
    {
        try
        {
            var parsed = ItemRefs.Parse(reference);
            return parsed switch
            {
                { Kind: ItemRefKind.Trigger, Path: { } path } => new SectionRef(path, new SectionIdentity("Trigger", parsed.Name)),
                { Kind: ItemRefKind.Section, Path: { } path, Section: { } id } => new SectionRef(path, id),
                _ => null,
            };
        }
        catch (EditException)
        {
            return null;
        }
    }

    /// <summary>The Console text of a raw edit that the editor refused. It keeps the whole text of the user, so the user
    /// can copy it back (decision D114). The raw blocks of all files use it.</summary>
    public static string RawRefusal(IEnumerable<string> problems, string text) =>
        $"{string.Join(" ", problems)} The editor kept the lines before the edit. Your text:\n{text}";

    /// <summary>The marks of a draft text of a mission file while the user types: one object for each file or block,
    /// so its marks stay from frame to frame.</summary>
    /// <param name="block">The sections of the INI block of a page, or null for the whole file.</param>
    public IRawDraft Draft(string path, IReadOnlyList<SectionIdentity>? block)
    {
        var key = block == null ? path : $"{path}#{string.Join("|", block)}";
        if (!drafts.TryGetValue(key, out var draft))
        {
            drafts[key] = draft = new MissionRawDraft(window, path, block);
        }

        return draft;
    }

    /// <summary>Why the user cannot edit a mission file, or null.</summary>
    public string? NoEditReason(string path) =>
        window.Session is not { } session ? "No profile is open."
        : session.NoEditReason ?? session.Edits.Missions.ReadOnlyReason(path);

    /// <summary>The note of the Mission tab and the mission pages while the session takes no edits, or null (section
    /// 8.1 of the M6b design).</summary>
    public string? ReadOnlyNote =>
        window.Session is { } session && (session.NoEditReason ?? session.Edits.ReadOnlyReason) is { } reason
            ? MissionErrors.NotEditableMessage(reason)
            : null;

    /// <summary>Forgets the result and the open mission, for example when the profile closes.</summary>
    public void Reset()
    {
        Unfollow();
        Result = null;
        OpenPath = null;
        PendingLine = null;
        PendingSection = null;
        PendingSceneFrame = null;
        PendingGraphFrame = false;
        latestRun = 0;
        rawTexts.Clear();
        marks.Clear();
        drafts.Clear();
        changedDuringRun.Clear();
    }

    /// <summary>True when the path is a mission script of the last run (section 12.4 of the M6a design).</summary>
    public bool IsScript(string path) => Result?.IsScript(path) == true;

    /// <summary>The analysis of a script, or null.</summary>
    public MissionAnalysis? Analysis(string path) => Result?.Of(path);

    /// <summary>
    /// Opens a mission: the Mission tab shows it, and its root becomes the selection of the Mission tab. When no system
    /// is open, the Scene opens the system of the start trigger. Else it opens the system that most triggers name
    /// (section 7.1 of the M6a design).
    /// </summary>
    /// <param name="show">True to show the Mission tab.</param>
    public void OpenMission(string path, bool show)
    {
        OpenPath = VfsPath.Normalize(path) ?? path;
        if (window.Scene.System == null && Analysis(OpenPath)?.Index is { } index &&
            MissionSummary.StartSystem(index) is { } nickname &&
            window.Session?.GameData?.Items.Systems.Get(nickname) is { } system)
        {
            // The open of a system clears the selection, so it comes before the selection of the mission.
            window.OpenSystem(system);
        }

        window.SelectInTab(CenterMode.Mission, Root!);
        if (show)
        {
            window.ShowPanel(PanelId);
        }
    }

    /// <summary>
    /// Opens the mission of a problem in the Mission tab and selects its line. The raw view goes to the line (section 12.4
    /// of the M6a design). Returns false and does nothing when the section is not in a mission script.
    /// </summary>
    /// <param name="entry">The index of the line in the section, or -1 for the whole section.</param>
    public bool GoTo(SectionRef target, int entry)
    {
        if (!IsScript(target.Path))
        {
            return false;
        }

        OpenMission(target.Path, show: true);
        window.SelectInTab(CenterMode.Mission, MissionRef.OfSection(target, entry));
        PendingLine = (target, entry);
        PendingGraphFrame = true;
        return true;
    }

    /// <summary>Opens a space cutscene that a mission line names in the Cutscene tab. The line holds a path below the
    /// data folder.</summary>
    public void OpenCutscene(string value)
    {
        if (MissionFiles.RelativeOf(value) is { } relative && window.Session?.Edits is { } edits &&
            edits.DataPath(relative) is { } path)
        {
            window.Cutscenes.OpenAndShow(path);
        }
    }

    /// <summary>Selects a mission and asks the Mission page to open one of its sections, for example "Voice lines"
    /// (section 5.2 of the M6a design).</summary>
    /// <param name="section">The id of the section of the page.</param>
    public void ShowSection(string path, string section)
    {
        var mission = MissionRef.Of(path);
        window.Selection = mission;
        PendingSection = (mission.Script, section);
    }

    /// <summary>
    /// A selection of the Mission tab when the last run has its script and its section, else null. Before the first
    /// run, each mission selection stays.
    /// </summary>
    public object? Valid(object? item) => Valid(Result, item);

    /// <summary>
    /// A kept selection of the Mission tab for a run (section 4.2 rule 3 of the M6a design). The selection stays when the
    /// run has its script and its section. It becomes its mission when its section is gone, else null. Before the first
    /// run (a null run), each mission selection stays.
    /// </summary>
    internal static object? Valid(MissionRunResult? result, object? item)
    {
        if (item is not MissionRef selection)
        {
            return null;
        }

        if (result == null)
        {
            return selection;
        }

        if (result.Of(selection.Script) is not { } analysis)
        {
            return null;
        }

        return selection.Section is not { } section || analysis.Document.Find(section) != null
            ? selection
            : MissionRef.Of(selection.Script);
    }

    // The mission lines that name each object, base or zone, for the run of the last result.
    private readonly Dictionary<string, IReadOnlyList<MissionObjectUse>> objectUses = new(StringComparer.OrdinalIgnoreCase);
    private MissionRunResult? objectUsesRun;

    /// <summary>The lines of the mission scripts that name an object of a system, a base or a zone (section 10.18 of the
    /// M6a design). The pages ask in each frame, so the workspace keeps the lines until the next run.</summary>
    public IReadOnlyList<MissionObjectUse> UsesOf(string nickname)
    {
        if (Result is not { } result)
        {
            return [];
        }

        if (!ReferenceEquals(objectUsesRun, result))
        {
            objectUses.Clear();
            objectUsesRun = result;
        }

        if (!objectUses.TryGetValue(nickname, out var uses))
        {
            objectUses[nickname] = uses = MissionSummary.ObjectUses(result, nickname);
        }

        return uses;
    }

    /// <summary>The number of hidden base-game problems of all scripts, for the toggle.</summary>
    public int HiddenTotal => Result == null ? 0 : BaseGameMessages.Count(Result.Problems);

    /// <summary>
    /// The raw text of a script: the file text of a text script that no mod changes, else the canonical text (decision
    /// D529). Null while a worker task makes it, and when the run has no such script.
    /// </summary>
    public RawText? RawTextOf(string path)
    {
        if (RawTextTask(path) is not { } task)
        {
            return null;
        }

        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>Why the raw text of a script cannot show, or null while it loads or when it shows.</summary>
    public string? RawTextError(string path) =>
        RawTextTask(path) is { IsFaulted: true } task ? task.Exception?.GetBaseException().Message ?? "" : null;

    private Task<RawText>? RawTextTask(string path)
    {
        if (Result?.Of(path) is not { } analysis)
        {
            return null;
        }

        if (!rawTexts.TryGetValue(analysis.Script.Path, out var task))
        {
            rawTexts[analysis.Script.Path] = task = Task.Run(analysis.RawText);
        }

        return task;
    }

    /// <summary>The marks of the raw text of a script: the problems that show, and the reasons of the read-only sections.
    /// The marks stay until the run, the text or the toggle changes, because the raw view reads them in each
    /// frame.</summary>
    public IReadOnlyList<RawMark> MarksOf(string path)
    {
        if (Result?.Of(path) is not { } analysis || RawTextOf(path) is not { } raw)
        {
            return [];
        }

        if (marksShowBaseGame != ShowBaseGame)
        {
            marks.Clear();
            marksShowBaseGame = ShowBaseGame;
        }

        if (!marks.TryGetValue(analysis.Script.Path, out var known) || !ReferenceEquals(known.Text, raw))
        {
            known = (raw, IniLint.Marks(raw, analysis.Problems, ShowBaseGame));
            marks[analysis.Script.Path] = known;
        }

        return known.Marks;
    }

    /// <summary>Sets the base-game toggle. The Console and the mission views share it.</summary>
    public void SetShowBaseGame(bool value)
    {
        window.Settings.ShowBaseGameMessages = value;
        window.Settings.Save();
    }
}
