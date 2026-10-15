using System.Linq;
using System.Numerics;
using LibreLancer;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The scene proof of the Inspector rows of step 5 on the retail Li01, without a GPU. A row edits its key as text: each
/// change of the text of a field applies <see cref="EditCommand.SetLinesFromText"/> in the transaction of the field,
/// and the end of the field commits it (FieldBinding.EditLines and LineGesture in the editor). The scene runs the
/// applier at each frame. So an edit of pos, archetype, size, color or atten_curve changes the scene while the field is
/// open, or the row shows the problem of the scene (SceneApplier.Problems with the key of the row).
/// </summary>
public sealed class InspectorEditRetailTests
{
    private const string Depot = "Li01_14_depot_water";
    private const string Death = "Zone_Li01_Manhattan_death";
    private const string ManhattanLight = "Light_Li01_Manhattan";

    static InspectorEditRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EachFieldChangesTheSceneOrShowsTheProblemOfItsRow()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m2proof");
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var depot = scene.Object(Depot);
        var death = scene.Zone(Death);
        var light = scene.Light(ManhattanLight);
        // A field edit is open while the user types, so the model of an archetype loads after the field ends.
        scene.Host.EditOpen = true;

        // pos: each keystroke moves the depot and its world object.
        Field(scene, session, scene.SectionOf(depot), "pos", "-37675, 0, -2770", "-37675, 0, -27705");
        Assert.Equal(new Vector3(-37675, 0, -27705), depot.Position);
        Assert.Equal(depot.Position, scene.Objects[depot].LocalTransform.Position);

        // archetype: a partial name names the problem for the archetype row, and the depot keeps its model. The full
        // name of an archetype applies at once, and the model loads after the field ends.
        var model = depot.Archetype;
        var section = scene.SectionOf(depot);
        var transaction = session.Begin("Set archetype");
        transaction.Apply(EditCommand.SetLinesFromText(section, "archetype", ["largestat"]));
        scene.Applier.Run();
        var problem = Assert.Single(scene.Applier.Problems(section));
        Assert.Equal("archetype", problem.Key);
        Assert.Contains("the archetype 'largestat' does not exist", problem.Message);
        Assert.Same(model, depot.Archetype);
        transaction.Apply(EditCommand.SetLinesFromText(section, "archetype", ["largestation1"]));
        scene.Applier.Run();
        Assert.Empty(scene.Applier.Problems(section));
        Assert.Equal("largestation1", depot.Archetype!.Nickname);
        Assert.True(scene.Applier.HasPending);
        transaction.Commit();
        scene.Host.EditOpen = false;
        scene.Applier.Run();
        Assert.False(scene.Applier.HasPending);
        scene.Host.EditOpen = true;

        // size: the sphere of the death zone gets its new radius on each axis.
        var moves = scene.Host.ZoneMoves;
        Field(scene, session, scene.SectionOf(death), "size", "5000");
        Assert.Equal(new Vector3(5000), death.Size);
        Assert.True(scene.Host.ZoneMoves > moves);

        // color: the renderer loads the lights again with the new color.
        var loads = scene.Host.LightLoads;
        Field(scene, session, scene.SectionOf(light), "color", "255, 0, 0");
        Assert.Equal(new Color3f(1, 0, 0), light.Light.Color);
        Assert.True(scene.Host.LightLoads > loads);

        // atten_curve: an unknown curve keeps the light and names the problem for the atten_curve row.
        var before = light.Light;
        Field(scene, session, scene.SectionOf(light), "atten_curve", "no_such", "no_such_curve");
        Assert.True(light.Light == before);
        problem = Assert.Single(scene.Applier.Problems(scene.SectionOf(light)));
        Assert.Equal("atten_curve", problem.Key);
        Assert.Contains("'no_such_curve'", problem.Message);
        // Each field edit is one entry of the history.
        Assert.Equal(5, CountEntries(session));
    }

    /// <summary>One field edit: a transaction with each text of the field, and a run of the scene for each frame.</summary>
    private static void Field(HeadlessScene scene, EditSession session, SectionRef section, string key,
        params string[] texts)
    {
        var transaction = session.Begin($"Set {key}");
        foreach (var text in texts)
        {
            transaction.Apply(EditCommand.SetLinesFromText(section, key, [text]));
            scene.Applier.Run();
        }

        transaction.Commit();
        scene.Applier.Run();
    }

    /// <summary>The number of entries of the history: each undo takes one.</summary>
    private static int CountEntries(EditSession session)
    {
        var count = 0;
        while (session.CanUndo)
        {
            session.Undo();
            count++;
        }

        for (var i = 0; i < count; i++)
            session.Redo();
        return count;
    }
}
