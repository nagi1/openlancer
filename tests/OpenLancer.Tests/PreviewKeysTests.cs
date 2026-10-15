using System;
using ImGuiNET;
using OpenLancer.Editor.Cutscene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The preview keys (section 7.7 of the M8 design). They work only while the Timeline or the Cutscene panel has the
/// focus and no text field takes the keys. F5 is the Play of the game, so the preview keys do not take it.
/// </summary>
public sealed class PreviewKeysTests
{
    [Theory]
    [InlineData(ImGuiKey.Space, false, "PlayPause")]
    [InlineData(ImGuiKey.Home, false, "Start")]
    [InlineData(ImGuiKey.End, false, "End")]
    [InlineData(ImGuiKey.LeftArrow, false, "FrameBack")]
    [InlineData(ImGuiKey.RightArrow, false, "FrameForward")]
    [InlineData(ImGuiKey.LeftArrow, true, "SecondBack")]
    [InlineData(ImGuiKey.RightArrow, true, "SecondForward")]
    [InlineData(ImGuiKey.F5, false, "None")]
    [InlineData(ImGuiKey.F, false, "None")]
    public void AKeyGivesItsCommandWhileThePanelHasTheFocus(ImGuiKey key, bool shift, string command)
    {
        Assert.Equal(Enum.Parse<PreviewKey>(command), PreviewKeys.Read(true, false, k => k == key, shift));
    }

    [Theory]
    [InlineData(ImGuiKey.Space)]
    [InlineData(ImGuiKey.Home)]
    [InlineData(ImGuiKey.End)]
    [InlineData(ImGuiKey.LeftArrow)]
    [InlineData(ImGuiKey.RightArrow)]
    public void NoKeyWorksWithoutTheFocusOrInATextField(ImGuiKey key)
    {
        Assert.Equal(PreviewKey.None, PreviewKeys.Read(false, false, k => k == key, false));
        Assert.Equal(PreviewKey.None, PreviewKeys.Read(true, true, k => k == key, false));
    }
}
