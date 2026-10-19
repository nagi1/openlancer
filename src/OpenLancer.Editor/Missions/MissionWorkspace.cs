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
/// mission checks, the open mission, the selection of the Mission tab, and the requests of the raw view. One mission is
/// open at a time. The workspace changes no file: M6a only shows mission scripts.
/// </summary>
/// <remarks>A run of the checks starts after each load of the game data. The UI thread makes the snapshot, a worker task
/// runs the checks, and the UI thread takes the result. A run that a newer run replaced drops its result (section 12.3
/// of the M6a design).</remarks>
internal sealed class MissionWorkspace(EditorWindow window)
{
    /// <summary>The id of the Mission tab.</summary>
    public const string PanelId = "mission";

    /// <summary>The id of the Missions tab.</summary>
    public const string BrowserId = "missions";

    private readonly Dictionary<string, RawText> rawTexts = new(VfsPath.Comparer);

    /// <summary>The result of the last run of the mission checks, or null before the first run.</summary>
    public MissionRunResult? Result { get; private set; }

    /// <summary>True while a run of the checks runs.</summary>
    public bool Running { get; private set; }

    /// <summary>The install path of the open mission, or null.</summary>
    public string? OpenPath { get; private set; }

    /// <summary>The analysis of the open mission, or null.</summary>
    public MissionAnalysis? Open => OpenPath != null ? Result?.Of(OpenPath) : null;

    /// <summary>The selection of the open mission when nothing in it is selected, or null.</summary>
    public MissionRef? Root => OpenPath != null ? MissionRef.Of(OpenPath) : null;

    /// <summary>A line of the raw view to go to at the next draw, or null.</summary>
    public (string Path, int Line)? PendingLine { get; set; }

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

        Running = true;
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

            window.QueueUIThread(() => Done(current, result, error));
        });
    }

    /// <summary>Takes the result of a run on the UI thread. A result of a closed session or of a replaced run is
    /// dropped.</summary>
    private void Done(EditorSession current, MissionRunResult? result, string? error)
    {
        if (window.Session != current)
        {
            return;
        }

        if (result == null)
        {
            Running = false;
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, $"The mission checks stopped: {error}");
            return;
        }

        if (!current.Edits.TakeMissionRun(result))
        {
            return;
        }

        Running = false;
        Result = result;
        rawTexts.Clear();
        window.Log.ReplaceSource(MissionChecks.Source, result.Problems.ToList());
        RawIniEditor.Identifiers = (MissionSchema.Retail.Conditions.Concat(MissionSchema.Retail.Actions).Select(n => n.Name),
            MissionSchema.Retail.Sections.SelectMany(s => s.Keys).Select(k => k.Key)
                .Concat(MissionSchema.Retail.Directives.Select(d => d.Name)).Distinct());
    }

    /// <summary>Forgets the result and the open mission, for example when the profile closes.</summary>
    public void Reset()
    {
        Result = null;
        Running = false;
        OpenPath = null;
        PendingLine = null;
        rawTexts.Clear();
    }

    /// <summary>True when the path is a mission script of the last run (section 12.4 of the M6a design).</summary>
    public bool IsScript(string path) => Result?.IsScript(path) == true;

    /// <summary>The analysis of a script, or null.</summary>
    public MissionAnalysis? Analysis(string path) => Result?.Of(path);

    /// <summary>Opens a mission: the Mission tab shows it, and its root becomes the selection of the Mission tab.</summary>
    /// <param name="show">True to show the Mission tab.</param>
    public void OpenMission(string path, bool show)
    {
        OpenPath = VfsPath.Normalize(path) ?? path;
        window.SelectInTab(CenterMode.Mission, Root!);
        if (show)
        {
            window.ShowPanel(PanelId);
        }
    }

    /// <summary>Closes the open mission.</summary>
    public void CloseMission() => OpenPath = null;

    /// <summary>
    /// Opens the mission of a problem, selects its line and goes to it in the raw view (section 12.4 of the M6a design).
    /// Returns false when the section is not in a mission script.
    /// </summary>
    public bool GoTo(SectionRef target, int entry)
    {
        if (!IsScript(target.Path))
        {
            return false;
        }

        OpenMission(target.Path, show: true);
        var item = RefOf(target, entry);
        window.SelectInTab(CenterMode.Mission, item);
        if (RawTextOf(target.Path) is { } raw && raw.LineOf(target.Id, entry) is { } line)
        {
            PendingLine = (OpenPath!, line);
        }

        return true;
    }

    /// <summary>The selection of a section of a mission, or of a line of it.</summary>
    public MissionRef RefOf(SectionRef target, int entry)
    {
        var path = VfsPath.Normalize(target.Path) ?? target.Path;
        var kind = MissionSection.KindOf(target.Id.Type) switch
        {
            MissionSectionKind.Mission => MissionItemKind.Mission,
            MissionSectionKind.Trigger => entry >= 0 ? MissionItemKind.Row : MissionItemKind.Trigger,
            MissionSectionKind.Ship => MissionItemKind.Ship,
            MissionSectionKind.Formation => MissionItemKind.Formation,
            MissionSectionKind.Solar => MissionItemKind.Solar,
            MissionSectionKind.Loot => MissionItemKind.Loot,
            MissionSectionKind.OrderList => MissionItemKind.OrderList,
            MissionSectionKind.Objective => MissionItemKind.Objective,
            MissionSectionKind.Npc => MissionItemKind.Npc,
            MissionSectionKind.Conversation => MissionItemKind.Conversation,
            MissionSectionKind.RandomEncounter => MissionItemKind.RandomEncounter,
            _ => MissionItemKind.OtherSection,
        };
        return kind == MissionItemKind.Mission
            ? MissionRef.Of(path)
            : new MissionRef(path, kind, target, Row: kind == MissionItemKind.Row ? entry : -1);
    }

    /// <summary>
    /// A selection of the Mission tab when the last run has its script and its section, else null. Before the first
    /// run, each mission selection stays.
    /// </summary>
    public object? Valid(object? item)
    {
        if (item is not MissionRef selection)
        {
            return null;
        }

        if (Result == null)
        {
            return selection;
        }

        if (Result.Of(selection.Script) is not { } analysis)
        {
            return null;
        }

        return selection.Section is not { } section || analysis.Document.Find(section) != null ? selection : Root;
    }

    /// <summary>The problems of an analysis that show: each problem of a mod, and the base-game problems while they
    /// show.</summary>
    public IReadOnlyList<Diagnostic> Shown(MissionAnalysis analysis) =>
        analysis.Problems.Where(p => ShowBaseGame || p.Origin?.IsBaseGame != true).ToList();

    /// <summary>The number of hidden base-game problems of an analysis, or 0 while they show.</summary>
    public int Hidden(MissionAnalysis analysis) =>
        ShowBaseGame ? 0 : analysis.Problems.Count(p => p.Origin?.IsBaseGame == true);

    /// <summary>The number of hidden base-game problems of all scripts, for the toggle.</summary>
    public int HiddenTotal => Result?.Problems.Count(p => p.Origin?.IsBaseGame == true) ?? 0;

    /// <summary>The raw text of a script: the file text of a text script that no mod changes, else the canonical text
    /// (decision D529).</summary>
    public RawText? RawTextOf(string path)
    {
        if (Result?.Of(path) is not { } analysis)
        {
            return null;
        }

        if (!rawTexts.TryGetValue(analysis.Script.Path, out var raw))
        {
            rawTexts[analysis.Script.Path] = raw = analysis.RawText();
        }

        return raw;
    }

    /// <summary>The raw lines of the problems of a script that show, as marks of its raw text.</summary>
    public IReadOnlyList<RawMark> MarksOf(string path) =>
        Result?.Of(path) is { } analysis && RawTextOf(path) is { } raw
            ? IniLint.Marks(raw, analysis.Problems, ShowBaseGame)
            : [];

    /// <summary>Sets the base-game toggle. The Console and the mission views share it.</summary>
    public void SetShowBaseGame(bool value)
    {
        window.Settings.ShowBaseGameMessages = value;
        window.Settings.Save();
    }
}
