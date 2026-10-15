using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Thn;
using OpenLancer.Data;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The THN branch of the overlay merge: the evaluator reads the lower script, each mod's THN patch applies in load
/// order, and the overlay serves the canonical text. A patch with an error does not apply.
/// </summary>
public sealed class ModOverlayThnTests
{
    internal const string ScriptPath = @"DATA\SCRIPTS\x.thn";
    internal const string PatchFile = @"patches\DATA\SCRIPTS\x.thn";

    internal const string Script = """
        duration = 10
        entities = {
            { entity_name = "Cam_A", type = CAMERA, cameraprops = { fovh = 10, hvaspect = 1.5, nearplane = 1, farplane = 100 } },
            { entity_name = "Monitor", type = MONITOR }
        }
        events = {
            { 0, SET_CAMERA, { "Monitor", "Cam_A" } },
            { 5, START_PSYS, { "Fx" }, { duration = 2 } },
            { 5, START_PSYS, { "Fx" }, { duration = 3 } }
        }
        """;

    internal const string FovPatch = """
        -- A wider shot.
        ol_thn_patch = 1
        entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20 } } }
        """;

    /// <summary>Deletes the first of the two events with one key and adds a new event with that key. The count of the
    /// key stays 2, but its elements change.</summary>
    private const string RegroupPatch = """
        ol_thn_patch = 1
        events = {
            { ol_match = { 5, START_PSYS, { "Fx" } }, ol_nth = 1, ol_count = 2, ol_delete = Y },
            { ol_new = { 5, START_PSYS, { "Fx" }, { duration = 8 } } }
        }
        """;

    /// <summary>Sets a path of the first of the two events with one key, by its place.</summary>
    private const string PlacePatch = """
        ol_thn_patch = 1
        events = { { ol_match = { 5, START_PSYS, { "Fx" } }, ol_nth = 1, ol_count = 2, ol_set = { ["props.duration"] = 4 } } }
        """;

    private static TestWorkspace Workspace(string? script = Script)
    {
        var ws = new TestWorkspace();
        if (script != null)
            ws.WriteBase(ScriptPath, script);
        return ws;
    }

    /// <summary>The merged text of the script, as the game reads it through the file system.</summary>
    internal static string Merged(ModOverlay overlay)
    {
        using var stream = overlay.CreateFileSystem().Open(ScriptPath);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    internal static string Canonical(string script) => ThnWriter.Write(Evaluate(script));

    private static void AssertNoProblems(ModOverlay overlay) => Assert.True(overlay.Diagnostics.Count == 0,
        string.Join("\n", overlay.Diagnostics));

    [Fact]
    public void APatchMergesAndTheEngineReadsTheText()
    {
        using var ws = Workspace();

        var overlay = ws.Build(ws.Mod("m", new() { [PatchFile] = FovPatch }));

        AssertNoProblems(overlay);
        Assert.Equal(Canonical(Script.Replace("fovh = 10", "fovh = 20")), Merged(overlay));
        var file = overlay.Files.Single(f => f.Path == ScriptPath);
        Assert.Null(file.DiskPath);
        // The engine reads the merged text as it reads bytecode (ThornRuntime.cs:49-59).
        var script = new ThnScript(file.ReadAllBytes(), include => throw new FileNotFoundException(include), ScriptPath);
        Assert.Equal(20, script.Entities["Cam_A"].FovH);
    }

    [Fact]
    public void ModLoadAcceptsThnPatchesAndRefusesOtherPatches()
    {
        using var ws = Workspace();
        var mod = ws.Mod("m", new() { [PatchFile] = FovPatch, [@"patches\DATA\goods.ini"] = "[Good]\nnickname = a" });

        Assert.Equal([@"DATA\goods.ini", ScriptPath], mod.Patches.Keys.Order());
        var e = Assert.Throws<ModLoadException>(() => ws.Mod("bad", new() { [@"patches\DATA\SCRIPTS\x.txt"] = "x" }));
        Assert.EndsWith(@"x.txt: A patch must be an .ini file or a .thn file.", e.Message);
    }

    /// <summary>A patch of a later mod changes the whole file of an earlier mod.</summary>
    [Fact]
    public void APatchChangesTheWholeFileOfAnEarlierMod()
    {
        using var ws = Workspace();
        var whole = Script.Replace("duration = 10", "duration = 12");

        var overlay = ws.Build(ws.Mod("a", new() { [@"files\" + ScriptPath] = whole }), ws.Mod("b", new() { [PatchFile] = FovPatch }));

        AssertNoProblems(overlay);
        Assert.Equal(Canonical(whole.Replace("fovh = 10", "fovh = 20")), Merged(overlay));
    }

    /// <summary>A whole file of a later mod discards the patches before it, with a warning, and the overlay serves the
    /// file as it is.</summary>
    [Fact]
    public void AWholeFileDiscardsThePatchesBeforeIt()
    {
        using var ws = Workspace();
        var whole = Script.Replace("duration = 10", "duration = 12");

        var overlay = ws.Build(ws.Mod("a", new() { [PatchFile] = FovPatch }), ws.Mod("b", new() { [@"files\" + ScriptPath] = whole }));

        var warning = Assert.Single(overlay.Diagnostics);
        Assert.Equal($"Mod 'b' replaces {ScriptPath}. The changes of mod 'a' are lost.", warning.Message);
        Assert.Equal(Path.Combine(ws.ModFolder("b"), "files", ScriptPath), overlay.Files.Single(f => f.Path == ScriptPath).DiskPath);
    }

    /// <summary>A patch after a whole file changes that file. The changes of the patches before the whole file are
    /// lost.</summary>
    [Fact]
    public void APatchAfterAWholeFileChangesOnlyThatFile()
    {
        using var ws = Workspace();
        var whole = Script.Replace("duration = 10", "duration = 12");
        const string aspectPatch = """
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.hvaspect"] = 2 } } }
            """;

        var overlay = ws.Build(ws.Mod("a", new() { [PatchFile] = FovPatch }), ws.Mod("b", new() { [@"files\" + ScriptPath] = whole }),
            ws.Mod("c", new() { [PatchFile] = aspectPatch }));

        Assert.Equal([$"Mod 'b' replaces {ScriptPath}. The changes of mod 'a' are lost."], overlay.Diagnostics.Select(d => d.Message));
        Assert.Equal(Canonical(whole.Replace("hvaspect = 1.5", "hvaspect = 2")), Merged(overlay));
    }

    /// <summary>A whole file discards the changed groups of the patches before it (rule 7). So a later entry with
    /// ol_nth for such a key gets no warning.</summary>
    [Fact]
    public void AWholeFileDiscardsTheChangedGroupsOfThePatchesBeforeIt()
    {
        using var ws = Workspace();
        var whole = Script.Replace("duration = 10", "duration = 12");

        var overlay = ws.Build(ws.Mod("a", new() { [PatchFile] = RegroupPatch }), ws.Mod("b", new() { [@"files\" + ScriptPath] = whole }),
            ws.Mod("c", new() { [PatchFile] = PlacePatch }));

        Assert.Equal([$"Mod 'b' replaces {ScriptPath}. The changes of mod 'a' are lost."], overlay.Diagnostics.Select(d => d.Message));
        Assert.Equal(Canonical(whole.Replace("{ duration = 2 }", "{ duration = 4 }")), Merged(overlay));
    }

    /// <summary>Rule 4: two mods that set the same path give a warning that names both mods. The later mod wins.</summary>
    [Fact]
    public void TwoModsThatSetOnePathGiveAWarning()
    {
        using var ws = Workspace();

        var overlay = ws.Build(ws.Mod("a", new() { [PatchFile] = FovPatch }),
            ws.Mod("b", new() { [PatchFile] = FovPatch.Replace("= 20", "= 30") }));

        var warning = Assert.Single(overlay.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(Path.Combine(ws.ModFolder("b"), PatchFile), warning.Source);
        Assert.Equal("entity \"Cam_A\": 'cameraprops.fovh' was also changed by mod 'a'. Mod 'b' loads later and wins.",
            warning.Message);
        Assert.Equal(Canonical(Script.Replace("fovh = 10", "fovh = 30")), Merged(overlay));
    }

    /// <summary>Rule 4 compares the paths of one element. The same path of two elements is not a conflict.</summary>
    [Fact]
    public void TwoModsThatSetOnePathOfTwoEntitiesGiveNoWarning()
    {
        using var ws = Workspace();
        const string patch = """
            ol_thn_patch = 1
            entities = { { ol_match = "NAME", ol_set = { ["spatialprops.pos"] = { 1, 2, 3 } } } }
            """;

        var overlay = ws.Build(ws.Mod("a", new() { [PatchFile] = patch.Replace("NAME", "Cam_A") }),
            ws.Mod("b", new() { [PatchFile] = patch.Replace("NAME", "Monitor") }));

        AssertNoProblems(overlay);
        Assert.Equal(Canonical(Script.Replace("farplane = 100 } }", "farplane = 100 }, spatialprops = { pos = { 1, 2, 3 } } }")
            .Replace("type = MONITOR }", "type = MONITOR, spatialprops = { pos = { 1, 2, 3 } } }")), Merged(overlay));
    }

    /// <summary>Rule 4: a later mod that sets a path in a record that an earlier mod set gives a warning that names both
    /// mods. The later mod wins.</summary>
    [Fact]
    public void ASetInARecordThatAnEarlierModSetGivesAWarning()
    {
        using var ws = Workspace();
        const string recordPatch = """
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { spatialprops = { pos = { 1, 2, 3 } } } } }
            """;
        const string pathPatch = """
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { ["spatialprops.pos"] = { 4, 5, 6 } } } }
            """;

        var overlay = ws.Build(ws.Mod("a", new() { [PatchFile] = recordPatch }), ws.Mod("b", new() { [PatchFile] = pathPatch }));

        var warning = Assert.Single(overlay.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("entity \"Cam_A\": 'spatialprops.pos' overlaps 'spatialprops', which mod 'a' also changed. Mod 'b' " +
                     "loads later and wins.", warning.Message);
        Assert.Equal(Canonical(Script.Replace("farplane = 100 } }", "farplane = 100 }, spatialprops = { pos = { 4, 5, 6 } } }")),
            Merged(overlay));
    }

    /// <summary>Rule 4 for the duration: two mods that set it give a warning that names both mods. The later mod
    /// wins.</summary>
    [Fact]
    public void TwoModsThatSetTheDurationGiveAWarning()
    {
        using var ws = Workspace();

        var overlay = ws.Build(ws.Mod("a", new() { [PatchFile] = "ol_thn_patch = 1\nduration = 11" }),
            ws.Mod("b", new() { [PatchFile] = "ol_thn_patch = 1\nduration = 12" }));

        var warning = Assert.Single(overlay.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(Path.Combine(ws.ModFolder("b"), PatchFile), warning.Source);
        Assert.Equal("'duration' was also changed by mod 'a'. Mod 'b' loads later and wins.", warning.Message);
        Assert.Equal(Canonical(Script.Replace("duration = 10", "duration = 12")), Merged(overlay));
    }

    /// <summary>Rule 7: mod 1 deletes the first of two events with one key and adds an event with that key. Mod 2 sets
    /// a path of place 1 of 2. The patch applies, with one warning that names both mods.</summary>
    [Fact]
    public void AChangedGroupGivesAWarningThatNamesBothMods()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("mod1", new() { [PatchFile] = RegroupPatch }), ws.Mod("mod2", new() { [PatchFile] = PlacePatch }));

        var warning = Assert.Single(overlay.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("Mod 'mod1' changed the events with the key { 5, START_PSYS, { \"Fx\" } }. The events entry 1 of mod " +
                     "'mod2' uses place 1 of 2. Check the result.", warning.Message);
        Assert.Equal(Canonical(Script.Replace("{ 5, START_PSYS, { \"Fx\" }, { duration = 2 } },", "")
            .Replace("{ duration = 3 } }", "{ duration = 4 } },\n{ 5, START_PSYS, { \"Fx\" }, { duration = 8 } }")), Merged(overlay));
    }

    /// <summary>Rule 2: a patch with an error does not apply. The script stays as the mods before it leave it, and the
    /// later patches apply to that script.</summary>
    [Fact]
    public void APatchWithAnErrorDoesNotApply()
    {
        using var ws = Workspace();
        var broken = ws.Mod("broken", new()
        {
            [PatchFile] = """
                ol_thn_patch = 1
                entities = {
                    { ol_match = "Monitor", ol_delete = Y },
                    { ol_match = "Cam_X", ol_set = { ["cameraprops.fovh"] = 20 } }
                }
                """,
        });

        var alone = ws.Build(broken);
        var withLater = ws.Build(broken, ws.Mod("later", new() { [PatchFile] = FovPatch }));

        var error = Assert.Single(alone.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(Path.Combine(ws.ModFolder("broken"), PatchFile), error.Source);
        Assert.Equal("entities entry 2: no entity has the key \"Cam_X\".", error.Message);
        // No patch applied, so the overlay has no file at the path, and the base file shows.
        Assert.DoesNotContain(alone.Files, f => f.Path == ScriptPath);
        Assert.Equal(Script.Replace("\r\n", "\n"), Merged(alone).Replace("\r\n", "\n"));
        Assert.Equal(Canonical(Script.Replace("fovh = 10", "fovh = 20")), Merged(withLater));
    }

    /// <summary>The ledger records only the changes of a patch that applied. So a later mod that changes the same path
    /// as a patch with an error gets no warning (rule 4).</summary>
    [Fact]
    public void APatchWithAnErrorGivesNoConflictWithALaterMod()
    {
        using var ws = Workspace();
        // The first entry sets a value. The second entry is an error, so the patch does not apply.
        var broken = ws.Mod("broken", new()
        {
            [PatchFile] = """
                ol_thn_patch = 1
                entities = {
                    { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 15 } },
                    { ol_match = "Monitor", ol_set = { ["entity_name.x"] = 1 } }
                }
                """,
        });

        var overlay = ws.Build(broken, ws.Mod("later", new() { [PatchFile] = FovPatch }));

        var error = Assert.Single(overlay.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(Path.Combine(ws.ModFolder("broken"), PatchFile), error.Source);
        Assert.StartsWith("entities entry 2: ", error.Message);
        Assert.Equal(Canonical(Script.Replace("fovh = 10", "fovh = 20")), Merged(overlay));
    }

    [Fact]
    public void WithoutAnEvaluatorAThnPatchIsAnError()
    {
        using var ws = Workspace();

        var overlay = ModOverlay.Build(ws.Base, [ws.Mod("m", new() { [PatchFile] = FovPatch })], TestWorkspace.FakeDllWriter.Instance,
            thn: null);

        var error = Assert.Single(overlay.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("This tool cannot merge cutscene patches.", error.Message);
    }

    [Theory]
    [MemberData(nameof(BrokenInputs))]
    public void APatchOfAFileThatCannotBeReadIsAnError(string? script, string patch, string message)
    {
        using var ws = Workspace(script);

        var overlay = ws.Build(ws.Mod("m", new() { [PatchFile] = patch }));

        Assert.Equal([message], overlay.Diagnostics.Select(d => d.Message));
        Assert.True(overlay.HasErrors);
    }

    public static TheoryData<string?, string, string> BrokenInputs() => new()
    {
        { null, FovPatch, $"The file to patch, {ScriptPath}, does not exist." },
        { "duration = 1\nentities = 5\nevents = {}", FovPatch, "Cannot read the cutscene to patch: The global 'entities' must be a list of tables." },
        { "duration = 1e39", FovPatch, $"Cannot read the cutscene to patch: {ScriptPath}: The value at duration is Infinity. A THN number must be finite." },
        { Script, "entities = {}", "The first statement of a THN patch must be 'ol_thn_patch = 1'." },
    };

    /// <summary>A path without a THN patch keeps its file: a whole file of a mod is served from the disk, without an
    /// evaluation.</summary>
    [Fact]
    public void AWholeFileWithoutAPatchIsServedFromTheDisk()
    {
        using var ws = Workspace();

        var overlay = ModOverlay.Build(ws.Base, [ws.Mod("a", new() { [@"files\" + ScriptPath] = "not lua" })],
            TestWorkspace.FakeDllWriter.Instance, thn: null);

        AssertNoProblems(overlay);
        Assert.Equal("not lua", Merged(overlay));
    }
}
