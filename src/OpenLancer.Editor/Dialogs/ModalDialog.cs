using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Dialogs;

/// <summary>
/// A modal dialog: it blocks the rest of the window until the user closes it. Call <see cref="Draw"/> each frame.
/// </summary>
internal abstract class ModalDialog
{
    // Sizes in unscaled points.
    private const float ButtonWidth = 100;
    private const float TextWidth = 460;

    private bool openRequested;

    /// <summary>The title, which is also the ImGui id of the popup.</summary>
    protected abstract string Title { get; }

    /// <summary>True while the dialog shows, from <see cref="Open"/> until it closes.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Shows the dialog from the next frame.</summary>
    protected void Open()
    {
        openRequested = true;
        IsOpen = true;
    }

    public void Draw()
    {
        if (openRequested)
        {
            ImGui.OpenPopup(Title);
            openRequested = false;
        }

        if (!IsOpen)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        var open = true;
        if (!ImGui.BeginPopupModal(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            // The close button of the title bar closed it, or another popup replaced it.
            IsOpen = false;
            Cancelled();
            return;
        }

        // ImGui does not close a modal popup on Escape, so the dialog does it. While a text field is active, Escape
        // only ends the field, so the check comes before the fields draw.
        var escape = ImGui.IsKeyPressed(ImGuiKey.Escape, false) && !ImGui.IsAnyItemActive();
        ImGui.PushTextWrapPos(MathF.Truncate(TextWidth * ImGuiHelper.Scale));
        Body();
        ImGui.PopTextWrapPos();
        if (escape && IsOpen)
        {
            Close();
            Cancelled();
        }

        ImGui.EndPopup();
    }

    /// <summary>Draws the content and the buttons. Call <see cref="Close"/> to close the dialog.</summary>
    protected abstract void Body();

    /// <summary>Runs when Escape or the close button of the title bar closes the dialog. It is the same as
    /// Cancel.</summary>
    protected virtual void Cancelled()
    {
    }

    protected void Close()
    {
        IsOpen = false;
        ImGui.CloseCurrentPopup();
    }

    /// <summary>A dialog button. The first button of a row is the default button: Enter clicks it.</summary>
    protected static bool Button(string label, bool primary = false, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        if (primary)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, EditorTheme.AccentStrong);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, EditorTheme.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, EditorTheme.Pressed);
        }

        // A long label, for example "Rename in this file", makes the button wider than the usual width.
        var width = MathF.Max(MathF.Truncate(ButtonWidth * ImGuiHelper.Scale),
            MathF.Ceiling(ImGui.CalcTextSize(label).X + 2 * ImGui.GetStyle().FramePadding.X));
        var clicked = ImGui.Button(label, new Vector2(width, 0)) ||
                      primary && enabled && ImGui.IsKeyPressed(ImGuiKey.Enter, false) &&
                      !ImGui.IsAnyItemActive();
        if (primary)
        {
            ImGui.PopStyleColor(3);
        }

        ImGui.EndDisabled();
        return clicked;
    }

    /// <summary>A line of text in the color of an error, for a problem of the input.</summary>
    protected static void Problem(string? message)
    {
        if (message != null)
        {
            ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.TimesCircle}  {message}");
        }
    }
}
