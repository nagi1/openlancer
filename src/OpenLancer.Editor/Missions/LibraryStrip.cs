using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;
using OpenLancer.Runtime.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>The payloads of the drags of the mission studio: an item of the library, and a row of a node at the lines
/// level. The payload holds an index into a table, because ImGui copies its bytes.</summary>
internal static class MissionDrag
{
    /// <summary>The payload type of an item of the library.</summary>
    public const string ItemType = "OL_MISSION_ITEM";

    /// <summary>The payload type of an action row of the graph.</summary>
    public const string RowType = "OL_MISSION_ROW";

    private static readonly Dictionary<int, object> Table = [];
    private static int next;

    /// <summary>Sets the payload of a drag source. Call it between <c>BeginDragDropSource</c> and
    /// <c>EndDragDropSource</c>.</summary>
    public static unsafe void Source(string type, object item, string preview)
    {
        // One drag runs at a time, and its source sets the payload again in each frame.
        Table.Clear();
        Table[++next] = item;
        var index = next;
        ImGui.SetDragDropPayload(type, (IntPtr)(&index), sizeof(int));
        ImGui.Text(preview);
    }

    /// <summary>The item of the payload of a type that a drag carries now, or null.</summary>
    public static unsafe object? Payload(string type)
    {
        var payload = ImGui.GetDragDropPayload();
        if (payload == null || !payload.IsDataType(type) || payload.DataSize != sizeof(int))
            return null;
        return Table.GetValueOrDefault(*(int*)payload.Data);
    }
}

/// <summary>
/// The library strip of the Mission tab (section 8.3 of the M6b design, decision D685): a strip at the left of the graph
/// with a search field and the groups "Events", "Actions" and "Conditions". Each item has its glyph, its plain name and
/// its info icon. A drag of an item into the graph adds it there, and a double-click adds it at the center of the view or
/// to the selected trigger. The model of the items is <see cref="MissionLibrary"/>.
/// </summary>
internal sealed class LibraryStrip(EditorWindow window)
{
    /// <summary>The width of the strip, in points.</summary>
    public const float Width = 240;

    /// <summary>The narrowest Mission tab that shows the strip, in points. A narrower tab closes it.</summary>
    public const float MinTabWidth = 900;

    /// <summary>True when the strip shows (section 8.3 of the M6b design): the setting is on, and the tab is wide or the
    /// toggle opened the strip in the narrow tab. The toggle shows this state.</summary>
    /// <param name="narrow">True for a Mission tab that is narrower than <see cref="MinTabWidth"/>.</param>
    /// <param name="openedInNarrowTab">True when the toggle opened the strip in the narrow tab.</param>
    public static bool Shows(bool setting, bool narrow, bool openedInNarrowTab) => setting && (!narrow || openedInNarrowTab);

    /// <summary>The state after a click on the toggle. In a wide tab, the click sets the setting. In a narrow tab, the
    /// click opens or closes the strip for the narrow tab only, and an open strip turns the setting on.</summary>
    /// <param name="shown">True when the click opens the strip.</param>
    /// <returns>The setting, and true when the strip shows in the narrow tab.</returns>
    public static (bool Setting, bool InNarrowTab) Toggled(bool setting, bool narrow, bool shown) =>
        narrow ? (setting || shown, shown) : (shown, false);

    private static readonly HelpText Help = new("Library",
        "The prefabs, events, actions and conditions that you can add to the mission.",
        "Nothing until you drop an item. Each item writes [Trigger] sections or lines.");

    private string search = "";

    /// <summary>The sections of the library, with the OpenLancer marks while the OpenLancer column shows (UI rule
    /// 4).</summary>
    public IReadOnlyList<LibrarySection> Sections =>
        MissionLibrary.Studio(window.Session?.Edits.HidesOpenLancer != true);

    /// <summary>Draws the strip. Returns the item of a double-click, or null.</summary>
    /// <param name="readOnly">The reason why the session takes no edits, or null.</param>
    public LibraryItem? Draw(Vector2 size, string? readOnly)
    {
        LibraryItem? picked = null;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Panel);
        var visible = ImGui.BeginChild("##library", size, ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleColor();
        if (visible)
        {
            ImGui.TextDisabled("Library");
            ImGui.SameLine();
            InfoIcon.Draw("library-help", Help);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##library-search", "Search the library", ref search, 128);
            if (readOnly != null)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextDisabled(readOnly);
                ImGui.PopTextWrapPos();
            }

            var filter = search.Trim();
            foreach (var section in Sections)
            {
                var groups = section.Groups.Select(g => g with
                    {
                        Items = MissionLibrary.Search([new LibrarySection(section.Title, [g])], filter),
                    })
                    .Where(g => g.Items.Count > 0).ToList();
                if (groups.Count == 0)
                {
                    continue;
                }

                var flags = filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
                if (filter.Length > 0)
                {
                    ImGui.SetNextItemOpen(true);
                }

                if (!ImGui.CollapsingHeader(section.Title, flags | (section.Title == "Events" ? ImGuiTreeNodeFlags.DefaultOpen : 0)))
                {
                    continue;
                }

                foreach (var group in groups)
                {
                    var single = groups.Count == 1 && group.Title == section.Title;
                    if (!single)
                    {
                        if (filter.Length > 0)
                        {
                            ImGui.SetNextItemOpen(true);
                        }

                        if (!ImGui.TreeNode($"{group.Title}##{section.Title}"))
                        {
                            continue;
                        }
                    }

                    foreach (var item in group.Items)
                    {
                        if (Item(item, readOnly == null))
                        {
                            picked = item;
                        }
                    }

                    if (!single)
                    {
                        ImGui.TreePop();
                    }
                }
            }
        }

        ImGui.EndChild();
        return picked;
    }

    /// <summary>One item: the glyph and the plain name, the marks, the info icon, the drag source and the
    /// double-click. Returns true for a double-click.</summary>
    private static bool Item(LibraryItem item, bool editable)
    {
        ImGui.PushID($"{item.Kind}.{item.Id}");
        var dim = item.NoRetailExample;
        if (dim)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
        }

        var infoWidth = InfoIcon.Width;
        ImGui.Selectable($"{item.Glyph}  {item.Name}", false, ImGuiSelectableFlags.AllowDoubleClick,
            new Vector2(ImGui.GetContentRegionAvail().X - infoWidth - ImGui.GetStyle().ItemSpacing.X, 0));
        if (dim)
        {
            ImGui.PopStyleColor();
        }

        var doubleClicked = editable && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if (ImGui.IsItemHovered() && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var lines = new List<string> { item.Help.WhatItIs, item.Key };
            if (item.NoRetailExample)
            {
                lines.Add("No retail example: the game data has no example of this item. Its effect is not checked.");
            }

            if (item.OpenLancerMark != null)
            {
                lines.Add($"{item.OpenLancerMark}. {item.OpenLancerTip}");
            }

            ImGui.SetTooltip(string.Join("\n", lines));
        }

        if (editable && ImGui.BeginDragDropSource())
        {
            MissionDrag.Source(MissionDrag.ItemType, item, $"{item.Glyph}  {item.Name}");
            ImGui.EndDragDropSource();
        }

        ImGui.SameLine();
        InfoIcon.Draw("info", item.Help);
        if (item.OpenLancerMark != null || item.NoRetailExample)
        {
            ImGui.Indent(ImGui.GetFontSize() * 1.5f);
            ImGui.TextDisabled(item.NoRetailExample ? "No retail example" : item.OpenLancerMark!);
            ImGui.Unindent(ImGui.GetFontSize() * 1.5f);
        }

        ImGui.PopID();
        return doubleClicked;
    }
}
