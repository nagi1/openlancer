using System;
using ImGuiNET;

namespace OpenLancer.Editor.Cutscene;

/// <summary>A command of the preview keys.</summary>
internal enum PreviewKey
{
    None,
    PlayPause,
    Start,
    End,
    FrameBack,
    FrameForward,
    SecondBack,
    SecondForward,
}

/// <summary>
/// The keys of the preview (section 7.7 of the M8 design): Space plays or pauses, Home and End go to the start and the
/// end, Left and Right step one frame, and Shift with Left or Right steps one second. The keys work only while the
/// Timeline or the Cutscene panel has the focus and no text field takes the keys. F5 and Shift+F5 stay the global Play
/// and Stop of the game (M4).
/// </summary>
internal static class PreviewKeys
{
    /// <summary>The command of the keys of this frame, or <see cref="PreviewKey.None"/>.</summary>
    /// <param name="focused">The Timeline or the Cutscene panel has the focus.</param>
    /// <param name="textInput">A text field takes the keys.</param>
    /// <param name="pressed">True for a key that went down in this frame, or repeats.</param>
    /// <param name="shift">Shift is down.</param>
    public static PreviewKey Read(bool focused, bool textInput, Func<ImGuiKey, bool> pressed, bool shift)
    {
        if (!focused || textInput)
        {
            return PreviewKey.None;
        }

        if (pressed(ImGuiKey.Space))
        {
            return PreviewKey.PlayPause;
        }

        if (pressed(ImGuiKey.Home))
        {
            return PreviewKey.Start;
        }

        if (pressed(ImGuiKey.End))
        {
            return PreviewKey.End;
        }

        if (pressed(ImGuiKey.LeftArrow))
        {
            return shift ? PreviewKey.SecondBack : PreviewKey.FrameBack;
        }

        return pressed(ImGuiKey.RightArrow)
            ? shift ? PreviewKey.SecondForward : PreviewKey.FrameForward
            : PreviewKey.None;
    }
}
