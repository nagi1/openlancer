using System.Globalization;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The Preview controls of the Timeline toolbar (section 7.7 of the M8 design): the label, the circle icons of play,
/// pause and stop, one frame back and forward, the loop, the pre-roll, the speed and the sound. The circle icons do not
/// look like the Play of the game in the menu bar.
/// </summary>
internal static class PreviewControls
{
    public const string PlayTooltip =
        "Preview in the editor (Space). The Play button of the menu bar starts the game.";

    public const string SoundTooltip = "Sound: the preview plays the sounds and the voice lines of this cutscene in 2D.";

    // Sizes in unscaled points.
    private const float SpeedWidth = 58;

    /// <summary>Draws the controls. They are disabled without a preview. The Sound toggle changes the setting of the
    /// editor (decision D132), as the Mute Audio toggle of the Unity Game view.</summary>
    public static void Draw(PreviewTransport? transport, EditorSettings settings)
    {
        var on = transport != null;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Preview");
        ImGui.SameLine();
        PlayButton(transport);
        ImGui.SameLine();
        if (ToolbarItems.Button("pause", EditorIcons.PauseCircle, "Pause the preview (Space).", transport?.Playing == true))
        {
            transport!.Pause();
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("stop", EditorIcons.StopCircle, "Stop the preview and go to the start (Home).", on))
        {
            transport!.Stop();
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("back", EditorIcons.StepBackward,
                "One frame back (Left). Shift+Left goes one second back.", on))
        {
            transport!.StepFrames(-1);
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("forward", EditorIcons.StepForward,
                "One frame forward (Right). Shift+Right goes one second forward.", on))
        {
            transport!.StepFrames(1);
        }

        ImGui.SameLine();
        var loop = ToolbarItems.Toggle("loop", EditorIcons.SyncAlt, LoopTooltip(transport), transport?.Loop == true,
            on);
        ImGui.SameLine();
        var preRoll = ToolbarItems.Toggle("preroll", EditorIcons.History,
            $"Pre-roll: a play from a scrubbed time starts {PreviewTransport.PreRollSeconds:0} s earlier at " +
            $"{PreviewTransport.PreRollSpeed:0} times the speed, so the particle effects show as in the game.",
            transport?.PreRoll == true, on);
        if (transport != null)
        {
            transport.Loop = loop;
            transport.PreRoll = preRoll;
        }

        ImGui.SameLine();
        SpeedMenu(transport, SpeedWidth * ImGuiHelper.Scale);
        ImGui.SameLine();
        settings.PreviewSound = ToolbarItems.Toggle("sound",
            settings.PreviewSound ? EditorIcons.VolumeUp : EditorIcons.VolumeMute, SoundTooltip, settings.PreviewSound);
    }

    /// <summary>The play toggle alone, for example next to the scrub bar of the Cutscene view.</summary>
    public static void PlayButton(PreviewTransport? transport)
    {
        var playing = transport?.Playing == true;
        if (ToolbarItems.Toggle("play", EditorIcons.PlayCircle, PlayTooltip, playing, transport != null) != playing)
        {
            transport!.TogglePlay();
        }
    }

    private static void SpeedMenu(PreviewTransport? transport, float width)
    {
        ImGui.SetNextItemWidth(width);
        ImGui.BeginDisabled(transport == null);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##speed", Speed(transport?.Speed ?? 1), ImGuiComboFlags.NoArrowButton))
        {
            foreach (var speed in PreviewTransport.Speeds)
            {
                if (ImGui.Selectable(Speed(speed), speed == transport?.Speed))
                {
                    transport!.Speed = speed;
                }
            }

            ImGui.EndCombo();
        }
        else
        {
            ImGui.SetItemTooltip("The speed of the preview.");
        }

        EditorTheme.PopMenuColors();
        ImGui.EndDisabled();
    }

    private static string Speed(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    /// <summary>The tooltip of the loop: the loop range, or the whole script and how to set a range.</summary>
    private static string LoopTooltip(PreviewTransport? transport) => transport?.LoopRange is { } range
        ? string.Create(CultureInfo.InvariantCulture,
            $"Loop: at {range.End:0.000} s, the preview plays again from {range.Start:0.000} s. Shift+click on the " +
            $"ruler of the Timeline clears the range.")
        : "Loop: at the end, the preview plays again from the start. Shift+drag on the ruler of the Timeline sets a " +
          "range to loop.";
}
