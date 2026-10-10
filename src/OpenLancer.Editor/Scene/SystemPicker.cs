using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The system list in the Scene toolbar strip. It opens a system of the loaded game data. The filter field at the top
/// of the list matches the name and the nickname. Enter opens the first match.
/// </summary>
internal sealed class SystemPicker(EditorWindow window)
{
    // Sizes in unscaled points.
    public const float MaxWidth = 220;
    public const float MinWidth = 120;

    private readonly FilterList<StarSystem> list = new();
    private GameDataManager? listData;

    // The preview text of the closed combo, for the system in previewSystem.
    private StarSystem? previewSystem;
    private string previewText = "";

    /// <summary>Forgets the list of the game data. Call it when the profile closes.</summary>
    public void Reset()
    {
        listData = null;
        list.Entries = [];
        previewSystem = null;
        previewText = "";
    }

    /// <summary>Draws the combo. It is disabled until the game data is loaded.</summary>
    /// <param name="width">The width of the combo, in pixels.</param>
    public void Draw(float width)
    {
        var data = window.Session?.GameData;
        if (!ReferenceEquals(data, listData))
        {
            list.Entries = data == null ? [] : Entries(data.Items.Systems, ids => window.Session!.Name(ids));
            listData = data;
        }

        var current = window.Scene.System;
        if (!ReferenceEquals(current, previewSystem))
        {
            previewSystem = current;
            previewText = current == null ? "" : $"{EditorIcons.Map}  {Name(current)} ({current.Nickname})";
        }

        // The combo cuts a long preview at its arrow, so the preview gets an ellipsis when it does not fit.
        var style = ImGui.GetStyle();
        var previewWidth = width - ImGui.GetFrameHeight() - 2 * style.FramePadding.X;
        var preview = current != null ? TextFit.CutRight(previewText, previewWidth)
            : data != null ? "Select a system"
            : "No game data";
        ImGui.SetNextItemWidth(width);
        ImGui.BeginDisabled(data == null);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##system", preview, ImGuiComboFlags.HeightLargest))
        {
            if (list.Draw(e => current != null && IsCurrent(e, current)) is { } chosen)
            {
                Open(chosen.Item);
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndCombo();
        }
        else
        {
            ImGui.SetItemTooltip(data == null ? "The game data is not loaded." : "Open a star system.");
        }

        EditorTheme.PopMenuColors();
        ImGui.EndDisabled();
    }

    /// <summary>The systems, sorted by name and then by nickname.</summary>
    internal static FilterEntry<StarSystem>[] Entries(IEnumerable<StarSystem> systems, Func<int, string> name) =>
        FilterList<StarSystem>.Sorted(systems.Select(s =>
            new FilterEntry<StarSystem>(s, name(s.IdsName) is { Length: > 0 } n ? n : s.Nickname, s.Nickname,
                EditorIcons.Map)));

    private static bool IsCurrent(FilterEntry<StarSystem> entry, StarSystem current) =>
        current.Nickname.Equals(entry.Nickname, StringComparison.OrdinalIgnoreCase);

    private void Open(StarSystem system)
    {
        if (window.Scene.System?.Nickname != system.Nickname)
        {
            window.OpenSystem(system);
        }
    }

    private string Name(StarSystem system) =>
        window.Session?.Name(system.IdsName) is { Length: > 0 } name ? name : system.Nickname;
}
