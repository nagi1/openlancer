using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Dialogs;

/// <summary>
/// A picker of one entry of a list, as the Unity object picker: a search field with the focus, the entries sorted by
/// name, and a footer with the selected entry. Enter and a double-click pick the selected entry. Esc and Cancel pick
/// nothing (decision D233 of the M3 design). The Create of the Scene uses it for the archetype and the base of a new
/// object, and "Add component" for the value of a component. A spawn of a ship of the Assets panel adds a second button,
/// "New static prop archetype" (decision D278 of the M23 design). Esc never runs the second button.
/// </summary>
internal sealed class PickDialog : ModalDialog
{
    // Sizes in unscaled points.
    private const int VisibleRows = 14;

    private string title = "Pick";
    private string prompt = "";
    private string cancelLabel = "Cancel";
    private ObjectKind kind = ObjectKinds.Unknown;
    private ListPickerModel model = new([], false, "");
    private IReadOnlyDictionary<ListEntry, string> details = new Dictionary<ListEntry, string>();
    private string filter = "";
    private int selected = -1;
    private Action<ListEntry>? pick;
    private Action? cancel;
    private string? secondLabel;
    private Action? second;

    protected override string Title => $"{title}###pick";

    /// <param name="prompt">The text above the search field.</param>
    /// <param name="entries">The entries with their display names, and an optional detail text for each.</param>
    /// <param name="pick">Gets the picked entry.</param>
    /// <param name="cancelLabel">The label of the button that picks nothing, for example "No base".</param>
    /// <param name="cancel">What Esc and the cancel button do, or null for nothing.</param>
    /// <param name="secondLabel">The label of an optional second button, or null for no second button.</param>
    /// <param name="second">What the second button does. Esc never runs it.</param>
    public void Show(string title, string prompt, ObjectKind kind, IReadOnlyList<(ListEntry Entry, string Name, string Detail)> entries,
        Action<ListEntry> pick, string cancelLabel = "Cancel", Action? cancel = null, string? secondLabel = null,
        Action? second = null)
    {
        this.title = title;
        this.prompt = prompt;
        this.kind = kind;
        this.cancelLabel = cancelLabel;
        this.pick = pick;
        this.cancel = cancel;
        this.secondLabel = secondLabel;
        this.second = second;
        var rows = new List<(ListEntry, string)>();
        var detail = new Dictionary<ListEntry, string>(ReferenceEqualityComparer.Instance);
        foreach (var (entry, name, text) in entries)
        {
            rows.Add((entry, name));
            detail[entry] = text;
        }

        model = new ListPickerModel(rows, false, "");
        details = detail;
        filter = "";
        selected = model.Rows.Count > 0 ? 0 : -1;
        Open();
    }

    protected override void Body()
    {
        if (prompt.Length > 0)
        {
            ImGui.TextWrapped(prompt);
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##filter", $"{EditorIcons.MagnifyingGlass}  Search", ref filter, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var visible = model.Visible(filter);
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            selected = ListPickerModel.Step(visible, selected, ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : -1);
        }

        var picked = enter ? ListPickerModel.EnterRow(visible, selected) : -1;
        var height = Math.Min(Math.Max(visible.Count, 1), VisibleRows) * ImGui.GetTextLineHeightWithSpacing() +
                     ImGui.GetStyle().WindowPadding.Y;
        ImGui.BeginChild("##rows", new Vector2(MathF.Truncate(420 * ImGuiHelper.Scale), height));
        if (visible.Count == 0)
        {
            ImGui.TextDisabled("Nothing matches.");
        }

        foreach (var index in visible)
        {
            var row = model.Rows[index];
            if (ImGui.Selectable($"{kind.Icon}  {row.Text}##row{index}", index == selected,
                    ImGuiSelectableFlags.AllowDoubleClick))
            {
                selected = index;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    picked = index;
                }
            }

            var detail = row.Entry != null ? details.GetValueOrDefault(row.Entry) ?? "" : "";
            var dim = string.Equals(row.Name, row.Value, StringComparison.OrdinalIgnoreCase) ? detail : $"{row.Value}  {detail}";
            if (dim.Trim().Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(dim.Trim());
            }
        }

        ImGui.EndChild();
        ImGui.Separator();
        if (Button("Pick", primary: true, enabled: selected >= 0 && visible.Contains(selected)))
        {
            picked = selected;
        }

        if (secondLabel != null)
        {
            ImGui.SameLine();
            if (Button(secondLabel))
            {
                Close();
                second?.Invoke();
                return;
            }
        }

        ImGui.SameLine();
        if (Button(cancelLabel))
        {
            Close();
            cancel?.Invoke();
            return;
        }

        if (picked >= 0 && model.Rows[picked].Entry is { } entry)
        {
            Close();
            pick?.Invoke(entry);
        }
    }

    /// <summary>Esc and the close button run the cancel action only, never the second button.</summary>
    protected override void Cancelled() => cancel?.Invoke();
}
