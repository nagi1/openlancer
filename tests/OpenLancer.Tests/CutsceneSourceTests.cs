using System.Collections.Generic;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The state of a cutscene in the Cutscenes tab without an open of the cutscene (section 7.2 of the M8 design): the
/// kind of the file of the active mod, as the document gives it after an open, the changes of the lower mods, and the
/// unsaved edits.
/// </summary>
public sealed class CutsceneSourceTests
{
    private const string NewPath = @"DATA\MODS\act\CUTSCENES\new.thn";

    private static readonly Dictionary<string, string> LowerPatch = new() { [PatchFile] = ModOverlayThnTests.FovPatch };

    [Fact]
    public void ACutsceneThatNoModChangesComesFromTheBaseFolder()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        Assert.Equal(new ThnFileSource(ThnFileKind.Lower, false), session.CutsceneSource(ScriptPath));
        // The source does not open the cutscene.
        Assert.Empty(session.OpenCutscenes);
        Assert.Equal(ThnFileKind.Lower, session.CutsceneKind(ScriptPath));
    }

    [Fact]
    public void APatchOfALowerModChangesTheCutscene()
    {
        using var ws = Workspace(lowFiles: LowerPatch);
        var session = ws.Session("act");

        Assert.Equal(new ThnFileSource(ThnFileKind.Lower, true), session.CutsceneSource(ScriptPath));
    }

    [Fact]
    public void WithoutAnActiveModEachModOfTheProfileIsALowerMod()
    {
        using var ws = Workspace(lowFiles: LowerPatch);
        var session = ws.Session(null);

        Assert.Equal(new ThnFileSource(ThnFileKind.Lower, true), session.CutsceneSource(ScriptPath));
    }

    [Fact]
    public void APatchOfTheActiveModIsPatchedHereAsTheDocumentHasIt()
    {
        using var ws = Workspace(new() { [PatchFile] = ModOverlayThnTests.FovPatch });
        var session = ws.Session("act");

        var source = session.CutsceneSource(ScriptPath);

        Assert.Equal(new ThnFileSource(ThnFileKind.PatchedHere, false), source);
        Assert.Equal(source.Kind, session.CutsceneKind(ScriptPath));
    }

    [Fact]
    public void AWholeFileOfTheActiveModReplacesALowerFileOrIsNew()
    {
        using var ws = Workspace(new() { [WholeFile] = Script, [@"files\" + NewPath] = Script });
        var session = ws.Session("act");

        Assert.Equal(new ThnFileSource(ThnFileKind.WholeFileHere, false), session.CutsceneSource(ScriptPath));
        Assert.Equal(new ThnFileSource(ThnFileKind.NewHere, false), session.CutsceneSource(NewPath));
        Assert.Equal(ThnFileKind.WholeFileHere, session.CutsceneKind(ScriptPath));
        Assert.Equal(ThnFileKind.NewHere, session.CutsceneKind(NewPath));
    }

    [Fact]
    public void AnEditIsUnsavedUntilASaveOrAnUndo()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Assert.False(session.HasUnsavedCutsceneEdits(ScriptPath));

        session.Execute(ThnEdits.SetDuration(ScriptPath, 12));
        Assert.True(session.HasUnsavedCutsceneEdits(ScriptPath));
        Assert.True(session.HasUnsavedCutsceneEdits(ScriptPath.ToLowerInvariant().Replace('\\', '/')));

        session.Undo();
        Assert.False(session.HasUnsavedCutsceneEdits(ScriptPath));

        session.Redo();
        Assert.True(session.Save().Saved);
        Assert.False(session.HasUnsavedCutsceneEdits(ScriptPath));
        // After the save, the active mod has a patch for the cutscene.
        Assert.Equal(ThnFileKind.PatchedHere, session.CutsceneKind(ScriptPath));
        Assert.Equal(new ThnFileSource(ThnFileKind.PatchedHere, false), session.CutsceneSource(ScriptPath));
    }
}
