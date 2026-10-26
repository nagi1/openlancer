using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Dialogs;

namespace OpenLancer.Editor.Play;

/// <summary>The game of a play copy that Stop can stop (milestone M11). <see cref="GamePlay"/> is it, and the tests give
/// a fake.</summary>
internal interface IGameStop
{
    /// <summary>True while the game of a play copy runs.</summary>
    bool GameRuns { get; }

    /// <summary>Stops the game of the play copy, or ends it at once when it does not exit.</summary>
    void StopGame();
}

/// <summary>What a stop request stopped.</summary>
internal enum PlayStopped
{
    /// <summary>No game ran.</summary>
    Nothing,

    /// <summary>The game of Play in OpenLancer.</summary>
    OpenLancer,

    /// <summary>The game of a play copy.</summary>
    Game,
}

/// <summary>
/// The result of a play request of the shared command layer (section 7.2 of the M6c design). A play request changes no
/// data of the mod, so the result has no revision and no history entry.
/// </summary>
/// <param name="Play">The Play status of OpenLancer when the request ended.</param>
internal sealed record PlayRequestResult(PlayStatus Play)
{
    /// <summary>Why the play did not start, or null when it started. The kinds are <c>refused</c>,
    /// <c>save_blocked</c> and <c>changed_on_disk</c> (section 7.4 of the M6c design).</summary>
    public EditError? Error { get; init; }

    /// <summary>The start plan of a mission play, or null for another play or a plan that the play did not make.</summary>
    public StartPlan? Plan { get; init; }

    /// <summary>True when the play started, or when the running mission play starts again at the new start.</summary>
    public bool Started => Error == null;

    /// <summary>A request that did not start a play.</summary>
    public static PlayRequestResult Refused(PlayStatus play, string detail, string message,
        EditErrorKind kind = EditErrorKind.Refused) => new(play) { Error = new EditError(kind, detail, message) };
}

/// <summary>What the agent play rule reads of the editor (user decision of 2026-10-20 of the M6c design).</summary>
internal interface IAgentPlayHost
{
    /// <summary>The id of the active mod while it has unsaved edits, else null.</summary>
    string? UnsavedMod { get; }

    /// <summary>The agent setting "Auto-save and continue when an MCP agent starts Play". It is off by default.</summary>
    bool AutoSaveAgentPlay { get; }

    /// <summary>Shows a question with buttons.</summary>
    /// <param name="cancel">What Escape and the close button of the title bar do.</param>
    void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel);
}

/// <summary>
/// The play requests of the shared command layer that each kind of play shares (section 7.2 of the M6c design, decision
/// D758). <see cref="Stop"/> is the one stop of Shift+F5, the Stop button, the menu item and the agent tool
/// <c>play_stop</c>, for each kind of play. <see cref="WithAgentRule"/> is the rule of a play request of an agent.
/// </summary>
internal static class PlayRequests
{
    /// <summary>The title of the question of the agent play rule.</summary>
    public const string AgentPlayTitle = "Agent Play";

    /// <summary>The button of the question that saves the active mod and starts the play.</summary>
    public const string SaveAndContinue = "Save and continue";

    /// <summary>The button of the question that does not start the play.</summary>
    public const string CancelLabel = "Cancel";

    /// <summary>The reason of the refusal when the user does not save (user decision of 2026-10-20).</summary>
    public const string NotSavedReason = "The user did not save. Play did not start.";

    /// <summary>The detail code of the refusal when the user does not save.</summary>
    public const string NotSavedDetail = "NotSaved";

    /// <summary>The question of the agent play rule (user decision of 2026-10-20 of the M6c design).</summary>
    public static string AgentPlayQuestion(string mod) =>
        $"An agent wants to start Play. Mod '{mod}' has unsaved edits. Save them and continue?";

    /// <summary>Stops the game of Play in OpenLancer when one runs, else the game of the play copy. A game that does not
    /// respond ends at once (Force stop).</summary>
    public static PlayStopped Stop(PlayController play, IGameStop games)
    {
        if (play.Status != PlayStatus.Idle)
        {
            play.Stop();
            return PlayStopped.OpenLancer;
        }

        if (games.GameRuns)
        {
            games.StopGame();
            return PlayStopped.Game;
        }

        return PlayStopped.Nothing;
    }

    /// <summary>
    /// Runs a play request with the agent play rule (user decision of 2026-10-20 of the M6c design). A request of an
    /// agent never saves on its own. When the active mod has unsaved edits, the editor asks the user first: "Save and
    /// continue" starts the play, and Play saves first, as F5 does. "Cancel", Escape and the close button give the
    /// refusal <see cref="NotSavedReason"/>. The agent setting "Auto-save and continue when an MCP agent starts Play"
    /// skips the question. A request without unsaved edits, and each request of the user (F5 and Ctrl+F5), starts at
    /// once.
    /// </summary>
    /// <param name="status">Gives the Play status for a refusal.</param>
    /// <param name="start">Starts the play on the UI thread. The question calls it from its button, so it also runs on
    /// the UI thread.</param>
    public static Task<PlayRequestResult> WithAgentRule(IAgentPlayHost host, EditOrigin origin, Func<PlayStatus> status,
        Func<Task<PlayRequestResult>> start)
    {
        if (origin != EditOrigin.Agent || host.UnsavedMod is not { } mod || host.AutoSaveAgentPlay)
        {
            return start();
        }

        var done = new TaskCompletionSource<PlayRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Ask(AgentPlayTitle, AgentPlayQuestion(mod),
        [
            new Choice(SaveAndContinue, () => Forward(start(), done)),
            new Choice(CancelLabel, Refuse),
        ], Refuse);
        return done.Task;

        void Refuse() => done.TrySetResult(PlayRequestResult.Refused(status(), NotSavedDetail, NotSavedReason));
    }

    /// <summary>Gives the result of a task to a completion source, also an exception.</summary>
    private static void Forward(Task<PlayRequestResult> from, TaskCompletionSource<PlayRequestResult> to) =>
        from.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                to.TrySetResult(task.Result);
            }
            else if (task.Exception is { } failure)
            {
                to.TrySetException(failure.InnerExceptions);
            }
            else
            {
                to.TrySetCanceled();
            }
        }, TaskScheduler.Default);
}
