using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The play requests that each kind of play shares (section 7.2 of the M6c design): the one stop of Shift+F5, the Stop
/// button and the agent tool <c>play_stop</c>, and the agent play rule of the user decision of 2026-10-20. The stop of a
/// running OpenLancer game is in <c>PlayControllerTests</c>, which has a game.
/// </summary>
public sealed class PlayRequestsTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly PlayController play = new(new PlayControllerTests.FakeEditor(new QueuedUIThread()),
        _ => throw new InvalidOperationException("The tests of the stop start no game."), () => TimeSpan.Zero);

    public void Dispose() => play.Dispose();

    [Fact]
    public void WithoutAnOpenLancerGameTheStopStopsTheGameOfThePlayCopy()
    {
        var games = new FakeGames { GameRuns = true };

        Assert.Equal(PlayStopped.Game, PlayRequests.Stop(play, games));

        Assert.Equal(1, games.Stops);
    }

    [Fact]
    public void WithoutAGameTheStopStopsNothing()
    {
        var games = new FakeGames();

        Assert.Equal(PlayStopped.Nothing, PlayRequests.Stop(play, games));

        Assert.Equal(0, games.Stops);
    }

    /// <summary>A request of an agent without unsaved edits starts at once, without a question. Nobody allowed a save,
    /// so Play refuses edits that come before its save.</summary>
    [Fact]
    public void AnAgentRequestWithoutUnsavedEditsStartsAtOnce()
    {
        var host = new FakeAgentHost();
        var rules = new List<PlaySaveRule>();

        var result = PlayRequests.WithAgentRule(host, EditOrigin.Agent, () => PlayStatus.Idle, Start(rules));

        Assert.Equal([PlaySaveRule.NoEdits], rules);
        Assert.Null(host.Asked);
        Assert.True(result.Wait(Wait));
        Assert.True(result.Result.Started);
    }

    /// <summary>With unsaved edits, the editor asks the user. Cancel gives the agent refused with the reason of the user
    /// decision, and the play does not start. Escape and the close button do the same. Cancel is the default button, so
    /// Enter does not save.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelOfTheQuestionGivesRefusedWithTheReason(bool button)
    {
        var host = new FakeAgentHost { UnsavedMod = "ol_mod" };
        var rules = new List<PlaySaveRule>();

        var result = PlayRequests.WithAgentRule(host, EditOrigin.Agent, () => PlayStatus.Idle, Start(rules));

        var (title, message, choices, cancel) = host.Asked!.Value;
        Assert.Equal("Agent Play", title);
        Assert.Equal("An agent wants to start Play. Mod 'ol_mod' has unsaved edits. Save them and continue?", message);
        Assert.Equal(["Save and continue", "Cancel"], choices.Select(c => c.Label));
        Assert.Equal([false, true], choices.Select(c => c.Default));
        Assert.False(result.IsCompleted);
        if (button)
            choices[1].Action();
        else
            cancel();

        Assert.True(result.Wait(Wait));
        Assert.Empty(rules);
        Assert.Equal(EditErrorKind.Refused, result.Result.Error!.Kind);
        Assert.Equal("The user did not save. Play did not start.", result.Result.Error.Message);
    }

    /// <summary>"Save and continue" starts the play. Play then saves first, as F5 does (see
    /// <c>PlayControllerTests</c>).</summary>
    [Fact]
    public void SaveAndContinueStartsThePlay()
    {
        var host = new FakeAgentHost { UnsavedMod = "ol_mod" };
        var rules = new List<PlaySaveRule>();

        var result = PlayRequests.WithAgentRule(host, EditOrigin.Agent, () => PlayStatus.Idle, Start(rules));
        Assert.Empty(rules);
        host.Asked!.Value.Choices[0].Action();

        Assert.True(result.Wait(Wait));
        Assert.Equal([PlaySaveRule.Save], rules);
        Assert.True(result.Result.Started);
    }

    /// <summary>The setting "Auto-save and continue when an MCP agent starts Play" skips the question. A request of the
    /// user never asks: F5 and Ctrl+F5 do not change.</summary>
    [Theory]
    [InlineData(EditOrigin.Agent, true)]
    [InlineData(EditOrigin.User, false)]
    public void TheSettingAndARequestOfTheUserSkipTheQuestion(EditOrigin origin, bool autoSave)
    {
        var host = new FakeAgentHost { UnsavedMod = "ol_mod", AutoSaveAgentPlay = autoSave };
        var rules = new List<PlaySaveRule>();

        var result = PlayRequests.WithAgentRule(host, origin, () => PlayStatus.Idle, Start(rules));

        Assert.Null(host.Asked);
        Assert.Equal([PlaySaveRule.Save], rules);
        Assert.True(result.Wait(Wait));
    }

    /// <summary>The setting is off by default, and the settings file keeps it.</summary>
    [Fact]
    public void TheSettingIsOffByDefaultAndTheSettingsKeepIt()
    {
        var settings = EditorSettings.Transient();
        Assert.False(settings.Agent.AutoSavePlay);

        settings.Agent.AutoSavePlay = true;
        var read = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        Assert.True(read.Agent.AutoSavePlay);
    }

    [Fact]
    public void TheTextsPassTheSteLint()
    {
        string[] texts =
        [
            PlayRequests.AgentPlayQuestion("ol_mod"), PlayRequests.NotSavedReason, Editor.Agent.AgentMenu.AutoSavePlayTip,
            Editor.Missions.MissionPlay.OtherPlayReason, PlayRequests.AgentWaitsReason,
            PlayRequests.EditsWhileWaiting("ol_mod"),
        ];

        Assert.Empty(texts.SelectMany(t => SteLint.Problems(t)));
    }

    /// <summary>The setting of agent Play shows only when an agent tool can start Play (UI rule 4). This build has no
    /// such tool. Milestone MCP1 adds them.</summary>
    [Fact]
    public void TheSettingOfAgentPlayShowsOnlyWithAPlayTool()
    {
        Assert.False(Editor.Agent.AgentMenu.AgentCanPlay);
        Assert.All(Editor.Agent.AgentMenu.PlayTools, name => Assert.Null(OpenLancer.Studio.Agent.AgentTools.Find(name)));
    }

    /// <summary>Starts the play: it keeps the save rule of each start.</summary>
    private static Func<PlaySaveRule, Task<PlayRequestResult>> Start(List<PlaySaveRule> rules) => rule =>
    {
        rules.Add(rule);
        return Task.FromResult(new PlayRequestResult(PlayStatus.Starting));
    };

    /// <summary>The game of a play copy for the tests of the stop.</summary>
    internal sealed class FakeGames : IGameStop
    {
        public bool GameRuns { get; set; }

        public int Stops { get; private set; }

        public void StopGame()
        {
            Stops++;
            GameRuns = false;
        }
    }

    /// <summary>The editor side of the agent play rule. It keeps the last question.</summary>
    internal sealed class FakeAgentHost : IAgentPlayHost
    {
        public string? UnsavedMod { get; set; }

        public bool AutoSaveAgentPlay { get; set; }

        public (string Title, string Message, IReadOnlyList<Choice> Choices, Action Cancel)? Asked { get; private set; }

        public void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel) =>
            Asked = (title, message, choices, cancel);
    }
}
