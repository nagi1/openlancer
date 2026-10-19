using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Games;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// The header of the detail page of the Mods window (section 10.3 of the M11c design): the display name in the large
/// font, the id, the version and the authors, and the chips of the state. The primary button Export... is at the top
/// right, with Play and the "..." menu next to it.
/// </summary>
internal sealed class ModHeader(EditorWindow window, ModsWindow mods)
{
    /// <summary>The look of a chip of a color of the catalog.</summary>
    internal static ChipLook LookOf(ChipColor color) => color switch
    {
        ChipColor.Blue => ChipLook.Filled(EditorTheme.Accent),
        ChipColor.Red => ChipLook.Filled(EditorTheme.Error),
        ChipColor.Green => ChipLook.Filled(EditorTheme.Success),
        ChipColor.Yellow => ChipLook.Filled(EditorTheme.Warning),
        ChipColor.WhiteOutline => ChipLook.Outline(EditorTheme.TextBright),
        // The mark "published". Violet stays for prefab overrides.
        ChipColor.GreenOutline => ChipLook.Outline(EditorTheme.Success),
        _ => ChipLook.Grey,
    };

    public void Draw(ModRow row)
    {
        ImGui.PushID("##header");
        var buttonsWidth = ButtonsWidth();
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(1, ImGui.GetContentRegionAvail().X - buttonsWidth));
        ImGui.PushFont(null, EditorTheme.HeroSize);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        CardLayout.Bold(row.Name);
        ImGui.PopStyleColor();
        ImGui.PopFont();
        var line = row.Manifest is { } manifest
            ? $"{row.Id} . {manifest.Version}" + (row.Settings.Authors.Count > 0 ? $" . by {string.Join(", ", row.Settings.Authors)}" : "")
            : row.Id;
        CardLayout.Dim(line);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        Buttons(row);
        ImGui.Spacing();
        Chip.Flow([.. row.Chips.Select(c => new ChipItem(c.Text, LookOf(c.Color), c.Tooltip))]);
        ImGui.PopID();
    }

    /// <summary>The width of the buttons at the right of the header.</summary>
    private static float ButtonsWidth()
    {
        var style = ImGui.GetStyle();
        return CardLayout.ButtonWidth("Export...") + ImGui.CalcTextSize($"{EditorIcons.Play}  Play {EditorIcons.CaretDown}").X +
               ImGui.CalcTextSize("...").X + 4 * style.FramePadding.X + 3 * style.ItemSpacing.X;
    }

    /// <summary>Export..., the Play menu and the "..." menu. A mod that does not load has Open folder only.</summary>
    private void Buttons(ModRow row)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - ButtonsWidth()));
        if (row.Manifest is not { } manifest)
        {
            if (ImGui.Button("Open folder"))
            {
                FolderActions.Open(window, row.Folder);
            }

            return;
        }

        var exportBlock = window.ExportBlock;
        if (CardLayout.Button(new CardButton("Export...", Primary: true, Enabled: exportBlock == null)))
        {
            mods.Export(row);
        }

        Tooltip(exportBlock ?? "Make the packages of this mod for its target games, and the texts of the Nexus page.");
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Play}  Play {EditorIcons.CaretDown}"))
        {
            ImGui.OpenPopup("##play");
        }

        ImGui.SetItemTooltip("Play this mod alone in a linked game, without a package.");
        if (ImGui.BeginPopup("##play"))
        {
            PlayMenu(row, manifest);
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("..."))
        {
            ImGui.OpenPopup("##more");
        }

        if (ImGui.BeginPopup("##more"))
        {
            MoreMenu(row);
            ImGui.EndPopup();
        }
    }

    /// <summary>One item "Play this mod alone in &lt;game&gt;" for each play game of the mod (decision D577).</summary>
    private void PlayMenu(ModRow row, Data.Mods.ModManifest manifest)
    {
        var games = mods.PlayGames(manifest);
        if (games.Count == 0)
        {
            ImGui.MenuItem("No linked game can play this mod", false);
            ImGui.SetItemTooltip("Link a game folder with its game program of a target game of the mod in File > Game link.");
            return;
        }

        var block = mods.PlayBlock();
        foreach (var game in games)
        {
            if (ImGui.MenuItem($"Play this mod alone in {game.Name}", "", false, block == null))
            {
                mods.PlayAlone(row, game);
            }

            Tooltip(block ?? $"Install the build of this mod for {game.Name} into the play copy of {game.Root}, and " +
                    "start the game. The play copy then holds this mod alone.");
        }
    }

    /// <summary>The "..." menu: Make active mod, Duplicate, Rename, the profile, the folders and the Console.</summary>
    private void MoreMenu(ModRow row)
    {
        var session = window.Session;
        var inProfile = row.Group == ModGroup.Profile;
        var active = session?.Edits.ActiveMod is { } current && GameFolders.Same(current.Folder, row.Folder);
        if (ImGui.MenuItem("Make active mod", "", active, inProfile && !active && window.CanChangeProfile))
        {
            window.SetActiveMod(row.Id);
        }

        Tooltip(!inProfile ? "Add the mod to the profile first. The active mod is a mod of the open profile."
            : "The editor saves your edits into the active mod.");
        ImGui.Separator();
        if (ImGui.MenuItem("Duplicate..."))
        {
            mods.Duplicate(row);
        }

        ImGui.SetItemTooltip("Make a copy of the mod with a new id and a new display name. The copy is not in a profile.");
        var writeBlock = mods.WriteBlock(row);
        if (ImGui.MenuItem("Rename...", "", false, writeBlock == null))
        {
            mods.Rename(row);
        }

        Tooltip(writeBlock ?? "Give the mod a new id and a new folder name.");
        ImGui.Separator();
        var profileBlock = mods.ProfileBlock();
        if (session != null && mods.ProfileNeedsAllowance && ImGui.MenuItem("Allow edits of this repo profile"))
        {
            mods.AllowProfile();
        }

        var changeBlock = profileBlock ?? (window.CanChangeProfile ? null : "The game data of the open profile is loading.");
        if (inProfile)
        {
            if (ImGui.MenuItem("Remove from profile", "", false, changeBlock == null))
            {
                mods.RemoveFromProfile(row);
            }

            Tooltip(changeBlock ?? "Remove the mod from the profile. The folder of the mod stays.");
        }
        else
        {
            if (ImGui.MenuItem("Add to profile", "", false, changeBlock == null && row.Manifest != null))
            {
                mods.AddToProfile(row);
            }

            Tooltip(changeBlock ?? "Add the mod to the end of the load order of the profile.");
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Open folder"))
        {
            FolderActions.Open(window, row.Folder);
        }

        if (ImGui.MenuItem("Open export folder"))
        {
            mods.OpenExportFolder(row);
        }

        if (ImGui.MenuItem("Show in Console"))
        {
            mods.ShowProblems(row);
        }

        ImGui.SetItemTooltip("Write the problems of the last check of the mod to the Console.");
    }

    /// <summary>The tooltip of the last item, also when it is disabled.</summary>
    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(text);
        }
    }
}
