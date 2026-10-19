using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;
using OpenLancer.Runtime.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The mission scripts of the open profile and the open mission (section 4 of the M6a design): the last run of the
/// mission checks, the open mission, the selection of the Mission tab, and the requests of the raw view and of the
/// Mission page. One mission is open at a time. The workspace changes no file: M6a only shows mission scripts.
/// </summary>
/// <remarks>
/// <para>A run of the checks starts after each load of the game data. The UI thread makes the snapshot, a worker task runs
/// the checks, and the UI thread takes the result. A run that a newer run replaced drops its result and its error
/// (section 12.3 of the M6a design).</para>
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
    private bool marksShowBaseGame;
    private int latestRun;

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
            inputs = current.Edits.MissionInputs(EngineCoverage.Instance);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, $"The mission checks did not start: {e.Message}");
            return;
        }

        latestRun = inputs.Generation;
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

            window.QueueUIThread(() => Done(current, inputs.Generation, result, error));
        });
    }

    /// <summary>Takes the result of a run on the UI thread. A result or an error of a closed session or of a replaced
    /// run is dropped.</summary>
    private void Done(EditorSession current, int generation, MissionRunResult? result, string? error)
    {
        if (window.Session != current || generation != latestRun)
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

        Result = result;
        rawTexts.Clear();
        marks.Clear();
        window.Log.ReplaceSet(MissionChecks.Source, result.Problems.ToList());
    }

    /// <summary>Forgets the result and the open mission, for example when the profile closes.</summary>
    public void Reset()
    {
        Result = null;
        OpenPath = null;
        PendingLine = null;
        PendingSection = null;
        PendingSceneFrame = null;
        PendingGraphFrame = false;
        latestRun = 0;
        rawTexts.Clear();
        marks.Clear();
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
