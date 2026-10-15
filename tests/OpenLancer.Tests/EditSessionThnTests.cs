using System.Collections.Generic;
using System.IO;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;
using static OpenLancer.Tests.ModOverlayThnTests;

namespace OpenLancer.Tests;

/// <summary>
/// An edit session on a profile whose mods have THN patches. The session reads the cutscene scripts with the
/// evaluator, so its merged views have no error, and the session takes edits.
/// </summary>
public sealed class EditSessionThnTests
{
    private const string AspectPatch = """
        ol_thn_patch = 1
        entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.hvaspect"] = 2 } } }
        """;

    /// <summary>A workspace with the base files of <see cref="EditSessionTests"/> and the cutscene script, a lower mod
    /// "low" and an active mod "act" with these files, and the profile.</summary>
    private static TestWorkspace Workspace(Dictionary<string, string> lowFiles, Dictionary<string, string> activeFiles)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        ws.WriteBase(ScriptPath, Script);
        ws.Mod("low", lowFiles);
        ws.Mod("act", activeFiles);
        ws.WriteProfile("low", "act");
        return ws;
    }

    private static void AssertSaved(SaveResult result)
    {
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Empty(result.Problems);
    }

    /// <summary>A lower mod and the active mod patch one cutscene. The session takes edits, and the edit view shows the
    /// cutscene with both patches, also after a save.</summary>
    [Fact]
    public void ASessionMergesTheThnPatchesOfTheLowerModsAndTheActiveMod()
    {
        using var ws = Workspace(new() { [PatchFile] = FovPatch }, new() { [PatchFile] = AspectPatch });
        var expected = Canonical(Script.Replace("fovh = 10", "fovh = 20").Replace("hvaspect = 1.5", "hvaspect = 2"));
        var patch = File.ReadAllBytes(Path.Combine(ws.ModFolder("act"), PatchFile));
        var session = ws.Session("act");

        Assert.Null(session.ReadOnlyReason);
        Assert.Empty(session.EditView.Diagnostics);
        Assert.Equal(expected, Merged(session.EditView));

        session.Execute(Set(Good("g1"), "price", new Int32Value(5)));
        AssertSaved(session.Save());

        // The save builds the edit view again, and keeps the THN patch.
        Assert.Empty(session.EditView.Diagnostics);
        Assert.Equal(expected, Merged(session.EditView));
        Assert.Equal(patch, File.ReadAllBytes(Path.Combine(ws.ModFolder("act"), PatchFile)));
    }

    /// <summary>
    /// The merge does not resolve text placeholders in a THN patch. So a text in a THN patch that looks like a
    /// placeholder is not a use of the text, and a save does not ask for it (rule S8).
    /// </summary>
    [Fact]
    public void ATextInAThnPatchIsNotAPlaceholder()
    {
        // An INI parser reads the line that starts with "[" as a section, and the comment as a line with the value
        // $str:x.
        const string patch = """
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = {
                ["userprops.note"] = "a"
                -- TextString = $str:x
            } } }
            """;
        using var ws = Workspace(new(), new() { [PatchFile] = patch });
        var session = ws.Session("act");
        Assert.Empty(session.EditView.Diagnostics);

        Assert.Empty(session.TextUses());
        session.Execute(EditCommand.SetText(StringKind.String, "extra", "Extra"));
        AssertSaved(session.Save());
        Assert.True(File.Exists(Path.Combine(ws.ModFolder("act"), "strings.json")));
    }
}
