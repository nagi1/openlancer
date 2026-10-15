using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.ImUI;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The Cutscenes tab (section 7.2 of the M8 design): each cutscene script of the edit view, with its folder, its
/// duration, its entity and event counts, the INI lines that use it and its state for the active mod. The counts come
/// from a scan that reads each script once. The commands are Open, Revert and Copy path.
/// </summary>
internal sealed class CutsceneBrowser(EditorWindow window, CutsceneWorkspace workspace)
{
    /// <summary>The id of the panel.</summary>
    public const string PanelId = "cutscenes";

    /// <summary>The time that the scan takes in each frame.</summary>
    private static readonly TimeSpan ScanBudget = TimeSpan.FromMilliseconds(8);

    // Sizes in unscaled points.
    private const float IconWidth = 20;
    private const float FolderWidth = 140;
    private const float DotRadius = 3;

    private const ImGuiTableFlags Flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                                          ImGuiTableFlags.Hideable | ImGuiTableFlags.Sortable |
                                          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                                          ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY |
                                          ImGuiTableFlags.SizingFixedFit;

    /// <summary>The columns: the header, the start width in points, and true for a number.</summary>
    private static readonly (string Name, float Width, bool Numeric)[] Columns =
    [
        ("Name", 300, false), ("Folder", 80, false), ("Duration", 80, true), ("Entities", 70, true),
        ("Events", 70, true), ("Used by", 330, true), ("State", 160, false),
    ];

    private static readonly CutsceneFolder[] Folders = Enum.GetValues<CutsceneFolder>();

    private CutsceneCatalog? catalog;
    private GameDataManager? catalogData;
    private readonly Dictionary<string, ThnFileSource> sources = new(VfsPath.Comparer);
    private readonly List<CutsceneEntry> shown = [];
    private string filter = "";
    private CutsceneFolder? folder;

    // The filter, the folder and the scan count of the shown rows. Another value filters and sorts again.
    private (string Filter, CutsceneFolder? Folder, int Scanned)? shownView;
    private (int Column, bool Descending)[] sortKeys = [(0, false)];
    private string? selected;

    /// <summary>Forgets the list of the game data. Call it when the profile closes.</summary>
    public void Reset()
    {
        catalog = null;
        catalogData = null;
        sources.Clear();
        shown.Clear();
        shownView = null;
        selected = null;
    }

    /// <summary>The filter, the folder, the count and the commands of the selected row.</summary>
    public void Toolbar()
    {
        var scale = ImGuiHelper.Scale;
        ToolbarItems.SearchField("filter", "Filter by name or path", ref filter, 220 * scale);
        ImGui.SameLine();
        FolderMenu(FolderWidth * scale);
        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        var row = selected;
        if (ToolbarItems.Button("open", EditorIcons.Open, "Open the selected cutscene (double-click).", row != null))
        {
            workspace.OpenAndShow(row!);
        }

        ImGui.SameLine();
        var revert = row == null ? "Select a cutscene." : RevertProblem(row);
        if (ToolbarItems.Button("revert", EditorIcons.Undo, revert ?? RevertTip, row != null && revert == null))
        {
            Revert(row!);
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("copy", EditorIcons.Copy, "Copy the install path of the selected cutscene.", row != null))
        {
            ImGui.SetClipboardText(row!);
        }

        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (catalog != null)
        {
            var count = $"{shown.Count.ToString("N0", CultureInfo.InvariantCulture)} of " +
                        $"{catalog.Entries.Count.ToString("N0", CultureInfo.InvariantCulture)} cutscenes";
            ImGui.TextDisabled(catalog.Done
                ? count
                : $"{count}, reading {catalog.Scanned.ToString("N0", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>Draws the table, and runs a part of the scan.</summary>
    public void Draw()
    {
        var data = window.Session?.GameData;
        if (data == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (!ReferenceEquals(catalogData, data) || catalog == null)
        {
            catalog = new CutsceneCatalog(VfsFiles.Find(data.Items.VFS, ".thn"));
            catalogData = data;
            sources.Clear();
            shownView = null;
        }

        if (!catalog.Done)
        {
            catalog.Scan(data.Items.VFS.ReadAllBytes, ThornEvaluator.Instance, ScanBudget);
        }

        var view = (filter, folder, catalog.Scanned);
        if (shownView != view)
        {
            shownView = view;
            shown.Clear();
            shown.AddRange(catalog.Entries.Where(e => (folder == null || e.Folder == folder) &&
                (filter.Length == 0 || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))));
            Sort();
        }

        Table();
    }

    private const string RevertTip =
        "Revert: put back the cutscene as the lower mods leave it. The next save deletes the patch of the active mod.";

    /// <summary>Why Revert cannot run for a cutscene, or null.</summary>
    private string? RevertProblem(string path)
    {
        if (window.Session is not { } session)
        {
            return "No profile is open.";
        }

        if (session.NoEditReason is { } reason)
        {
            return reason;
        }

        return Source(path).Kind switch
        {
            ThnFileKind.WholeFileHere or ThnFileKind.NewHere =>
                "The active mod has a whole file for this cutscene, so there are no lower values to put back.",
            ThnFileKind.Lower when !session.Edits.HasUnsavedCutsceneEdits(path) =>
                "The active mod does not change this cutscene.",
            _ => null,
        };
    }

    private void Revert(string path)
    {
        if (window.Edits.Execute(ThnEdits.Revert(path)))
        {
            window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource,
                $"Reverted cutscene {path}. The next save deletes the patch of the active mod.");
        }
    }

    /// <summary>The source of a cutscene for the active mod. It changes only with a save, which loads the data
    /// again.</summary>
    private ThnFileSource Source(string path)
    {
        if (!sources.TryGetValue(path, out var source))
        {
            source = window.Session!.Edits.CutsceneSource(path);
            sources[path] = source;
        }

        return source;
    }

    private void FolderMenu(float width)
    {
        ImGui.SetNextItemWidth(width);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##folder", folder?.ToString() ?? "All folders"))
        {
            if (ImGui.Selectable("All folders", folder == null))
            {
                folder = null;
            }

            foreach (var choice in Folders)
            {
                if (ImGui.Selectable(choice.ToString(), choice == folder))
                {
                    folder = choice;
                }
            }

            ImGui.EndCombo();
        }
        else
        {
            ImGui.SetItemTooltip("The folder group: the folders of DATA\\SCRIPTS, the missions, or the new cutscenes " +
                                 "of the mods in DATA\\MODS.");
        }

        EditorTheme.PopMenuColors();
    }

    private unsafe void Table()
    {
        var scale = ImGuiHelper.Scale;
        if (!ImGui.BeginTable("##cutscenes", Columns.Length + 1, Flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize |
                                         ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoSort |
                                         ImGuiTableColumnFlags.NoReorder | ImGuiTableColumnFlags.NoHeaderLabel,
            MathF.Truncate(IconWidth * scale));
        for (var c = 0; c < Columns.Length; c++)
        {
            var (name, width, numeric) = Columns[c];
            var flags = ImGuiTableColumnFlags.WidthFixed;
            if (c == 0)
            {
                flags |= ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.DefaultSort;
            }

            if (numeric)
            {
                flags |= ImGuiTableColumnFlags.PreferSortDescending;
            }

            ImGui.TableSetupColumn(name, flags, MathF.Truncate(width * scale), (uint)(c + 1));
        }

        EditorTheme.PushMenuColors();
        var specs = ImGui.TableGetSortSpecs();
        EditorTheme.PopMenuColors();
        ImGui.TableHeadersRow();
        if (specs != null && specs.SpecsDirty)
        {
            var spec = (ImGuiTableColumnSortSpecs*)specs.Specs.Handle;
            sortKeys = Enumerable.Range(0, specs.SpecsCount)
                .Select(i => ((int)spec[i].ColumnUserID - 1, spec[i].SortDirection == ImGuiSortDirection.Descending))
                .ToArray();
            specs.SpecsDirty = false;
            Sort();
        }

        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        var clipper = new ImGuiListClipper();
        clipper.Begin(shown.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                Row(shown[i], i, focused);
            }
        }

        clipper.End();
        ImGui.EndTable();
    }

    private void Row(CutsceneEntry entry, int index, bool focused)
    {
        var scale = ImGuiHelper.Scale;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(index);
        var isSelected = VfsPath.Comparer.Equals(selected, entry.Path);
        var isOpen = VfsPath.Comparer.Equals(workspace.Path, entry.Path);
        var cell = ImGui.GetCursorScreenPos();
        var cellWidth = ImGui.GetContentRegionAvail().X;
        var selection = ListRow.SelectionColor(focused);
        ImGui.PushStyleColor(ImGuiCol.Header, selection);
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, selection);
        }

        if (ImGui.Selectable("##row", isSelected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
        {
            selected = entry.Path;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                workspace.OpenAndShow(entry.Path);
            }
        }

        ImGui.PopStyleColor(isSelected ? 2 : 1);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            selected = entry.Path;
        }

        var uses = workspace.References().UsedBy(entry.Path);
        if (ImGui.BeginPopupContextItem("##row-menu"))
        {
            EditorTheme.PushMenuColors();
            RowMenu(entry, uses);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // The glyph of the row. The open cutscene has the accent color.
        var icon = EditorIcons.Film.ToString();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(new Vector2(cell.X + MathF.Truncate((cellWidth - ImGui.CalcTextSize(icon).X) / 2), cell.Y),
            ImGui.GetColorU32(isOpen ? EditorTheme.AccentText : EditorTheme.TextDim), icon);

        var counts = entry.Counts;
        Cell(0, entry.Name, false, entry.Path);
        Cell(1, entry.Folder.ToString(), false, null);
        Cell(2, counts == null ? "..." : counts.Problem != null ? "" : Number(counts.Duration, "0.###"), true,
            counts?.Problem);
        Cell(3, counts is { Problem: null } ? Number(counts.Entities, "N0") : "", true, null);
        Cell(4, counts is { Problem: null } ? Number(counts.Events, "N0") : "", true, null);
        if (ImGui.TableSetColumnIndex(6))
        {
            UsedBy(uses);
        }

        if (ImGui.TableSetColumnIndex(7))
        {
            var state = CutsceneCatalog.StateText(Source(entry.Path));
            ImGui.Text(state);
            if (window.Session?.Edits.HasUnsavedCutsceneEdits(entry.Path) == true)
            {
                // A dot after the state marks unsaved edits.
                var radius = DotRadius * scale;
                var max = ImGui.GetItemRectMax();
                drawList.AddCircleFilled(new Vector2(max.X + 4 * scale + radius, (ImGui.GetItemRectMin().Y + max.Y) / 2),
                    radius, ImGui.GetColorU32(EditorTheme.AccentText));
                ImGui.SetItemTooltip("The cutscene has unsaved edits.");
            }
        }

        ImGui.PopID();
    }

    /// <summary>The text of a column, right-aligned for a number, with a tooltip or null.</summary>
    private static void Cell(int column, string text, bool numeric, string? tooltip)
    {
        if (!ImGui.TableSetColumnIndex(column + 1))
        {
            return;
        }

        if (numeric)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() +
                                Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X));
        }

        ImGui.Text(text);
        if (tooltip != null)
        {
            ImGui.SetItemTooltip(tooltip);
        }
    }

    /// <summary>
    /// The INI lines that use a cutscene: the count and the first line. A click on the line opens its section in the
    /// Inspector. The tooltip lists all lines.
    /// </summary>
    private void UsedBy(IReadOnlyList<ThnReference> uses)
    {
        if (uses.Count == 0)
        {
            ImGui.TextDisabled("none");
            ImGui.SetItemTooltip("No INI line of the game data names this cutscene.");
            return;
        }

        var first = Describe(uses[0]);
        if (ImGui.TextLink(uses.Count == 1 ? first : $"{first} and {uses.Count - 1} more"))
        {
            ShowSection(uses[0]);
        }

        ImGui.SetItemTooltip(string.Join("\n", uses.Select(Describe)) + "\n\nClick to show the first line in the Inspector. " +
                             "The menu of the row shows each line.");
    }

    private void RowMenu(CutsceneEntry entry, IReadOnlyList<ThnReference> uses)
    {
        if (Theme.IconMenuItem(EditorIcons.Open, "Open", true))
        {
            workspace.OpenAndShow(entry.Path);
        }

        var revert = RevertProblem(entry.Path);
        if (Theme.IconMenuItem(EditorIcons.Undo, "Revert", revert == null))
        {
            Revert(entry.Path);
        }

        ImGui.SetItemTooltip(revert ?? RevertTip);
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy path", true))
        {
            ImGui.SetClipboardText(entry.Path);
        }

        if (uses.Count == 0)
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextDisabled("Used by");
        foreach (var use in uses)
        {
            if (ImGui.MenuItem(Describe(use)))
            {
                ShowSection(use);
            }
        }
    }

    /// <summary>Selects the section of an INI line, so that the Inspector shows it.</summary>
    private void ShowSection(ThnReference use)
    {
        if (workspace.SectionOf(use) is { } section)
        {
            window.Selection = section;
        }
        else
        {
            window.Log.Add(ConsoleSeverity.Warning, EditorWindow.EditorSource,
                $"The edit view has no single section [{use.Section}] in {use.File} with {use.Key} = {use.Value}.");
        }
    }

    /// <summary>A line that names a cutscene, for example "M000_s002xe_Li01_01_nrml.ini [CharacterEncounter] action".</summary>
    internal static string Describe(ThnReference use) =>
        $"{System.IO.Path.GetFileName(use.File)} [{use.Section}] {use.Key}";

    /// <summary>Sorts the shown rows by the sort keys of the table. Rows with the same keys keep the order of the
    /// paths. A row that the scan did not read yet sorts after the others.</summary>
    private void Sort()
    {
        var order = catalog!.Entries.Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i);
        var references = workspace.References();
        shown.Sort((a, b) =>
        {
            foreach (var (column, descending) in sortKeys)
            {
                var result = Compare(a, b, column, references);
                if (result != 0)
                {
                    return descending ? -result : result;
                }
            }

            return order[a].CompareTo(order[b]);
        });
    }

    private int Compare(CutsceneEntry a, CutsceneEntry b, int column, ThnReferences references) => column switch
    {
        0 => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        1 => a.Folder.CompareTo(b.Folder),
        2 => Value(a, c => c.Duration).CompareTo(Value(b, c => c.Duration)),
        3 => Value(a, c => c.Entities).CompareTo(Value(b, c => c.Entities)),
        4 => Value(a, c => c.Events).CompareTo(Value(b, c => c.Events)),
        5 => references.UsedBy(a.Path).Count.CompareTo(references.UsedBy(b.Path).Count),
        _ => string.Compare(CutsceneCatalog.StateText(Source(a.Path)), CutsceneCatalog.StateText(Source(b.Path)),
            StringComparison.Ordinal),
    };

    /// <summary>A count of a row, or -1 when the scan did not read the row or could not read the script.</summary>
    private static double Value(CutsceneEntry entry, Func<CutsceneCounts, double> value) =>
        entry.Counts is { Problem: null } counts ? value(counts) : -1;

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
