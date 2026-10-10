using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// The states of a panel body that has nothing to show yet: a loading spinner or a message, centered in the free space.
/// </summary>
internal static class PanelStates
{
    // Sizes in unscaled points.
    private const float SpinnerRadius = 12;
    private const float SpinnerThickness = 3;
    private const float LineGap = 8;

    /// <summary>
    /// Draws the state of a panel that needs the game data, when the data is not there: no profile, the load, or a
    /// failed load.
    /// </summary>
    public static void NoGameData(EditorSession? session)
    {
        if (session == null)
        {
            Message("No profile is open.", "Open a profile from the File menu.");
        }
        else if (session.IsLoading)
        {
            Loading("Loading game data");
        }
        else
        {
            Message("The game data did not load.", "The Console shows the error.");
        }
    }

    /// <summary>Draws a spinner above a line of text, both centered in the free space.</summary>
    public static void Loading(string text)
    {
        var scale = ImGuiHelper.Scale;
        var radius = MathF.Truncate(SpinnerRadius * scale);
        // The spinner item is as high as its circle plus two frame paddings.
        var spinnerHeight = 2 * (radius + ImGui.GetStyle().FramePadding.Y);
        var gap = MathF.Truncate(LineGap * scale);
        var textSize = ImGui.CalcTextSize(text);
        var (start, avail) = (ImGui.GetCursorPos(), ImGui.GetContentRegionAvail());
        var top = start.Y + MathF.Max(0, MathF.Truncate((avail.Y - spinnerHeight - gap - textSize.Y) / 2));

        ImGui.SetCursorPos(new Vector2(start.X + MathF.Truncate((avail.X - 2 * radius) / 2), top));
        ImGuiExt.Spinner("##loading", radius, (int)MathF.Round(SpinnerThickness * scale),
            ImGui.GetColorU32(EditorTheme.Accent));
        ImGui.SetCursorPos(new Vector2(start.X + MathF.Truncate((avail.X - textSize.X) / 2),
            top + spinnerHeight + gap));
        ImGui.TextDisabled(text);
    }

    /// <summary>Draws a dim message and a smaller hint below it, both centered in the free space.</summary>
    public static void Message(string message, string hint) => Lines(message, hint, false);

    /// <summary>
    /// Draws a dim message and a link below it, both centered in the free space. Returns true when the user clicks
    /// the link.
    /// </summary>
    public static bool MessageWithLink(string message, string link) => Lines(message, link, true);

    /// <summary>Draws a message and a second line in the caption size: a dim hint, or a link.</summary>
    private static bool Lines(string message, string second, bool isLink)
    {
        var gap = MathF.Truncate(LineGap * ImGuiHelper.Scale / 2);
        var messageSize = ImGui.CalcTextSize(message);
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        var secondSize = ImGui.CalcTextSize(second);
        ImGui.PopFont();
        var (start, avail) = (ImGui.GetCursorPos(), ImGui.GetContentRegionAvail());
        var top = start.Y + MathF.Max(0, MathF.Truncate((avail.Y - messageSize.Y - gap - secondSize.Y) / 2));

        ImGui.SetCursorPos(new Vector2(start.X + MathF.Max(0, MathF.Truncate((avail.X - messageSize.X) / 2)), top));
        ImGui.TextDisabled(message);
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        ImGui.SetCursorPos(new Vector2(start.X + MathF.Max(0, MathF.Truncate((avail.X - secondSize.X) / 2)),
            top + messageSize.Y + gap));
        var clicked = false;
        if (isLink)
        {
            clicked = ImGui.TextLink(second);
        }
        else
        {
            ImGui.TextDisabled(second);
        }

        ImGui.PopFont();
        return clicked;
    }
}
