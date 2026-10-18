using System;
using System.Collections.Generic;
using ImGuiNET;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>
/// Draws a game folder check with the parts of <see cref="CardLayout"/>: the status row, the facts, the notes and the
/// Details table. The first-run screen and the Game link window use it, so a game looks the same in both. Each note
/// shows once: the first notes above Details, the other notes in Details (<see cref="GameDetails"/>).
/// </summary>
internal static class GameReportView
{
    // The widths of the columns of the Details table, as parts of the table width.
    private const float CheckColumn = 0.36f;
    private const float ResultColumn = 0.64f;

    /// <summary>The status row of a game folder: the glyph of its severity, its name in bold and its state
    /// line.</summary>
    public static void Status(GameReport? report, string name, string state)
    {
        var (glyph, color) = GameLook.Of(report);
        CardLayout.StatusRow(glyph, color, name, state);
    }

    /// <summary>The rows "Export target" and "Game program" of a game, in a table of facts.</summary>
    public static void GameFacts(GameReport report)
    {
        CardLayout.Fact("Export target", report.Target ?? "none");
        CardLayout.Fact("Game program", report.ExePath != null ? "found" : "not found (game data only)");
    }

    /// <summary>The notes above Details, then a line with the number of notes in Details. Returns the notes for
    /// <see cref="Details"/>.</summary>
    public static IReadOnlyList<GameFinding> Notes(GameReport report)
    {
        var (shown, more) = GameDetails.Split(GameDetails.Notes(report));
        foreach (var note in shown)
        {
            Note(note);
        }

        if (more.Count > 0)
        {
            CardLayout.Dim($"Details shows {more.Count} more lines.");
        }

        return more;
    }

    /// <summary>The collapsed Details section: the notes that do not show above it, then the table of the check with
    /// the columns Check and Result. It does not show when it has nothing.</summary>
    public static void Details(GameReport report, IReadOnlyList<GameFinding> more)
    {
        var rows = GameDetails.Rows(report);
        if ((rows.Count == 0 && more.Count == 0) || !ImGui.TreeNodeEx("Details", ImGuiTreeNodeFlags.NoTreePushOnOpen))
        {
            return;
        }

        foreach (var note in more)
        {
            Note(note);
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter |
                                      ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings |
                                      ImGuiTableFlags.SizingStretchProp;
        if (rows.Count > 0 && ImGui.BeginTable("##details", 2, flags))
        {
            ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthStretch, CheckColumn);
            ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, ResultColumn);
            ImGui.TableHeadersRow();
            for (var i = 0; i < rows.Count; i++)
            {
                ImGui.PushID(i);
                Row(rows[i]);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    /// <summary>One finding with the glyph of its severity.</summary>
    public static void Note(GameFinding finding)
    {
        var (glyph, color) = GameLook.Of(finding.Severity);
        CardLayout.Note(glyph, color, finding.Text);
    }

    /// <summary>A row of the Details table. A row with a hash shows its short form, the full value in the tooltip,
    /// and a Copy button after it.</summary>
    private static void Row(DetailRow row)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.TextWrapped(row.Check);
        ImGui.PopStyleColor();
        ImGui.TableSetColumnIndex(1);
        if (row.Hash is not { } hash)
        {
            ImGui.TextWrapped(row.Result);
            return;
        }

        // The text wraps before the Copy button, so the button stays in the column.
        var button = CardLayout.IconButtonWidth(EditorIcons.Copy) + ImGui.GetStyle().ItemSpacing.X;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(1, ImGui.GetContentRegionAvail().X - button));
        ImGui.TextWrapped(row.Result);
        ImGui.PopTextWrapPos();
        ImGui.SetItemTooltip($"SHA-256 {hash}");
        ImGui.SameLine();
        if (CardLayout.IconButton(EditorIcons.Copy, "Copy the SHA-256 value."))
        {
            ImGui.SetClipboardText(hash);
        }
    }
}
