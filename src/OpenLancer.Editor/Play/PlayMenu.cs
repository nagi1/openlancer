using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

/// <summary>A system and its bases, for the Play at base list.</summary>
internal sealed record BaseGroup(StarSystem System, IReadOnlyList<Base> Bases);

/// <summary>
/// The Play controls of the main menu bar (section 11.1 of the M4 design): the Play menu, and three buttons in the
/// middle of the bar, as Unity has them. The buttons are Play, a down arrow that opens the Play menu, and Stop.
/// </summary>
/// <remarks>
/// A Play control only asks <see cref="PlayController"/> for a play. The request runs after the layout of the frame,
/// and it waits for the end of an open field edit or drag (section 9.1). So a click on Play while a field of the Inspector
/// is open ends the field, and the save has its edit.
/// </remarks>
internal sealed class PlayMenu(EditorWindow window)
{
    private const string MenuPopup = "##play-menu";

    // The Play at base list, made when the list opens.
    private IReadOnlyList<BaseGroup> bases = [];

    /// <summary>
    /// The bases by system, in the order of the systems of the game data, with the open system first. A base without a
    /// system of the game data is not in the list, because the play starts in the system of the base.
    /// </summary>
    /// <param name="open">The nickname of the open system, or null.</param>
    internal static IReadOnlyList<BaseGroup> BaseGroups(IEnumerable<StarSystem> systems, IEnumerable<Base> bases,
        string? open)
    {
        var bySystem = bases.Where(b => b.System != null).ToLookup(b => b.System!, StringComparer.OrdinalIgnoreCase);
        var groups = systems.Select(s => new BaseGroup(s, bySystem[s.Nickname].ToList())).Where(g => g.Bases.Count > 0)
            .ToList();
        var first = groups.FindIndex(g => g.System.Nickname.Equals(open, StringComparison.OrdinalIgnoreCase));
        if (first > 0)
        {
            var group = groups[first];
            groups.RemoveAt(first);
            groups.Insert(0, group);
        }

        return groups;
    }

    /// <summary>Plays from the Scene camera: the Play button, F5 and Play from camera. A running game closes first
    /// (section 9.9). The request keeps the camera. The start check of section 9.5 runs when the game starts (see
    /// <see cref="SpawnPoint.FromCamera"/>).</summary>
    public void PlayFromCamera()
    {
        if (CameraBlock() is { } reason)
        {
            Log(ConsoleSeverity.Warning, $"Play from camera is not available: {reason}");
            return;
        }

        var scene = window.Scene;
        Request(new PlayStart.InSpace(scene.System!.Nickname, scene.CameraPosition, scene.CameraOrientation));
    }

    /// <summary>Stops the game, or ends it at once when Stop is Force stop: the Stop button, Shift+F5 and Stop.</summary>
    public void Stop() => window.Play.Stop();

    /// <summary>The items of the Play menu, in the menu bar and in the popup of the arrow button.</summary>
    public void Menu()
    {
        var play = window.Play;
        var block = CameraBlock();
        if (ImGui.MenuItem($"{EditorIcons.Play}   Play from camera", "F5", false, block == null))
        {
            PlayFromCamera();
        }

        ImGui.SetItemTooltip(PlayTooltip(block));
        BaseMenu();
        ImGui.Separator();
        ShipItems();
        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.SyncAlt, "Reload game", play.CanReload))
        {
            play.Reload();
        }

        ImGui.SetItemTooltip(play.ReloadBlock ??
                             "The running game loads the mod files again and continues at the same place. Use it " +
                             "after a change outside the editor. A save reloads the game itself when its validation " +
                             "passes.");
        if (ImGui.MenuItem($"{EditorIcons.Stop}   {(play.ForceStop ? "Force stop" : "Stop")}", "Shift+F5", false,
                play.Status != PlayStatus.Idle))
        {
            Stop();
        }

        ImGui.SetItemTooltip(StopTooltip());
    }

    /// <summary>Draws the Play, menu and Stop buttons in the middle of the main menu bar. Call it in the menu bar after
    /// the menus, while the menu colors are pushed, so that the Play menu of the arrow button gets them.</summary>
    public void Buttons()
    {
        var play = window.Play;
        var scale = ImGuiHelper.Scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = 3 * ToolbarItems.IconWidth + 2 * spacing;
        // In the middle of the window, or after the menus when the window is too narrow for that.
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), MathF.Truncate((ImGui.GetWindowWidth() - width) / 2)));
        EditorTheme.PushFlatButtonColors();

        // The Play button shows pressed while a game runs. A click then closes the game and plays again.
        var running = play.Status != PlayStatus.Idle;
        var block = CameraBlock();
        if (ToolbarItems.Toggle("play", EditorIcons.Play, PlayTooltip(block), running, block == null) != running)
        {
            PlayFromCamera();
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("play-menu", EditorIcons.CaretDown, "Play menu"))
        {
            ImGui.OpenPopup(MenuPopup);
        }

        // The menu opens below the button.
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + 2 * scale),
            ImGuiCond.Appearing);
        if (ImGui.BeginPopup(MenuPopup))
        {
            Menu();
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("stop", EditorIcons.Stop, StopTooltip(), running))
        {
            Stop();
        }

        EditorTheme.PopFlatButtonColors();
    }

    /// <summary>Why Play from camera cannot run now, or null when it can.</summary>
    private string? CameraBlock() =>
        window.Session == null ? "No profile is open."
        : window.Session.GameData == null ? "The game data of the profile did not load."
        : window.Scene.System == null ? "No system is open. Play from camera starts the ship at the Scene camera."
        : null;

    private string PlayTooltip(string? block) =>
        block ?? (window.Play.Status != PlayStatus.Idle
            ? "Close the game and play again at the camera (F5)"
            : "Play from camera (F5): save the active mod, then fly where the Scene camera is");

    private string StopTooltip() => window.Play.ForceStop
        ? "Force stop (Shift+F5): the game does not respond, so this ends its process"
        : "Stop the game (Shift+F5)";

    /// <summary>The Play at base list: the bases of the open system first, then the other bases by system (section
    /// 9.7).</summary>
    private void BaseMenu()
    {
        var data = window.Session?.GameData;
        if (!ImGui.BeginMenu($"{EditorIcons.Building}   Play at base", data != null))
        {
            if (data == null)
            {
                ImGui.SetItemTooltip("The game data of the profile did not load.");
            }

            return;
        }

        var open = window.Scene.System?.Nickname;
        if (ImGui.IsWindowAppearing())
        {
            bases = BaseGroups(data!.Items.Systems, data.Items.Bases, open);
        }

        for (var i = 0; i < bases.Count; i++)
        {
            var group = bases[i];
            if (i == 0 && group.System.Nickname.Equals(open, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextDisabled(Named(group.System));
                BaseItems(group);
                ImGui.Separator();
            }
            else if (ImGui.BeginMenu($"{Named(group.System)}##{group.System.Nickname}"))
            {
                BaseItems(group);
                ImGui.EndMenu();
            }
        }

        if (bases.Count == 0)
        {
            ImGui.MenuItem("The game data has no bases", false);
        }

        ImGui.EndMenu();
    }

    private void BaseItems(BaseGroup group)
    {
        foreach (var dock in group.Bases)
        {
            var name = window.Session?.ItemName(dock) is { Length: > 0 } text ? text : dock.Nickname;
            if (ImGui.MenuItem($"{name}##{dock.Nickname}", dock.Nickname))
            {
                Request(new PlayStart.AtBase(dock.Nickname));
            }
        }
    }

    /// <summary>The ship line and Use the default ship (section 11.2, decision D15).</summary>
    private void ShipItems()
    {
        var session = window.Session;
        var package = session == null ? null : window.Settings.PlayPackageOf(session.Profile.FilePath);
        var name = session == null ? null : PlayShips.ShipName(session, package);
        string text, tooltip;
        if (package == null)
        {
            text = $"Ship: {name ?? "the ship of a new character"} (default)";
            tooltip = "The ship of a new multiplayer character. To fly another ship, right-click a ship package in " +
                      "Database > Goods, then click Use for Play.";
        }
        else if (name != null)
        {
            text = $"Ship: {name}";
            tooltip = $"The ship package {package}. Select another one in Database > Goods.";
        }
        else
        {
            text = $"Ship: {package} (not in the data)";
            tooltip = $"The edit view has no ship package {package}, so Play stops with an error. Click Use the " +
                      "default ship, or select a ship package in Database > Goods.";
        }

        ImGui.MenuItem($"{EditorIcons.SpaceShuttle}   {text}", false);
        ImGui.SetItemTooltip(tooltip);
        if (Theme.IconMenuItem(EditorIcons.Undo, "Use the default ship", package != null))
        {
            window.UseForPlay(null);
        }
    }

    /// <summary>"New York (Li01)", or only the nickname when the system has no name.</summary>
    private string Named(StarSystem system) =>
        window.Session?.ItemName(system) is { Length: > 0 } name ? $"{name} ({system.Nickname})" : system.Nickname;

    private void Request(PlayStart start) =>
        window.Play.Request(start, window.Settings.PlayPackageOf(window.Session!.Profile.FilePath));

    private void Log(ConsoleSeverity severity, string message) =>
        window.Log.Add(severity, PlayController.Source, message);
}
