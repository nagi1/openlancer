using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using LibreLancer.Dialogs;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>
/// The fields of a new profile for a game: the game, the profile name and the workspace folder (section 7.3 of the M11a
/// design). The first-run screen and the New profile dialog show it. Create makes the profile with an empty mod, links
/// the game, and opens the profile, as a new Unity project opens ready to edit (decisions D194 and D216).
/// </summary>
internal sealed class NewProfileForm(EditorWindow window)
{
    private IReadOnlyList<GameReport> games = [];
    private int selected;
    private string name = "";
    private string? error;

    // The name of the last check whether a profile with the name exists. The check reads the disk, so it runs only when
    // the name or the workspace changes.
    private (string Name, string Workspace, bool Taken) taken = ("", "", false);

    /// <summary>The game of the new profile.</summary>
    public GameReport? Game => selected < games.Count ? games[selected] : null;

    /// <summary>Starts a new form for one of the games. Each game must be a game that the editor can use as a
    /// base.</summary>
    public void Reset(IReadOnlyList<GameReport> choices)
    {
        games = choices.Where(g => g.IsGame).ToList();
        selected = 0;
        error = null;
        name = Game == null ? "" : ModWorkspace.FreeProfileName(window.Settings.WorkspaceOrDefault, Game.ProfileName);
    }

    /// <summary>True when the fields are valid.</summary>
    public bool CanCreate => Game != null && name.Length > 0 && Problem() == null;

    /// <summary>Draws the fields.</summary>
    public void Draw()
    {
        if (games.Count > 1)
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
            if (ImGui.BeginCombo("Game", Game?.Name ?? ""))
            {
                for (var i = 0; i < games.Count; i++)
                {
                    if (ImGui.Selectable($"{games[i].Name}##{i}", i == selected))
                    {
                        selected = i;
                        name = ModWorkspace.FreeProfileName(window.Settings.WorkspaceOrDefault, games[i].ProfileName);
                    }

                    ImGui.SetItemTooltip(games[i].Root);
                }

                ImGui.EndCombo();
            }
        }

        ImGui.TextWrapped("OpenLancer makes a profile for this game. A profile names the game folder and your mods.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.InputText("Profile name", ref name, 64))
        {
            error = null;
        }

        ImGui.SetItemTooltip(GameTexts.ProfileNameRule);
        var workspace = window.Settings.WorkspaceOrDefault;
        ImGui.TextWrapped($"OpenLancer puts your mods in {Path.Combine(workspace, ModWorkspace.ModsFolder)}.");
        ImGui.SameLine();
        if (ImGui.TextLink("Change..."))
        {
            ChangeWorkspace();
        }

        ImGui.SetItemTooltip("Select the folder for new profiles and mods. The profiles that exist stay where they are.");
        ImGui.TextWrapped($"OpenLancer also makes an empty mod, {ModWorkspace.StarterModId}, for your changes.");
        if ((Problem() ?? error) is { } problem)
        {
            ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.TimesCircle}  {problem}");
        }
    }

    /// <summary>
    /// Makes the profile and its empty mod, links the game when it is not linked, sets the mod as the active mod and
    /// opens the profile. Returns false and shows the error when a step fails. The profile is made first, so a failure
    /// links nothing.
    /// </summary>
    public bool Create()
    {
        if (Game is not { Root: { } root } game || !CanCreate)
        {
            return false;
        }

        try
        {
            var (path, mod) = ModWorkspace.CreateProfile(window.Settings.WorkspaceOrDefault, name, root,
                window.Links.Games.Select(g => g.Path).ToList());
            if (window.Links.Find(root) == null)
            {
                window.Links.Add(game);
            }

            window.Settings.SetActiveMod(path, mod);
            window.Settings.Save();
            window.Log.Add(ConsoleSeverity.Info, GameLinks.Source,
                $"Made the profile {path} for {game.Name}, with the empty mod '{mod}'.");
            window.OpenProfile(path);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or ModLoadException)
        {
            error = ErrorText.Of(e);
            return false;
        }
    }

    /// <summary>The problem of the name, or null.</summary>
    private string? Problem()
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (!ModManifest.IsValidId(name))
        {
            return GameTexts.ProfileNameRule;
        }

        var workspace = window.Settings.WorkspaceOrDefault;
        if (taken.Name != name || taken.Workspace != workspace)
        {
            taken = (name, workspace, ModWorkspace.FreeProfileName(workspace, name) != name);
        }

        return taken.Taken ? GameTexts.ProfileNameExists : null;
    }

    /// <summary>Asks for a new workspace folder. A folder whose profiles or mods folder is in a game folder is refused
    /// (decision D204). The new folder applies to new profiles only (decision D216).</summary>
    private void ChangeWorkspace()
    {
        try
        {
            FileDialog.ChooseFolder(folder => window.QueueUIThread(() =>
            {
                if (folder == null)
                {
                    return;
                }

                try
                {
                    var roots = window.Links.Games.Select(g => g.Path).ToList();
                    if (Game?.Root is { } root)
                    {
                        roots.Add(root);
                    }

                    ModWorkspace.CheckWorkspace(folder, roots);
                    window.Settings.WorkspaceFolder = Path.GetFullPath(folder);
                    window.Settings.Save();
                    error = null;
                    if (Game != null)
                    {
                        name = ModWorkspace.FreeProfileName(window.Settings.WorkspaceOrDefault, Game.ProfileName);
                    }
                }
                catch (IOException e)
                {
                    error = e.Message;
                }
            }));
        }
        catch (Exception e)
        {
            // The folder picker of the system can fail, for example without a desktop portal.
            window.Log.Add(ConsoleSeverity.Error, GameLinks.Source, $"The folder picker did not open: {e.Message}");
        }
    }
}
