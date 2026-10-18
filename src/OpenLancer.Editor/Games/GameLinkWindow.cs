using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Dialogs;
using LibreLancer.ImUI;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>
/// The Game link window (section 8 of the M11a design, decision D202): a floating window with one card for each linked
/// game. A card shows the game, its state, its export target, its game program and its profiles, and has Check again,
/// Relink, Open folder, New profile and Remove link. It follows the Installs page of the Unity Hub.
/// </summary>
internal sealed class GameLinkWindow(EditorWindow window)
{
    private const string Title = "Game link";
    private static readonly Vector2 DefaultSize = new(640, 420);

    /// <summary>A check is older than this when the window opens, so it runs again.</summary>
    private static readonly TimeSpan RecheckAge = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, (Vector2 Min, Vector2 Max)> cards = new(StringComparer.OrdinalIgnoreCase);
    private bool open;
    private bool focus;
    private string? linking;
    private string? linkError;

    /// <summary>True while the window shows.</summary>
    public bool IsOpen => open;

    /// <summary>Shows the window and gives it the focus. The checks that are older than 10 s run again.</summary>
    public void Open()
    {
        if (!open)
        {
            window.Links.CheckOlderThan(RecheckAge);
            window.Links.RefreshUsedBy();
        }

        open = true;
        focus = true;
    }

    /// <summary>Links the game of a folder, for example of a drop on the window. A folder without a game gets no card,
    /// and the row below the button shows why.</summary>
    public void Link(string folder)
    {
        Open();
        linking = folder;
        linkError = null;
        window.Links.Link(folder, report =>
        {
            linking = null;
            linkError = report.IsGame ? null : report.Error;
            window.Links.RefreshUsedBy();
        });
    }

    /// <summary>The linked game whose card is at the position, or null. A folder that the user drops on a card relinks
    /// that game.</summary>
    public string? CardAt(Vector2 position) => open
        ? cards.FirstOrDefault(c => position.X >= c.Value.Min.X && position.Y >= c.Value.Min.Y &&
                                    position.X <= c.Value.Max.X && position.Y <= c.Value.Max.Y).Key
        : null;

    /// <summary>Draws the window while it is open.</summary>
    public void Draw()
    {
        cards.Clear();
        if (!open)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(DefaultSize * scale, ImGuiCond.FirstUseEver);
        if (focus)
        {
            ImGui.SetNextWindowFocus();
            focus = false;
        }

        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.NoCollapse))
        {
            Content();
        }

        ImGui.End();
    }

    private void Content()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.TextWrapped("OpenLancer reads your game files from these folders. It does not change files in them.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        var games = window.Links.Games;
        if (games.Count == 0)
        {
            ImGui.TextDisabled("There is no linked game. Select your game folder to start.");
        }

        // The game of the open profile shows first.
        var openBase = window.Session?.Profile.BaseFolder;
        var ordered = games.OrderBy(g => openBase != null && GameFolders.Same(g.Path, openBase) ? 0 : 1).ToList();
        foreach (var game in ordered)
        {
            Card(game);
        }

        ImGui.Spacing();
        if (ImGui.Button($"{EditorIcons.Link}  Link a game folder..."))
        {
            ChooseFolder(Link);
        }

        ImGui.PushFont(null, EditorTheme.CaptionSize);
        ImGui.TextDisabled("You can also drag a game folder to this window.");
        ImGui.PopFont();
        if (linking != null)
        {
            ImGui.TextDisabled($"Checking {linking}...");
        }
        else if (linkError != null)
        {
            ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.TimesCircle}  {linkError}");
        }
    }

    /// <summary>The card of one linked game.</summary>
    private void Card(LinkedGame game)
    {
        var links = window.Links;
        var report = links.ReportOf(game.Path);
        var checking = links.IsChecking(game.Path);
        var exists = report is not { FolderMissing: true };
        ImGui.PushID(game.Path);
        var start = ImGui.GetCursorScreenPos();
        ImGui.Separator();
        var (glyph, color) = GameLook.Of(report);
        ImGui.TextColored(color, glyph.ToString());
        ImGui.SameLine();
        ImGui.Text(GameLook.Name(report, game));
        ImGui.TextDisabled(game.Path);
        var state = GameLook.State(report, checking);
        ImGui.Text(state);
        if (report is { IsGame: true })
        {
            ImGui.Text($"Export target: {report.Target ?? "none"}");
            ImGui.Text(report.ExePath != null ? "Game program: found" : "Game program: not found (game data only)");
        }

        UsedBy(links.UsedBy(game.Path));
        if (report != null && (report.Findings.Count > 0 || report.Evidence.Count > 0))
        {
            FirstRunCard.Details(report);
        }

        if (links.ErrorOf(game.Path) is { } error)
        {
            ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.TimesCircle}  {error}");
        }

        Buttons(game, report, checking, exists);
        cards[game.Path] = (start, new Vector2(start.X + ImGui.GetContentRegionAvail().X, ImGui.GetCursorScreenPos().Y));
        ImGui.PopID();
    }

    private void UsedBy(IReadOnlyList<string> profiles)
    {
        ImGui.Text("Used by:");
        if (profiles.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("no profile");
            return;
        }

        foreach (var profile in profiles)
        {
            ImGui.SameLine();
            if (ImGui.TextLink(Path.GetFileNameWithoutExtension(profile)))
            {
                window.OpenProfile(profile);
            }

            ImGui.SetItemTooltip(profile);
        }
    }

    private void Buttons(LinkedGame game, GameReport? report, bool checking, bool exists)
    {
        var links = window.Links;
        ImGui.BeginDisabled(checking);
        if (ImGui.Button("Check again"))
        {
            links.Check(game.Path);
            links.RefreshUsedBy();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Relink..."))
        {
            ChooseFolder(folder => links.Relink(game.Path, folder));
        }

        ImGui.SetItemTooltip("Select the new folder of this game, for example after you moved it. The profiles of this " +
                             "game then use the new folder. You can also drag the folder to this card.");
        ImGui.SameLine();
        ImGui.BeginDisabled(!exists);
        if (ImGui.Button("Open folder"))
        {
            OpenFolder(game.Path);
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(report is not { IsGame: true });
        if (ImGui.Button("New profile...") && report != null)
        {
            window.ShowNewProfile([report]);
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Remove link"))
        {
            var name = GameLook.Name(report, game);
            window.Choice.Show("Remove link", $"Remove the link to {name}? OpenLancer does not delete files. A profile " +
                                              "that uses this folder makes the link again when it opens.",
            [
                new Choice("Remove link", () => links.Remove(game.Path)),
                new Choice("Cancel", () => { }),
            ]);
        }
    }

    /// <summary>Opens the folder in the file manager. A file path would start a program, so only a folder that exists
    /// opens.</summary>
    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Shell.OpenCommand(path);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            window.Log.Add(ConsoleSeverity.Error, GameLinks.Source, $"The folder {path} did not open: {e.Message}");
        }
    }

    /// <summary>Opens the folder picker of the system, and gives the folder on the UI thread.</summary>
    private void ChooseFolder(Action<string> chosen)
    {
        try
        {
            FileDialog.ChooseFolder(path => window.QueueUIThread(() =>
            {
                if (path != null)
                {
                    chosen(path);
                }
            }));
        }
        catch (Exception e)
        {
            window.Log.Add(ConsoleSeverity.Error, GameLinks.Source, $"The folder picker did not open: {e.Message}");
        }
    }
}
