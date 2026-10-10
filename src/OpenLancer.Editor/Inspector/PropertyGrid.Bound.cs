using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The bound rows of the grid. Inside a <see cref="Bind"/> scope, a row with an INI key that the schema of the section
/// has (or that the section has) shows the lines of the edit view, and the user can edit them. The page code keeps its
/// row calls: each row method asks <see cref="BoundRow"/> first.
/// </summary>
/// <remarks>
/// <para>Numbers and text edit as text with invariant parsing, so a float value stays exact. A bool gets a checkbox,
/// an enum a list and a vector three fields. A text key, for example <c>ids_name</c>, edits the text of the active
/// mod.</para>
/// <para>Each text field is one edit of the history: the edit starts when the field becomes active, each change shows
/// at once, and the edit ends when the field ends. Escape cancels the edit (see <see cref="FieldEditRouter"/>).</para>
/// <para>While a field edit is open, its row keeps the widgets that it had at the start of the edit, and a bound row
/// has the ImGui id of its key. So the active widget stays with its key when the edit changes the rows.</para>
/// <para>Each bound row has marks: a blue bar and a blue label when the active mod sets the value, a warning glyph
/// when a later mod changes it, and a lock when the row is read-only. The tooltip of the label tells which layer sets
/// the value. The menu of the label has Revert and Copy INI line.</para>
/// </remarks>
internal sealed partial class PropertyGrid
{
    // Sizes in unscaled points.
    private const float OriginBarWidth = 3;
    private const int MultilineRows = 4;

    private readonly Dictionary<SectionRef, BoundSection?> boundSections = [];
    private readonly FieldEditRouter fieldEdits =
        new(message => window.Log.Add(Logs.ConsoleSeverity.Warning, EditController.Source, message));

    private (EditorSession? Session, int Version, FieldGesture? Edit) boundVersion;
    private BoundSection? bound;

    /// <summary>True while a field edit is open. The edit history and the save wait for its end.</summary>
    public bool IsEditing => fieldEdits.Open != null;

    /// <summary>The open field edit, or null.</summary>
    public FieldGesture? OpenEdit => fieldEdits.Open;

    /// <summary>
    /// Binds the rows of a scope to a section of the edit view. Null ends the binding for the scope, for example for
    /// rows that show values of another section. Dispose the result to end the scope.
    /// </summary>
    public BindScope Bind(SectionRef? section)
    {
        var previous = bound;
        bound = section == null ? null : Section(section);
        return new BindScope(this, previous);
    }

    /// <summary>The end of a <see cref="Bind"/> scope.</summary>
    internal readonly struct BindScope(PropertyGrid grid, BoundSection? previous) : IDisposable
    {
        public void Dispose() => grid.bound = previous;
    }

    /// <summary>Cancels an open field edit and forgets the bindings. Call it when the profile closes.</summary>
    public void Reset()
    {
        fieldEdits.End(keep: false);
        boundSections.Clear();
        boundVersion = default;
    }

    /// <summary>
    /// Ends a field edit whose field did not draw in the last frame, for example because the selection changed. Call
    /// it once each frame before the rows draw.
    /// </summary>
    public void EndLostEdit() => fieldEdits.EndLost(ImGui.GetFrameCount());

    /// <summary>Ends an open field edit and keeps its text. Call it when the window closes, so that the recovery file
    /// keeps the edit.</summary>
    public void EndEdit() => fieldEdits.End(keep: true);

    /// <summary>The binding of a key of the current scope, or null when the row is not bound.</summary>
    private FieldBinding? Binding(string? iniKey) => iniKey == null ? null : bound?.Field(iniKey);

    /// <summary>The bound section of a reference for this edit version, or null when the edit view has no such
    /// section.</summary>
    private BoundSection? Section(SectionRef section)
    {
        if (window.Session is not { } session)
        {
            return null;
        }

        // An edit, an undo, a redo or a save changes the version. The bindings of the version before are old. The
        // start and the end of a field edit change which key stays bound (see BoundSection).
        var open = fieldEdits.Open;
        if (boundVersion != (session, session.Version, open))
        {
            boundSections.Clear();
            boundVersion = (session, session.Version, open);
        }

        if (!boundSections.TryGetValue(section, out var result))
        {
            var view = session.Edits.Get(section);
            var editing = open != null && open.Binding.Section.Equals(section) ? open.Binding.Name : null;
            result = view == null ? null : new BoundSection(session.Edits, section, view, editing);
            boundSections[section] = result;
        }

        return result;
    }

    /// <summary>
    /// Draws a bound row when the key of the row is bound. Returns false for an unbound row, which the caller then
    /// draws as before.
    /// </summary>
    /// <param name="unset">The value that the engine uses when no line sets the key, as text.</param>
    private bool BoundRow(string label, string? iniKey, string unset)
    {
        if (Binding(iniKey) is not { } field)
        {
            return false;
        }

        var reason = field.ReadOnlyReason ?? window.Session!.NoEditReason;
        BeginRow(label, iniKey, field, reason);
        var hint = $"Not set. The engine uses {unset}.";
        // While a field edit of the key is open, the row draws the widgets of the key at the start of the edit. So the
        // active widget stays, also when the edit changes the number of lines.
        var shape = fieldEdits.StartOf(field) ?? field;
        if (field.Key?.TextKind is { } kind)
        {
            TextKeyField(field, kind, reason);
        }
        else if (reason != null)
        {
            // A read-only field has one line, so a bar separates the lines of the key.
            Field(field.Text.Replace("\n", " | "), hint: field.Lines.Count == 0 ? hint : "None");
        }
        else if (field.Key?.ClrType == typeof(bool) && shape.Lines.Count <= 1)
        {
            BoolField(field, unset);
        }
        else if (field.Key?.ClrType is { IsEnum: true } type && shape.Lines.Count <= 1)
        {
            EnumField(field, type, hint);
        }
        else if (field.Key is { ClrType: var vector, Multiline: false } && vector == typeof(Vector3) &&
                 shape.Lines.Count == 1 && FieldEdit.Values(shape.Text).Length == 3)
        {
            // An edit of an axis keeps three values: the schema allows at most three, and an empty value is not valid.
            VectorField(field);
        }
        else
        {
            TextField(field, shape.Lines.Count, hint);
        }

        EndRow();
        return true;
    }

    /// <summary>A text field for the lines of a key: one line, or a field with more lines for a key that the engine
    /// reads more than one time.</summary>
    /// <param name="startLines">The number of lines of the key at the start of an open edit, or now.</param>
    private void TextField(FieldBinding field, int startLines, string hint)
    {
        var text = field.Text;
        var multiline = field.Key?.Multiline == true || startLines > 1;
        bool changed;
        ImGui.SetNextItemWidth(-1);
        if (multiline)
        {
            var lines = Math.Clamp(field.Lines.Count + 1, 2, MultilineRows);
            var height = lines * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y;
            changed = ImGui.InputTextMultiline("##value", ref text, Capacity(text), new Vector2(-1, height));
        }
        else
        {
            changed = ImGui.InputTextWithHint("##value", hint, ref text, Capacity(text));
        }

        Route(field, changed, _ => text);
        FieldError(field);
    }

    /// <summary>Three fields with an X, Y and Z prefix. Each field edits one value of the line. Each field is its own
    /// field edit.</summary>
    private void VectorField(FieldBinding field)
    {
        AxisFields(FieldEdit.Values(field.Text), (index, text, width) =>
        {
            var value = text;
            ImGui.SetNextItemWidth(width);
            var changed = ImGui.InputText("##value", ref value, Capacity(value));
            // The other values come from the line at the start of the edit.
            Route(field, changed, start => FieldEdit.WithValue(start.Text, index, value));
        });
        FieldError(field);
    }

    private void BoolField(FieldBinding field, string unset)
    {
        // A line without a value is true, for example 'separable ='.
        var value = field.Lines is [{ Values: var values }]
            ? values.Count == 0 || values[0].TryToBoolean(out var truth) && truth
            : unset == "true";
        if (ImGui.Checkbox("##value", ref value))
        {
            window.Edits.Execute(field.EditLines([value ? "true" : "false"]));
        }

        if (field.Lines.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Not set");
        }
    }

    private void EnumField(FieldBinding field, Type type, string hint)
    {
        ImGui.SetNextItemWidth(-1);
        var preview = field.Lines.Count == 0 ? hint : field.Text;
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##value", preview))
        {
            foreach (var name in System.Enum.GetNames(type))
            {
                var selected = name.Equals(field.Text, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(name, selected) && !selected)
                {
                    window.Edits.Execute(field.EditLines([name]));
                }
            }

            ImGui.EndCombo();
        }

        EditorTheme.PopMenuColors();
    }

    /// <summary>
    /// A key that holds a text id, for example <c>ids_name</c>: the field shows the text and edits the text of the
    /// active mod. The edit runs when the field ends, because it reads the text of the mod when it starts.
    /// </summary>
    private void TextKeyField(FieldBinding field, StringKind kind, string? reason)
    {
        var (text, id) = ShownText(field, kind);
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var idWidth = id.Length == 0 ? 0 : ImGui.CalcTextSize(id).X + spacing;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - idWidth);
        var edited = text;
        if (reason != null)
        {
            Field(text.Replace('\n', ' '), width, field.Lines.Count == 0 ? "Not set" : "No text for this id");
        }
        else if (kind == StringKind.Infocard)
        {
            var height = MultilineRows * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y;
            ImGui.InputTextMultiline("##value", ref edited, Capacity(edited), new Vector2(width, height));
        }
        else
        {
            ImGui.SetNextItemWidth(width);
            ImGui.InputTextWithHint("##value", "Not set", ref edited, Capacity(edited));
        }

        if (reason == null)
        {
            ImGui.SetItemTooltip("The text of the active mod. A new text gets a key in the strings.json of the mod.");
            if (ImGui.IsItemDeactivatedAfterEdit() && edited != text)
            {
                window.SetItemText(field, kind == StringKind.Infocard ? ItemEdits.ToInfocard(edited) : edited);
            }
        }

        if (id.Length > 0)
        {
            ImGui.SameLine(0, spacing);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(id);
        }
    }

    /// <summary>The text that a text key shows, and its id or placeholder.</summary>
    private (string Text, string Id) ShownText(FieldBinding field, StringKind kind)
    {
        if (field.Lines is not [{ Values: [var value, ..] }, ..])
        {
            return ("", "");
        }

        var session = window.Session!;
        var id = FieldEdit.FormatValue(value);
        if (StringPlaceholder.TryParse(id, out var placeholder))
        {
            var text = session.Edits.Text(placeholder) ?? "";
            return (kind == StringKind.Infocard && text.Length > 0 ? EditorSession.PlainText(text) : text, id);
        }

        return value.TryToInt32(out var number) && number > 0
            ? (kind == StringKind.Infocard ? session.InfoText(number) : session.Name(number), id)
            : ("", id);
    }

    /// <summary>Gives the events of the widget that drew last to the field edits.</summary>
    /// <param name="text">Gives the text of the key after a change, from the key at the start of the edit.</param>
    private void Route(FieldBinding field, bool changed, Func<FieldBinding, string> text)
    {
        var deactivated = ImGui.IsItemDeactivated();
        var events = new FieldEvents(ImGui.IsItemActivated(), ImGui.IsItemActive(), changed, deactivated,
            deactivated && ImGui.IsKeyPressed(ImGuiKey.Escape, false));
        fieldEdits.Route(window.Session!.Edits, ImGui.GetItemID(), field, events, text, ImGui.GetFrameCount());
    }

    /// <summary>The error of the open edit of a field, below the field.</summary>
    private void FieldError(FieldBinding field)
    {
        if (fieldEdits.ErrorOf(field) is { } error)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    /// Draws the marks of a bound row in the label cell, after the label: a later-mod warning and a lock. The blue bar
    /// at the left edge shows that the active mod sets the value.
    /// </summary>
    private bool RowMarks(FieldBinding field, string? reason, Vector2 labelMin)
    {
        if (field.Origin == ValueOrigin.ActiveMod)
        {
            var height = ImGui.GetFrameHeight();
            var x = labelMin.X - MathF.Truncate(CellPadding.X * ImGuiHelper.Scale);
            ImGui.GetWindowDrawList().AddRectFilled(new Vector2(x, labelMin.Y),
                new Vector2(x + MathF.Truncate(OriginBarWidth * ImGuiHelper.Scale), labelMin.Y + height),
                ImGui.GetColorU32(EditorTheme.Accent));
        }

        var hovered = false;
        if (field.LaterMods.Count > 0)
        {
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextColored(EditorTheme.Warning, EditorIcons.ExclamationTriangle.ToString());
            hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        }

        if (reason != null)
        {
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextDisabled(EditorIcons.Lock.ToString());
            hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        }

        return hovered;
    }

    /// <summary>The tooltip lines of a bound row: the layer that sets the value, the later mods and the lock.</summary>
    private static void AppendMarks(StringBuilder tooltip, FieldBinding field, string? reason)
    {
        tooltip.Append("\n\n").Append(field.OriginText);
        if (field.LaterMods.Count > 0)
        {
            tooltip.Append("\n").Append(EditorIcons.ExclamationTriangle).Append(' ')
                .Append(field.LaterMods.Count == 1 ? $"Mod '{field.LaterMods[0]}' loads later and changes it."
                    : $"Mods {string.Join(", ", field.LaterMods.Select(m => $"'{m}'"))} load later and change it.")
                .Append(" The game uses the value of the last one.");
        }

        if (reason != null)
        {
            tooltip.Append("\n").Append(EditorIcons.Lock).Append(' ').Append(reason);
        }
    }

    /// <summary>The menu of the label of a bound row: Revert and Copy INI line.</summary>
    private void RowMenu(FieldBinding field)
    {
        if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup("##row-menu");
        }

        if (!ImGui.BeginPopup("##row-menu"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        var canEdit = window.Session?.NoEditReason == null;
        if (Theme.IconMenuItem(EditorIcons.Undo, "Revert to the lower layer", field.CanRevert && canEdit))
        {
            window.Edits.Execute(field.Revert());
        }

        ImGui.SetItemTooltip("Puts back the value of the base data and the mods that load before the active mod.");
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy INI line", field.Lines.Count > 0))
        {
            ImGui.SetClipboardText(FieldEdit.IniText(field.Name, field.Lines));
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The buffer size of an edit field: room for the text and for more text.</summary>
    private static uint Capacity(string text) => (uint)(Encoding.UTF8.GetByteCount(text) + 1024);

    /// <summary>One section of the edit view with the bindings of its keys, for one edit version.</summary>
    /// <param name="editing">The key of the open field edit of the section, or null. It stays bound when the edit
    /// removes its last line, so that its field stays until the edit ends.</param>
    internal sealed class BoundSection(EditSession session, SectionRef section, SectionView view, string? editing)
    {
        private readonly Dictionary<string, FieldBinding?> fields = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The binding of a key, or null when neither the schema nor the section has the key, and no field
        /// edit of the key is open.</summary>
        public FieldBinding? Field(string name)
        {
            if (!fields.TryGetValue(name, out var field))
            {
                field = session.Schema(section, name) != null || view.Lines(name).Count > 0 ||
                        name.Equals(editing, StringComparison.OrdinalIgnoreCase)
                    ? FieldBinding.Create(session, section, view, name)
                    : null;
                fields[name] = field;
            }

            return field;
        }
    }

    /// <summary>Formats a number for the text of a row that is not set.</summary>
    private static string Unset(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
