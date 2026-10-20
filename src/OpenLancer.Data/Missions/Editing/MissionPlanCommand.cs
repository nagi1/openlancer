using System;
using System.Collections.Generic;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The session command that runs mission commands in the scope of the session (section 4.1 of the M6b design). While
/// it runs, the guard of the mission files lets its edits through (decision D660). One plan command runs all
/// commands of a batch, so a later command sees the result of an earlier one.
/// </summary>
internal sealed class MissionPlanCommand : EditCommand
{
    private readonly MissionEditor? editor;
    private readonly IReadOnlyList<MissionCommand> commands;
    private readonly EditCommand? inner;

    /// <param name="label">The label of the history entry.</param>
    public MissionPlanCommand(string label, MissionEditor editor, IReadOnlyList<MissionCommand> commands) : base(label)
    {
        this.editor = editor;
        this.commands = commands;
    }

    private MissionPlanCommand(string label, EditCommand inner) : base(label)
    {
        commands = [];
        this.inner = inner;
    }

    /// <summary>
    /// A session command that runs another session command with the guard open. The reference provider of the M3
    /// rename and delete uses it for the mission lines that the layer planned (section 4.10 of the M6b design).
    /// </summary>
    internal static MissionPlanCommand Wrap(EditCommand inner) => new(inner.Label, inner);

    /// <summary>The draft of the last run, or null before the first run or after a refusal.</summary>
    public MissionDraft? Draft { get; private set; }

    /// <summary>The index of the command that refused in the last run, or -1.</summary>
    public int FailedIndex { get; private set; } = -1;

    internal override void Run(EditSession.CommandScope scope)
    {
        scope.MissionDepth++;
        try
        {
            if (inner != null)
            {
                inner.Run(scope);
                return;
            }
            Draft = null;
            FailedIndex = -1;
            var draft = new MissionDraft(editor!, scope);
            for (var i = 0; i < commands.Count; i++)
            {
                draft.BeginCommand(i);
                try
                {
                    commands[i].Run(draft);
                }
                catch (EditException e)
                {
                    FailedIndex = i;
                    throw new EditException(e.Error with { Index = e.Error.Index ?? i });
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException)
                {
                    FailedIndex = i;
                    throw new EditException(new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, e.Message)
                    {
                        Index = i,
                    });
                }
            }
            Draft = draft;
        }
        finally
        {
            scope.MissionDepth--;
        }
    }
}
