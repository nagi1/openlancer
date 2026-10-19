using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The save prompt when the user closes the editor window (roadmap decision D78). The host of these tests runs the
/// close prompt on a real edit session, as the editor window does.
/// </summary>
public sealed class ClosePromptTests
{
    private const string ModName = "Act Mod";

    /// <summary>A workspace whose active mod "act" has a patch of the goods and the name <see cref="ModName"/>.</summary>
    private static TestWorkspace Workspace()
    {
        var ws = EditSessionTests.Workspace(new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 120\n" });
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), ModManifest.FileName),
            $$"""{ "id": "act", "name": "{{ModName}}", "formatVersion": 1 }""");
        return ws;
    }

    private static void Edit(EditSession session) => session.Execute(Set(Good("g1"), "volume", new Int32Value(3)));

    [Fact]
    public void WithoutUnsavedEditsTheWindowClosesAtOnce()
    {
        using var ws = Workspace();
        var host = new Host(ws.Session("act"));

        Assert.True(new ClosePrompt(host).Request());

        Assert.Equal(["end edits"], host.Calls);
        Assert.Null(host.Dialog);
    }

    [Fact]
    public void UnsavedEditsKeepTheWindowOpenAndShowTheDialog()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);

        Assert.False(new ClosePrompt(host).Request());

        Assert.Equal(["end edits", "ask"], host.Calls);
        var dialog = host.Dialog!.Value;
        Assert.Equal(ClosePrompt.Title, dialog.Title);
        Assert.Equal($"Save changes to {ModName} before you close?", dialog.Message);
        // Save is the first choice, so Enter keeps the edits.
        Assert.Equal(["Save", "Don't Save", "Cancel"], dialog.Choices.Select(c => c.Label).ToList());
    }

    /// <summary>A field edit or a drag that is open when the request comes ends first. Its change counts as an unsaved
    /// edit.</summary>
    [Fact]
    public void AnOpenFieldEditEndsBeforeTheCheck()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var host = new Host(session) { OpenEdit = () => Edit(session) };

        Assert.False(new ClosePrompt(host).Request());

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void ASaveThatSucceedsClosesTheWindow()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);
        Assert.False(new ClosePrompt(host).Request());

        host.Choose("Save");

        Assert.Equal(["end edits", "ask", "save", "close"], host.Calls);
        Assert.False(session.IsDirty);
        Assert.Contains("volume = 3", File.ReadAllText(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath)));
    }

    /// <summary>A save that fails keeps the window open. The Console of the editor tells why. The next request asks
    /// again.</summary>
    [Fact]
    public void ASaveThatFailsKeepsTheWindowOpen()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());
        // Something outside the editor changes the patch, so the save stops (rule P7).
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath), "[Good]\r\nnickname = g1\r\nprice = 130\r\n");

        host.Choose("Save");

        Assert.Equal(["end edits", "ask", "save"], host.Calls);
        Assert.True(session.IsDirty);
        Assert.False(prompt.Request());
        Assert.NotNull(host.Dialog);
        Assert.DoesNotContain("close", host.Calls);
    }

    /// <summary>Don't Save closes the window and deletes the recovery file, so the next open does not offer the
    /// edits. The files of the mod do not change.</summary>
    [Fact]
    public void DontSaveDeletesTheRecoveryFileAndClosesTheWindow()
    {
        using var ws = Workspace();
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        var session = ws.Session("act");
        Edit(session);
        Assert.Empty(session.WriteRecovery());
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));
        Assert.True(File.Exists(recovery));
        var host = new Host(session);
        Assert.False(new ClosePrompt(host).Request());

        host.Choose("Don't Save");

        Assert.Equal(["end edits", "ask", "discard", "close"], host.Calls);
        Assert.False(File.Exists(recovery));
        session.Dispose();
        Assert.False(ws.Session("act").HasRecovery);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act"), exclude: SaveTransaction.FolderName));
    }

    /// <summary>Cancel and Escape keep the window open and the edits unsaved. The next request asks again.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelAndEscapeKeepTheWindowOpen(bool escape)
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());

        if (escape)
            host.Escape();
        else
            host.Choose("Cancel");

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.True(session.IsDirty);
        Assert.False(prompt.Request());
        Assert.Equal(["end edits", "ask", "end edits", "ask"], host.Calls);
    }

    /// <summary>A second request while the dialog shows, for example a second click on the close button, changes
    /// nothing.</summary>
    [Fact]
    public void ASecondRequestWhileTheDialogShowsDoesNothing()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());
        var dialog = host.Dialog;

        Assert.False(prompt.Request());

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.Equal(dialog, host.Dialog);
    }

    /// <summary>A publish draft of the Mods window asks first (section 10.7 of the M11c design). After Apply, the
    /// question about the unsaved edits follows.</summary>
    [Fact]
    public void ADraftAsksBeforeTheUnsavedEdits()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session) { UnappliedDraft = "Starrunner" };
        var prompt = new ClosePrompt(host);

        Assert.False(prompt.Request());

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.Equal(PublishMessages.DraftQuestion("Starrunner"), host.Dialog!.Value.Message);
        Assert.Equal(["Apply", "Revert", "Cancel"], host.Dialog.Value.Choices.Select(c => c.Label).ToList());
        host.Choose("Apply");
        Assert.Equal(["end edits", "ask", "apply draft", "ask"], host.Calls);
        Assert.Equal($"Save changes to {ModName} before you close?", host.Dialog!.Value.Message);
        host.Choose("Don't Save");
        Assert.Equal(["end edits", "ask", "apply draft", "ask", "discard", "close"], host.Calls);
    }

    /// <summary>Revert drops the draft. Without unsaved edits, the window then closes.</summary>
    [Fact]
    public void RevertOfTheDraftClosesTheWindowWithoutUnsavedEdits()
    {
        using var ws = Workspace();
        var host = new Host(ws.Session("act")) { UnappliedDraft = "Starrunner" };

        Assert.False(new ClosePrompt(host).Request());
        host.Choose("Revert");

        Assert.Equal(["end edits", "ask", "revert draft", "close"], host.Calls);
    }

    /// <summary>Cancel and Escape keep the window open and the draft. The next request asks again.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelOfTheDraftQuestionKeepsTheWindowOpen(bool escape)
    {
        using var ws = Workspace();
        var host = new Host(ws.Session("act")) { UnappliedDraft = "Starrunner" };
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());

        if (escape)
            host.Escape();
        else
            host.Choose("Cancel");

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.Equal("Starrunner", host.UnappliedDraft);
        Assert.False(prompt.Request());
        Assert.Equal(["end edits", "ask", "end edits", "ask"], host.Calls);
    }

    /// <summary>An Apply that stops, for example after a refusal of the write guard, keeps the window open.</summary>
    [Fact]
    public void AnApplyThatStopsKeepsTheWindowOpen()
    {
        using var ws = Workspace();
        var host = new Host(ws.Session("act")) { UnappliedDraft = "Starrunner", ApplyWrites = false };
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());

        host.Choose("Apply");

        Assert.Equal(["end edits", "ask", "apply draft"], host.Calls);
        Assert.False(prompt.Request());
        Assert.DoesNotContain("close", host.Calls);
    }

    /// <summary>The editor window as the close prompt sees it, on a real edit session. The dialog is a record of its
    /// last call.</summary>
    private sealed class Host(EditSession session) : IClosePromptHost
    {
        public List<string> Calls { get; } = [];

        /// <summary>The display name of the mod of a publish draft with changes, or null.</summary>
        public string? UnappliedDraft { get; set; }

        /// <summary>False for an Apply that stops, for example after a refusal.</summary>
        public bool ApplyWrites { get; init; } = true;

        public void ApplyDraft(Action applied)
        {
            Calls.Add("apply draft");
            if (!ApplyWrites)
                return;
            UnappliedDraft = null;
            applied();
        }

        public void RevertDraft()
        {
            Calls.Add("revert draft");
            UnappliedDraft = null;
        }

        /// <summary>The open field edit or drag: its end makes the edit.</summary>
        public Action? OpenEdit { get; init; }

        private bool editOpen = true;

        /// <summary>The dialog that shows, or null.</summary>
        public (string Title, string Message, IReadOnlyList<Choice> Choices, Action Cancel)? Dialog { get; private set; }

        public string? UnsavedMod => session.IsDirty ? session.ActiveMod!.Manifest.Name : null;

        public void EndEdits()
        {
            Calls.Add("end edits");
            if (editOpen)
                OpenEdit?.Invoke();
            editOpen = false;
        }

        public void Save(Action saved)
        {
            Calls.Add("save");
            if (session.Save().Saved)
                saved();
        }

        public void DiscardRecovery()
        {
            Calls.Add("discard");
            session.DiscardRecovery();
        }

        public void Close() => Calls.Add("close");

        public void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel)
        {
            Calls.Add("ask");
            Dialog = (title, message, choices, cancel);
        }

        /// <summary>Clicks a button of the dialog. As in <see cref="ChoiceDialog"/>, the dialog closes first.</summary>
        public void Choose(string label)
        {
            var choice = Dialog!.Value.Choices.Single(c => c.Label == label);
            Dialog = null;
            choice.Action();
        }

        /// <summary>Presses Escape: the dialog closes and runs its cancel action.</summary>
        public void Escape()
        {
            var cancel = Dialog!.Value.Cancel;
            Dialog = null;
            cancel();
        }
    }
}
