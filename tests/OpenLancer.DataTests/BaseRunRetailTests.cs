using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Resources;
using OpenLancer.Data;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The base run gives the same engine lines as the editor (test 9 of the console filter design): the data load of the
/// edit view of a profile, with a <see cref="ServerResourceManager"/> as the audit harness uses, gives the same key
/// counts as the load part of <see cref="BaseEngineLines"/>. So a mod that changes nothing gives no mod line.
/// </summary>
public sealed class BaseRunRetailTests(ITestOutputHelper output)
{
    static BaseRunRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheEditorLoadOfTheRetailGameGivesTheKeysOfTheBaseRun() => AssertSameKeys(GameDataPaths.RequireBase());

    /// <summary>The test needs <c>OPENLANCER_HDE</c>, a copy of an HD Edition install. It only reads it, and it skips
    /// without it.</summary>
    [Fact]
    public void TheEditorLoadOfHdEditionGivesTheKeysOfTheBaseRun() => AssertSameKeys(GameDataPaths.RequireHde());

    private void AssertSameKeys(string folder)
    {
        var run = BaseEngineLines.Run(folder);
        using var workspace = new RetailWorkspace(folder, "base-run");
        var session = workspace.Open();

        var editor = EditorLoad(session.EditView.CreateFileSystem());

        output.WriteLine($"{editor.Count} lines of the editor load, {run.Load.Count} of the base run.");
        Assert.NotEmpty(editor);
        Assert.Equal(Sorted(BaseEngineLines.Count(run.Load)), Sorted(BaseEngineLines.Count(editor)));
    }

    /// <summary>The engine lines of the data load of the editor (EditorWindow.OpenNow), without a window.</summary>
    internal static IReadOnlyList<EngineLine> EditorLoad(LibreLancer.Data.IO.FileSystem fs)
    {
        using var capture = EngineLogCapture.Start();
        var resources = new ServerResourceManager(null, fs);
        // The resource manager is not disposable, but its physics shape pool is.
        using var shapes = resources.ConvexCollection;
        new GameDataManager(new GameItemDb(fs), resources).LoadData(null);
        return capture.StopLines();
    }

    private static IEnumerable<KeyValuePair<string, int>> Sorted(IReadOnlyDictionary<string, int> counts) =>
        counts.OrderBy(c => c.Key, System.StringComparer.Ordinal);
}
