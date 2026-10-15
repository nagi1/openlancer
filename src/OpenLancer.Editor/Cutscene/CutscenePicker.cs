using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using LibreLancer;
using OpenLancer.Data.IO;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The cutscene list in the Cutscene toolbar strip. It opens a cutscene script of the loaded game data: each
/// <c>.thn</c> of the edit view. The filter field matches the file name and the install path. The list builds again
/// when the game data loads again, because a save can add a script.
/// </summary>
internal sealed class CutscenePicker(EditorWindow window, CutsceneWorkspace workspace)
{
    // Sizes in unscaled points.
    public const float Width = 220;

    private readonly FilterList<string> list = new();

    // The game data that the list shows.
    private GameDataManager? listData;

    /// <summary>Forgets the list of the game data. Call it when the profile closes.</summary>
    public void Reset()
    {
        listData = null;
        list.Entries = [];
    }

    /// <summary>Draws the combo. It is disabled until the game data is loaded.</summary>
    /// <param name="width">The width of the combo, in pixels.</param>
    public void Draw(float width)
    {
        var data = window.Session?.GameData;
        if (!ReferenceEquals(listData, data))
        {
            list.Entries = data == null ? [] : Entries(VfsFiles.Find(data.Items.VFS, ".thn"));
            listData = data;
        }

        var current = workspace.Path;
        var style = ImGui.GetStyle();
        var previewWidth = width - ImGui.GetFrameHeight() - 2 * style.FramePadding.X;
        var preview = current != null
            ? TextFit.CutRight($"{EditorIcons.Film}  {Path.GetFileNameWithoutExtension(current)}", previewWidth)
            : data != null ? "Open a cutscene"
            : "No game data";
        ImGui.SetNextItemWidth(width);
        ImGui.BeginDisabled(data == null);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##cutscene", preview, ImGuiComboFlags.HeightLargest))
        {
            if (list.Draw(e => VfsPath.Comparer.Equals(e.Item, current)) is { } chosen)
            {
                workspace.Open(chosen.Item);
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndCombo();
        }
        else
        {
            ImGui.SetItemTooltip(data == null ? "The game data is not loaded."
                : current ?? "Open a cutscene script of the game data.");
        }

        EditorTheme.PopMenuColors();
        ImGui.EndDisabled();
    }

    /// <summary>The rows: the file name, with the install path as the nickname, sorted by name.</summary>
    private static FilterEntry<string>[] Entries(IEnumerable<string> paths) =>
        FilterList<string>.Sorted(paths.Select(p =>
            new FilterEntry<string>(p, Path.GetFileNameWithoutExtension(p), p, EditorIcons.Film)));
}
