using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LibreLancer;
using LibreLancer.Thn;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;
using static OpenLancer.DataTests.ThnPatchRetailTests;

namespace OpenLancer.DataTests;

/// <summary>
/// The core loop of the cutscene editor on retail data: the edits of the manual check A on s002xe through
/// <see cref="ThnEdits"/>, then a save into a temporary mod. The patch has the two entries, the merge gives W, and the
/// history works across the saves.
/// </summary>
public sealed class ThnEndToEndRetailTests
{
    private const string Camera = "Cam_02_Intro_Trent_A";
    private const string Monitor = "Cam_Monitor_s002xe";
    private const string Medics = "Cam_03_ACOH_Trent_Medics_A";

    static ThnEndToEndRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void AnEditOfS002xeSavesAPatchWithTwoEntriesAndUndoDeletesIt()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var baseFile = Path.Combine(baseFolder, S002xe);
        var hashBefore = SHA256.HashData(File.ReadAllBytes(baseFile));
        using var ws = new RetailWorkspace(baseFolder, "m8proof");
        var session = ws.Open();
        var lower = session.Cutscene(S002xe)!;
        Assert.Equal(ThnFileKind.Lower, session.CutsceneKind(S002xe));
        var camera = new ThnRef(S002xe, ThnElementKind.Entity,
            lower.Entities.Single(e => e.Table["entity_name"] == new ThnText(Camera)).Id);
        var cut = new ThnRef(S002xe, ThnElementKind.Event, lower.Events.Single(e =>
            e.Table.List[0] == new ThnNumber(25.75f) && e.Table.List[2] == ThnTable.FromList(new ThnText(Monitor), new ThnText(Medics))).Id);
        var fovh = new ThnPath("cameraprops.fovh");
        Assert.Equal(new ThnNumber(10), ThnElementPaths.Get(ThnElementKind.Entity,
            lower.Entities.Single(e => e.Id == camera.Id).Table, fovh));

        session.Execute(ThnEdits.MoveEvents([cut], 1.25f, 0));
        session.Execute(ThnEdits.Set(camera, fovh, new ThnNumber(20)));
        var w = session.Cutscene(S002xe)!;
        var first = session.Save();

        Assert.True(first.Saved, string.Join("\n", first.Problems));
        Assert.Empty(first.Problems);
        var patchFile = Assert.Single(first.Written);
        var bytes = File.ReadAllBytes(patchFile);
        TestContext.Current.TestOutputHelper?.WriteLine($"{patchFile} ({bytes.Length} bytes):\n{System.Text.Encoding.ASCII.GetString(bytes)}");
        var patch = ThnPatch.Read(bytes, patchFile, ThornEvaluator.Instance);
        Assert.Equal(2, patch.Entities.Length + patch.Events.Length);
        var entity = Assert.Single(patch.Entities);
        Assert.Equal(new ThnText(Camera), entity.Match);
        Assert.Equal(new[] { (fovh, (ThnValue)new ThnNumber(20)) }, entity.Sets);
        var ev = Assert.Single(patch.Events);
        Assert.Equal(new ThnNumber(25.75f), ((ThnTable)ev.Match!).List[0]);
        Assert.Equal(new[] { (new ThnPath("time"), (ThnValue)new ThnNumber(27)) }, ev.Sets);
        Assert.Null(patch.Duration);

        // The merged file of the edit view evaluates to W, bit for bit and in key order, and the engine reads it.
        var merged = session.EditView.CreateFileSystem().ReadAllBytes(S002xe);
        Assert.Null(ThnValues.FirstDifference(w.ToGlobals(), ThornEvaluator.Instance.Evaluate(merged, S002xe)));
        var script = new ThnScript(merged, include => throw new FileNotFoundException(include), S002xe);
        Assert.Equal(20, script.Entities[Camera].FovH);
        Assert.Equal(27, Assert.Single(script.Events, e => e.Type == EventTypes.SetCamera && e.Targets is [Monitor, Medics]).Time);
        // W after the save is W before it, with the same ids.
        Assert.Equal(w, session.Cutscene(S002xe));
        Assert.Equal(ThnFileKind.PatchedHere, session.CutsceneKind(S002xe));

        session.Undo();
        session.Undo();
        var undone = session.Save();
        Assert.True(undone.Saved, string.Join("\n", undone.Problems));
        Assert.Equal([patchFile], undone.Deleted);
        Assert.False(File.Exists(patchFile));
        Assert.Equal(lower, session.Cutscene(S002xe));

        session.Redo();
        session.Redo();
        var redone = session.Save();
        Assert.True(redone.Saved, string.Join("\n", redone.Problems));
        Assert.Equal(bytes, File.ReadAllBytes(patchFile));
        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(baseFile)));
    }

    /// <summary>A change of a cutscene lists the cutscene and no section, so the scene of a system has nothing to catch
    /// up. Without the list of cutscenes, the change would look like a restore, which catches up the whole
    /// system.</summary>
    [Fact]
    public void ACutsceneEditGivesTheSystemSceneNothingToDo()
    {
        using var ws = new RetailWorkspace(GameDataPaths.RequireBase(), "m8proof");
        var session = ws.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        Assert.False(scene.Applier.HasPending);
        var camera = session.Cutscene(S002xe)!.Entities.Single(e => e.Table["entity_name"] == new ThnText(Camera));

        session.Execute(ThnEdits.Set(new ThnRef(S002xe, ThnElementKind.Entity, camera.Id), new ThnPath("cameraprops.fovh"),
            new ThnNumber(20)));
        Assert.False(scene.Applier.HasPending);
        session.Undo();
        Assert.False(scene.Applier.HasPending);
    }
}
