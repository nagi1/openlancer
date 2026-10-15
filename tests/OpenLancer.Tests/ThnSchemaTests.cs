using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LibreLancer.Thn;
using LibreLancer.Thn.Events;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The key schema of cutscenes (test 13.1 item 14 of the M8 design): the place map of START_CAMERA_PROP_ANIM with its
/// evidence, the letter case of the user props, the bits of the flags keys, and the evidence of each row.
/// </summary>
public sealed class ThnSchemaTests
{
    private const string CameraAnim = "START_CAMERA_PROP_ANIM";

    /// <summary>
    /// START_CAMERA_PROP_ANIM reads its keys by place (StartCameraPropAnimEvent.cs:38-47). The rows say which value each
    /// key sets, each with its engine line, and the engine gives that value from the key.
    /// </summary>
    [Fact]
    public void TheCameraAnimationRowsMapAspectNearAndFarToTheNearPlaneTheFarPlaneAndTheAspect()
    {
        // The engine script below compiles with the native compiler, which ThnScripts loads.
        _ = Evaluator;
        (string Key, string Label, string Evidence)[] expected =
        [
            ("fovh", "fovh (sets the field of view)", "StartCameraPropAnimEvent.cs:42"),
            ("aspect", "aspect (sets the near plane)", "StartCameraPropAnimEvent.cs:43"),
            ("near", "near (sets the far plane)", "StartCameraPropAnimEvent.cs:45"),
            ("far", "far (sets the aspect)", "StartCameraPropAnimEvent.cs:47"),
        ];
        foreach (var (key, label, evidence) in expected)
        {
            var row = ThnSchema.Find(ThnElementKind.Event, CameraAnim, new ThnPath($"props.cameraprops.{key}"));
            Assert.NotNull(row);
            Assert.Equal((label, evidence, ThnKeyKind.Number, true), (row.Label, row.Evidence, row.Kind, row.Always));
        }

        // The rows of the schema list the four keys in this order, so the Inspector shows them so.
        Assert.Equal(["fovh", "aspect", "near", "far"], ThnSchema.RowsOf(ThnElementKind.Event, CameraAnim)
            .Where(r => r.Path.Text.StartsWith("props.cameraprops.", System.StringComparison.Ordinal) && r.Always)
            .Select(r => r.Path.Text["props.cameraprops.".Length..]));
        // The engine: aspect gives the near plane, near the far plane, and far the aspect.
        var script = new ThnScript(Encoding.ASCII.GetBytes("""
            duration = 1
            entities = { { entity_name = "C", type = CAMERA } }
            events = { { 0, START_CAMERA_PROP_ANIM, { "C" }, { duration = 1, cameraprops = { fovh = 1, aspect = 2, near = 3, far = 4 } } } }
            """), _ => null!, "anim.thn");
        var anim = Assert.IsType<StartCameraPropAnimEvent>(Assert.Single(script.Events));
        Assert.Equal((1f, 2f, 3f, 4f), (anim.FovH, anim.NearPlane, anim.FarPlane, anim.HVAspect));
    }

    /// <summary>The engine reads the user props without letter case (ThnScript.cs:305-314). A row matches each
    /// spelling, and the Inspector edits the spelling that the entity has: the last one, which the engine uses.</summary>
    [Fact]
    public void AUserPropsRowMatchesEachLetterCaseAndEditsTheLastSpelling()
    {
        var actor = ThnSchema.Find(ThnElementKind.Entity, "DEFORMABLE", new ThnPath("userprops.actor"));
        Assert.NotNull(actor);
        Assert.Equal("userprops.Actor", actor.Path.Text);
        Assert.Same(actor, ThnSchema.Find(ThnElementKind.Entity, "DEFORMABLE", new ThnPath("userprops.ACTOR")));
        var entity = Record(F("userprops", Record(F("Actor", T("a")), F("category", T("Char")), F("actor", T("b")))));
        Assert.Equal("userprops.actor", ThnSchema.KeyIn(actor, ThnElementKind.Entity, entity).Text);
        Assert.Equal("userprops.Actor", ThnSchema.KeyIn(actor, ThnElementKind.Entity, Record(F("type", S("DEFORMABLE")))).Text);

        var nofog = ThnSchema.Find(ThnElementKind.Entity, "COMPOUND", new ThnPath("userprops.NoFog"));
        Assert.NotNull(nofog);
        Assert.True(nofog.Read);
        var noFog = ThnSchema.Find(ThnElementKind.Entity, "COMPOUND", new ThnPath("userprops.No_Fog"));
        Assert.NotNull(noFog);
        Assert.False(noFog.Read);
        Assert.Equal("The engine reads nofog without letter case, so it does not read No_Fog.", noFog.Note);
        // TextStart has its letter case (ThnScript.cs:342).
        Assert.Null(ThnSchema.Find(ThnElementKind.Entity, "SCENE", new ThnPath("userprops.textstart")));
        Assert.NotNull(ThnSchema.Find(ThnElementKind.Entity, "SCENE", new ThnPath("userprops.TextStart")));
    }

    /// <summary>The bits of the flags rows are the values of the engine names, so a checkbox writes what the engine
    /// reads.</summary>
    [Fact]
    public void TheBitsOfTheFlagRowsAreTheValuesOfTheEngineNames()
    {
        foreach (var row in ThnSchema.Rows.Where(r => r.Kind == ThnKeyKind.Flags))
        {
            Assert.NotEmpty(row.Bits);
            foreach (var bit in row.Bits)
            {
                if (row.Group == null)
                    continue;
                var info = Evaluator.Symbols[bit.Name];
                Assert.Equal((row.Group, (long)bit.Bit), (info.Group, info.Value));
            }
        }

        Assert.Equal([2, 128], ThnSchema.Find(ThnElementKind.Event, "START_MOTION", new ThnPath("props.event_flags"))!
            .Bits.Select(b => b.Bit));
    }

    /// <summary>Each row names the engine file and the lines that read the key, and each row of a key that the engine
    /// does not read has a note.</summary>
    [Fact]
    public void EachRowNamesItsEvidenceAndEachUnreadKeyHasANote()
    {
        var evidence = new Regex(@"^[A-Za-z]+\.cs:\d+(-\d+)?(, (:\d+(-\d+)?|[A-Za-z]+\.cs:\d+(-\d+)?))*$");
        Assert.All(ThnSchema.Rows, r => Assert.Matches(evidence, r.Evidence));
        Assert.All(ThnSchema.Rows.Where(r => !r.Read), r => Assert.False(string.IsNullOrEmpty(r.Note)));
        Assert.All(ThnSchema.Rows.Where(r => r.Element == ThnElementKind.Event),
            r => Assert.StartsWith("props.", r.Path.Text));
    }

    /// <summary>
    /// Each entity type and each event type has one row for a key, so the Inspector draws each key once, and no two rows
    /// of a page have one ImGui id. The row of a type for a path is the row that <see cref="ThnSchema.Find"/> gives, also
    /// for the spellings of a user prop. A key that some types always show has a row that shows only when the element
    /// has the key for the other types.
    /// </summary>
    [Fact]
    public void EachTypeHasOneRowForEachKey()
    {
        foreach (var (kind, group) in new[] { (ThnElementKind.Entity, "EntityTypes"), (ThnElementKind.Event, "EventTypes") })
        {
            var types = Evaluator.Symbols.Where(s => s.Value.Group == group).Select(s => (string?)s.Key).Append(null).ToList();
            Assert.True(types.Count > 10, group);
            foreach (var type in types)
            {
                foreach (var row in ThnSchema.RowsOf(kind, type))
                    Assert.True(ReferenceEquals(row, ThnSchema.Find(kind, type, row.Path)), $"{kind} {type}: {row.Path} has two rows.");
            }
        }

        foreach (var (type, key, always) in new[]
                 {
                     ("MONITOR", "userprops.Priority", true), ("CAMERA", "userprops.Priority", false),
                     ("COMPOUND", "userprops.nofog", true), ("LIGHT", "userprops.NoFog", false),
                 })
        {
            var row = Assert.Single(ThnSchema.RowsOf(ThnElementKind.Entity, type),
                r => r.Path.Text.Equals(key, System.StringComparison.OrdinalIgnoreCase));
            Assert.Equal(always, row.Always);
        }
    }

    /// <summary>
    /// The engine cannot load the <c>orient</c> key of a START_SPATIAL_PROP_ANIM: it reads the rows of the matrix from
    /// index 0 (ThnTypes.cs:80-92). The row says so, with that evidence. When a change of the engine loads the key, this
    /// test fails, and the row needs a review.
    /// </summary>
    [Fact]
    public void TheOrientRowOfASpatialAnimationSaysThatTheEngineCannotLoadIt()
    {
        _ = Evaluator;
        var row = ThnSchema.Find(ThnElementKind.Event, "START_SPATIAL_PROP_ANIM", new ThnPath("props.spatialprops.orient"))!;
        Assert.Contains("ThnTypes.cs:80-92", row.Evidence);
        Assert.StartsWith("The engine cannot load a script with this key.", row.Note);
        Assert.False(row.Always);

        var orient = Encoding.ASCII.GetBytes("""
            duration = 1
            entities = { { entity_name = "A", type = MARKER } }
            events = { { 0, START_SPATIAL_PROP_ANIM, { "A" }, { duration = 1, spatialprops = { orient = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } } } } }
            """);
        Assert.ThrowsAny<System.Exception>(() => new ThnScript(orient, _ => null!, "orient.thn"));
        var quaternion = Encoding.ASCII.GetBytes("""
            duration = 1
            entities = { { entity_name = "A", type = MARKER } }
            events = { { 0, START_SPATIAL_PROP_ANIM, { "A" }, { duration = 1, spatialprops = { q_orient = { 1, 0, 0, 0 } } } } }
            """);
        Assert.Single(new ThnScript(quaternion, _ => null!, "q_orient.thn").Events);
    }

    /// <summary>A row applies to the types that it lists. The schema knows the type of an element as a name or as a
    /// number.</summary>
    [Fact]
    public void ARowAppliesToItsTypesAndTheTypeOfAnElementIsAName()
    {
        Assert.NotNull(ThnSchema.Find(ThnElementKind.Entity, "CAMERA", new ThnPath("cameraprops.fovh")));
        Assert.Null(ThnSchema.Find(ThnElementKind.Entity, "LIGHT", new ThnPath("cameraprops.fovh")));
        Assert.Null(ThnSchema.Find(ThnElementKind.Entity, "MONITOR", new ThnPath("spatialprops.pos")));
        Assert.Equal("CAMERA", ThnSchema.TypeOf(ThnElementKind.Entity, Record(F("type", N(3))), Evaluator.Symbols));
        Assert.Equal("SET_CAMERA", ThnSchema.TypeOf(ThnElementKind.Event, List(N(0), N(2), List()), Evaluator.Symbols));
        Assert.Null(ThnSchema.TypeOf(ThnElementKind.Entity, Record(F("type", S("SET_CAMERA"))), Evaluator.Symbols));

        var on = ThnSchema.Find(ThnElementKind.Entity, "LIGHT", new ThnPath("lightprops.on"))!;
        Assert.True(ThnSchema.Fits(on, new ThnBool(true), Evaluator.Symbols));
        Assert.True(ThnSchema.Fits(on, N(0), Evaluator.Symbols));
        Assert.False(ThnSchema.Fits(on, T("Y"), Evaluator.Symbols));
        var type = ThnSchema.Find(ThnElementKind.Entity, "LIGHT", new ThnPath("lightprops.type"))!;
        Assert.True(ThnSchema.Fits(type, S("L_SPOT"), Evaluator.Symbols));
        Assert.False(ThnSchema.Fits(type, S("CAMERA"), Evaluator.Symbols));
    }
}
