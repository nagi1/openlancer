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
/// of the list matches the name and the nickname. Enter opens the first match. The names come from W (see
/// <see cref="EditorSession.ItemName"/>), so the list builds again when the edit view or the text changes.
/// </summary>
internal sealed class SystemPicker(EditorWindow window)
{
    // Sizes in unscaled points.
    public const float MaxWidth = 220;
    public const float MinWidth = 120;

    private readonly FilterList<StarSystem> list = new();

    // The game data and the version of the session that the list shows.
    private (GameDataManager? Data, int Version) listState;

    // The preview text of the closed combo, for the system and the version of the session in previewState.
    private (StarSystem? System, int Version) previewState;
    private string previewText = "";

    /// <summary>Forgets the list of the game data. Call it when the profile closes.</summary>
    public void Reset()
    {
        listState = default;
        list.Entries = [];
        previewState = default;
        previewText = "";
    }

    /// <summary>Draws the combo. It is disabled until the game data is loaded.</summary>
    /// <param name="width">The width of the combo, in pixels.</param>
    public void Draw(float width)
    {
        var session = window.Session;
        var data = session?.GameData;
        var version = session?.Version ?? 0;
        if (IsStale(listState, data, version))
        {
            list.Entries = data == null ? [] : Entries(data.Items.Systems, session!.ItemName);
            listState = (data, version);
        }

        var current = window.Scene.System;
        if (IsStale(previewState, current, version))
        {
            previewState = (current, version);
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
    /// <param name="name">The name of a system, or an empty string when it has none.</param>
    internal static FilterEntry<StarSystem>[] Entries(IEnumerable<StarSystem> systems, Func<StarSystem, string> name) =>
        FilterList<StarSystem>.Sorted(systems.Select(s =>
            new FilterEntry<StarSystem>(s, name(s) is { Length: > 0 } n ? n : s.Nickname, s.Nickname, EditorIcons.Map)));

    /// <summary>
    /// True when the list or the preview must build again: it shows other game data or another system, or an older
    /// version of the session. A new version can bring a new name of a system.
    /// </summary>
    /// <param name="shown">The game data or the system, and the version of the session, that it shows.</param>
    internal static bool IsStale<T>((T? Item, int Version) shown, T? item, int version) where T : class =>
        !ReferenceEquals(shown.Item, item) || shown.Version != version;

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
        window.Session?.ItemName(system) is { Length: > 0 } name ? name : system.Nickname;
}
