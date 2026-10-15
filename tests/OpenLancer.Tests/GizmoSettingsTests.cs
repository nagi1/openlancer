using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Editor;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The settings of the gizmo: the tool, the space, the snap toggle and the snap steps (decision
/// D27).</summary>
public sealed class GizmoSettingsTests
{
    private static readonly DateTime Saved = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheDefaultsAreTheMoveToolTheWorldAxesNoSnapAndTheStepsOfD27()
    {
        var settings = EditorSettings.Parse("{}", Saved);

        Assert.Equal(GizmoTool.Move, settings.Tool);
        Assert.Equal(GizmoSpace.World, settings.Space);
        Assert.Equal(new GizmoSnap(false, 100, 15, 100), settings.DragSnap);
        Assert.Equal(new GizmoSnap(true, 100, 15, 100), settings.DragSnap.WithCtrl(true));
    }

    [Fact]
    public void TheGizmoSettingsStayAfterASaveAndARead()
    {
        var settings = new EditorSettings
        {
            Tool = GizmoTool.Scale, Space = GizmoSpace.Local, Snap = true, MoveStep = 1000, RotateStep = 45,
            SizeStep = 50,
        };

        var json = settings.ToJson();
        var copy = EditorSettings.Parse(json, Saved);

        Assert.Contains("\"Tool\": \"Scale\"", json);
        Assert.Contains("\"Space\": \"Local\"", json);
        Assert.DoesNotContain("DragSnap", json);
        Assert.Equal(GizmoTool.Scale, copy.Tool);
        Assert.Equal(GizmoSpace.Local, copy.Space);
        Assert.Equal(new GizmoSnap(true, 1000, 45, 50), copy.DragSnap);
        Assert.Equal(new GizmoSnap(false, 1000, 45, 50), copy.DragSnap.WithCtrl(true));
    }

    /// <summary>
    /// A tool or a space that this editor does not know, for example a tool of a later editor, or a snap step that is
    /// not a finite number above 0, gives the default of that setting only. The other settings stay, also the active
    /// mod of each profile, so that the next edits go into the same mod. 1e39 is a JSON number that gives a float
    /// infinity.
    /// </summary>
    [Theory]
    [InlineData("Tool", "\"Bend\"")]
    [InlineData("Space", "\"Screen\"")]
    [InlineData("MoveStep", "0")]
    [InlineData("RotateStep", "-15")]
    [InlineData("SizeStep", "1e39")]
    [InlineData("SizeStep", "-1e39")]
    public void AnUnknownToolOrSpaceOrAnInvalidStepGivesItsDefaultAndKeepsTheOtherSettings(string name, string value)
    {
        var stored = new EditorSettings
        {
            Tool = GizmoTool.Rotate, Space = GizmoSpace.Local, Snap = true, MoveStep = 1000, RotateStep = 45,
            SizeStep = 50, ShowGrid = false,
        };
        stored.SetActiveMod(@"C:\p\sample.json", "ol-sample");
        stored.AddRecentProfile(@"C:\p\sample.json", Saved);
        var json = JsonNode.Parse(stored.ToJson())!;
        Assert.NotNull(json[name]);
        json[name] = JsonNode.Parse(value);

        var read = EditorSettings.Parse(json.ToJsonString(), Saved);

        var defaults = new EditorSettings();
        Assert.Equal(name == "Tool" ? defaults.Tool : stored.Tool, read.Tool);
        Assert.Equal(name == "Space" ? defaults.Space : stored.Space, read.Space);
        Assert.Equal(name == "MoveStep" ? defaults.MoveStep : stored.MoveStep, read.MoveStep);
        Assert.Equal(name == "RotateStep" ? defaults.RotateStep : stored.RotateStep, read.RotateStep);
        Assert.Equal(name == "SizeStep" ? defaults.SizeStep : stored.SizeStep, read.SizeStep);
        Assert.True(read.Snap);
        Assert.False(read.ShowGrid);
        Assert.Equal("ol-sample", read.ActiveModOf(@"C:\p\sample.json", ["ol-sample", "proof"]));
        Assert.Equal([@"C:\p\sample.json"], read.RecentProfiles.Select(r => r.Path));
    }

    /// <summary>The serializer itself rejects a value of the wrong JSON type, as for the other settings. So the file
    /// gives the defaults: a name in place of a number (JSON has no NaN), and a number or null in place of a
    /// name.</summary>
    [Theory]
    [InlineData("""{ "SizeStep": "NaN" }""")]
    [InlineData("""{ "Tool": 2 }""")]
    [InlineData("""{ "Space": null }""")]
    public void AValueOfTheWrongJsonTypeIsAnError(string json)
    {
        Assert.ThrowsAny<JsonException>(() => EditorSettings.Parse(json, Saved));
    }
}
