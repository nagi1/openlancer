using System;
using System.Linq;
using System.Text;
using ImGuiNET;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The "Sold at" section of a good: each <c>marketgood</c> line of all market files that sells it, by base. A line of
/// a <c>[BaseGood]</c> that the active mod adds can change or go. A patch cannot change a line of a lower
/// layer, so such a line is read-only. "Add to base" makes one more base sell the good (see <see cref="MarketEdits"/>).
/// </summary>
internal sealed class MarketSection(EditorWindow window, PropertyGrid grid)
{
    private const string LowerLineReason =
        "A market line of a lower layer. A patch cannot change or remove it. Use a whole-file override for that.";

    private readonly FilterList<string> bases = new();
    private ListView? basesList;

    /// <param name="good">The nickname of the good.</param>
    /// <param name="key">The key of the open state of the section in the settings, "Type.Section".</param>
    public void Draw(string good, string key, string title = "Sold at")
    {
        if (window.Session is not { } session || !grid.BeginSection(key, EditorIcons.Store, title))
        {
            return;
        }

        var lines = session.Edits.MarketLines(good);
        if (lines.Count == 0)
        {
            grid.Row("Bases", () => PropertyGrid.Field(null, hint: "No base sells it"));
        }

        foreach (var line in lines)
        {
            grid.Row(line.Base, () => Line(session, line));
        }

        grid.Row("Add to base", () => AddToBase(session, good));
        grid.EndSection();
    }

    /// <summary>The values of a line after the good: a field that the user can edit and a remove button, or a
    /// read-only field.</summary>
    private void Line(EditorSession session, MarketLine line)
    {
        var text = MarketEdits.ValuesText(line);
        var editable = session.CanEdit && MarketEdits.CanChange(session.Edits, line);
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var buttonWidth = ToolbarItems.IconWidth;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - buttonWidth - spacing);
        if (!editable)
        {
            PropertyGrid.Field(text, width);
            ImGui.SetItemTooltip("rank, reputation, min, max, preserve, price multiplier\n\n" +
                                 (session.NoEditReason ?? LowerLineReason));
            ImGui.SameLine(0, spacing);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(EditorIcons.Lock.ToString());
            return;
        }

        var edited = text;
        ImGui.SetNextItemWidth(width);
        var changed = ImGui.InputText("##values", ref edited, (uint)Encoding.UTF8.GetByteCount(edited) + 256);
        // The edit runs when the field ends, also when the window closes, as the other text fields do.
        grid.CommitField(MarketEdits.LineKey(line), changed, text, edited, (values, _) =>
        {
            // Edits.Execute writes a problem of the edit to the Console, so the commit returns no error.
            window.Edits.Execute(() => MarketEdits.SetLine(session.Edits, line, values));
            return null;
        });
        ImGui.SetItemTooltip("rank, reputation, min, max, preserve, price multiplier");

        ImGui.SameLine(0, spacing);
        if (ToolbarItems.Button("remove", EditorIcons.TrashAlt, $"Stop selling it at {line.Base}"))
        {
            window.Edits.Execute(() => MarketEdits.SetLine(session.Edits, line, null));
        }
    }

    /// <summary>A list of the bases of the data list (milestone M21), so a base that the active mod adds and did not save
    /// is in it. The chosen base gets a line that sells the good.</summary>
    private void AddToBase(EditorSession session, string good)
    {
        var list = session.Edits.Lists.List("base");
        if (!ReferenceEquals(list, basesList))
        {
            basesList = list;
            bases.Entries = list == null
                ? []
                : FilterList<string>.Sorted(list.Entries.Select(b => new FilterEntry<string>(b.Value,
                    b.Section != null && session.SectionName(b.Section) is { Length: > 0 } name ? name : b.Value, b.Value,
                    EditorIcons.Building)));
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(!session.CanEdit || list == null);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##add-base", "Choose a base...", ImGuiComboFlags.HeightLargest))
        {
            if (bases.Draw(_ => false) is { } chosen)
            {
                ImGui.CloseCurrentPopup();
                var values = MarketEdits.DefaultValues(MarketEdits.Category(session.Edits, good));
                window.Edits.Execute(() => MarketEdits.AddToBase(session.Edits, good, chosen.Item, values));
            }

            ImGui.EndCombo();
        }

        EditorTheme.PopMenuColors();
        ImGui.EndDisabled();
        ImGui.SetItemTooltip(session.NoEditReason ??
                             "Makes the base sell the good: a line in a [BaseGood] of the base that the active mod adds.");
    }
}
