using System;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the read-only scene sections (section 12 of the design): St02c is the only retail system file with a
/// repeated nickname. A patch finds a section by its type and key, so the editor cannot change those sections, and the
/// gizmo has no target for them.
/// </summary>
public sealed class SceneReadOnlyRetailTests
{
    private const string Repeated = "light_lair_core_lower";

    static SceneReadOnlyRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheRepeatedLightsOfSt02cAreReadOnlyAndHaveNoGizmo()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var lights = session.Sections(session.SystemPath("St02c")!)
            .Where(s => s.Id.Type.Equals("LightSource", StringComparison.OrdinalIgnoreCase)).ToList();
        var repeated = lights.Where(s => string.Equals(s.Id.Key, Repeated, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(9, lights.Count);
        Assert.Equal(8, repeated.Count);

        const string reason = $"another [LightSource] section also has nickname = {Repeated}.";
        foreach (var light in repeated)
        {
            Assert.Equal(reason, session.Get(light)!.ReadOnlyReason);
            Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.SetLines(light, "pos",
                [[new Int32Value(0), new Int32Value(0), new Int32Value(0)]])));
            Assert.Null(GizmoTarget.Of(session, light, null, out var why));
            Assert.Equal(reason, why);
        }

        // The other light of St02c takes the move tool.
        var other = GizmoTarget.Of(session, Assert.Single(lights.Except(repeated)), null, out var none);
        Assert.NotNull(other);
        Assert.Null(none);
        Assert.Null(other.Tools[GizmoTool.Move]);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }
}
