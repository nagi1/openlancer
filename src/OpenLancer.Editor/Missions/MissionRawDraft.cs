using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The marks of a draft text of a mission file while the user types (section 8.8 of the M6b design, UI rule 11 of
/// AGENTS.md). The problems of the text show at once. The problems of the mission checks of the parsed text come from a
/// worker. One run of the checks runs at a time. A newer text waits for it, and the result of an older text goes.
/// </summary>
/// <param name="block">The sections of the INI block of a page in the file, or null for the whole file.</param>
internal sealed class MissionRawDraft(EditorWindow window, string path, IReadOnlyList<SectionIdentity>? block) : IRawDraft
{
    // The number of the last text, the checks of the newest text that wait for the worker, and their number.
    private int generation;
    private Func<IReadOnlyList<RawDraftMark>>? waiting;
    private int waitingGeneration;
    private bool running;

    public IReadOnlyList<RawMark> Marks { get; private set; } = [];

    public void Changed(string text)
    {
        generation++;
        if (window.Session?.Edits is not { } edits)
        {
            return;
        }

        var draft = RawMissionEdit.Draft(edits, path, block, text);
        if (draft.Checks is not { } checks)
        {
            waiting = null;
            Marks = draft.Problems
                .Select(p => new RawMark(Math.Max(0, p.Line - 1), DiagnosticSeverity.Error, p.Message, BaseGame: false))
                .ToList();
            return;
        }

        waiting = checks;
        waitingGeneration = generation;
        StartWaiting();
    }

    /// <summary>Starts the checks that wait, when no run is busy.</summary>
    private void StartWaiting()
    {
        if (running || waiting is not { } checks)
        {
            return;
        }

        var run = waitingGeneration;
        waiting = null;
        running = true;
        var showBaseGame = window.Settings.ShowBaseGameMessages;
        Task.Run(() =>
        {
            IReadOnlyList<RawMark>? marks = null;
            try
            {
                marks = IniLint.Marks(checks().Select(m => (Math.Max(0, m.Line - 1), m.Problem)), showBaseGame);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The marks of the text stay. The commit of the edit runs the checks again and shows their problems.
            }

            window.QueueUIThread(() =>
            {
                running = false;
                if (run == generation && marks != null)
                {
                    Marks = marks;
                }

                StartWaiting();
            });
        });
    }
}
