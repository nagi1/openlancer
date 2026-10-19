using System;
using OpenLancer.Editor;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The toggles of the editor settings that have no test file of their own.</summary>
public sealed class EditorSettingsTests
{
    private static readonly DateTime Saved = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The toggle "Script objects" of a space context is on by default and survives a save and a read
    /// (decision D645).</summary>
    [Fact]
    public void TheScriptObjectsToggleIsOnByDefaultAndSurvivesASave()
    {
        Assert.True(EditorSettings.Parse("{}", Saved).CutsceneScriptObjects);
        Assert.True(new EditorSettings().CutsceneScriptObjects);

        var settings = new EditorSettings { CutsceneScriptObjects = false };
        var json = settings.ToJson();

        Assert.Contains("\"CutsceneScriptObjects\": false", json);
        Assert.False(EditorSettings.Parse(json, Saved).CutsceneScriptObjects);
    }
}
