using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// Draws the fields of an Inspector page: sections that open and close, with one row for each field. A row has the
/// label at the left and the value at the right. Each value method is the only place that draws its kind of value, so
/// all pages look the same.
/// </summary>
/// <remarks>
/// A row shows the game data value as a read-only field, so the user can select and copy it. Inside a
/// <see cref="Bind"/> scope, a row with an INI key shows the lines of the edit view and can edit them (see the bound
/// rows in <c>PropertyGrid.Bound.cs</c>).
/// </remarks>
internal sealed partial class PropertyGrid(EditorWindow window)
{
    // Sizes in unscaled points.
    private const float BodyIndent = 8;
    private const float BodyTop = 4;
    private const float AxisPrefixWidth = 14;
    private const float ChipGap = 4;
    private const float ChipInset = 2;
    private const float ChipPaddingX = 6;
    private const float ChipRounding = 3;
    private static readonly Vector2 CellPadding = new(4, 1);

    // The Unity label width: 45% of the Inspector width less 40 points, and at least 140 points for the 15 point font.
    private const float LabelFraction = 0.45f;
    private const float LabelOffset = 40;
    private const float LabelMinimum = 140;

    // An infocard shows this number of lines until the user asks for all of them.
    private const int InfoLines = 4;

    private const ImGuiInputTextFlags FieldFlags = ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll;

    private const string EulerNote =
        "The INI rotate value goes through a quaternion when the game data loads. So these angles can be different " +
        "from the INI text, but they give the same rotation.";

    /// <summary>The text that a rotation row without a line shows: the engine uses no rotation.</summary>
    private const string NoRotation = "0, 0, 0";

    /// <summary>The labels of the three values of a vector, in the axis colors.</summary>
    private static readonly ComponentLabel[] AxisLabels =
        [new("X", EditorTheme.AxisX), new("Y", EditorTheme.AxisY), new("Z", EditorTheme.AxisZ)];

    // The infocards that show all their lines, by id.
    private readonly HashSet<int> expandedInfo = [];
    private float labelWidth;
    private int row;
    private string? nextNote;

    [LibraryImport("cimgui")]
    private static partial void igTableFullRowBegin();

    [LibraryImport("cimgui")]
    private static partial void igTableFullRowEnd();

    /// <summary>
    /// The width of the label column, in pixels, for an Inspector that is <paramref name="width"/> pixels wide.
    /// </summary>
    internal static float LabelWidth(float width, float scale) =>
        MathF.Max(MathF.Ceiling(width * LabelFraction) - LabelOffset * scale, LabelMinimum * scale);

    /// <summary>
    /// Draws a section header that spans the Inspector. Returns true when the section is open. Then draw its rows and
    /// call <see cref="EndSection"/>.
    /// </summary>
    /// <param name="key">The key of the open state in the settings, "Type.Section".</param>
    /// <param name="defaultOpen">The open state until the user opens or closes the section.</param>
    public bool BeginSection(string key, char icon, string title, bool defaultOpen = true)
    {
        var scale = ImGuiHelper.Scale;
        var width = ImGui.GetContentRegionAvail().X;
        labelWidth = LabelWidth(width, scale);

        // A dark line above the header separates it from the section above it.
        ImGui.PushStyleVarY(ImGuiStyleVar.ItemSpacing, 0);
        var min = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, 1),
            ImGui.GetColorU32(EditorTheme.SectionLine));
        ImGui.Dummy(new Vector2(width, 1));

        // ImGuiCol.Header also colors selected rows. Without this push, each header looks selected.
        ImGui.PushStyleColor(ImGuiCol.Header, EditorTheme.Section);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, EditorTheme.SectionHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, EditorTheme.SectionHover);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        var settings = window.Settings;
        ImGui.SetNextItemOpen(settings.InspectorFoldouts.GetValueOrDefault(key, defaultOpen), ImGuiCond.Once);
        var open = ImGui.CollapsingHeader($"{icon}  {title}###{key}",
            ImGuiTreeNodeFlags.AllowOverlap | (defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None));
        if (ImGui.IsItemToggledOpen())
        {
            settings.InspectorFoldouts[key] = open;
            settings.Save();
        }

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
        if (!open)
        {
            return false;
        }

        var indent = MathF.Truncate(BodyIndent * scale);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Truncate(BodyTop * scale));
        ImGui.Indent(indent);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, CellPadding * scale);
        var visible = ImGui.BeginTable($"##{key}", 2, ImGuiTableFlags.NoSavedSettings,
            new Vector2(Math.Max(1, width - 2 * indent), 0));
        ImGui.PopStyleVar();
        if (!visible)
        {
            ImGui.Unindent(indent);
            return false;
        }

        // A column that cannot be resized gets its fixed width each frame, so the labels follow the Inspector width.
        ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize,
            labelWidth);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
        row = 0;
        return true;
    }

    /// <summary>Ends a section that <see cref="BeginSection"/> opened.</summary>
    public void EndSection()
    {
        ImGui.EndTable();
        ImGui.Unindent(MathF.Truncate(BodyIndent * ImGuiHelper.Scale));
    }

    /// <summary>
    /// Gives the next row a note: a dim glyph after its label, and the note in the tooltip of the label. Use it for a
    /// field that needs an explanation, for example a field that the engine does not use.
    /// </summary>
    public void SetNextNote(string note) => nextNote = note;

    /// <summary>A heading that spans the two columns, with a line after it.</summary>
    public void SubHeading(string text)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        igTableFullRowBegin();
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.SeparatorText(text);
        ImGui.PopStyleColor();
        igTableFullRowEnd();
    }

    /// <summary>A text value. Null or empty text shows a dim "None".</summary>
    public void Text(string label, string? value, string? iniKey = null)
    {
        if (BoundRow(label, iniKey, string.IsNullOrEmpty(value) ? "no value" : value))
        {
            return;
        }

        BeginRow(label, iniKey);
        Field(value);
        EndRow();
    }

    /// <summary>A number, formatted with the invariant culture.</summary>
    public void Number(string label, double value, string format = "0.###", string? iniKey = null)
    {
        if (BoundRow(label, iniKey, Unset(value, format)))
        {
            return;
        }

        BeginRow(label, iniKey);
        Field(value.ToString(format, CultureInfo.InvariantCulture));
        EndRow();
    }

    /// <summary>Three fields with an X, Y and Z prefix in the axis colors.</summary>
    public void Vector(string label, Vector3 value, string? iniKey = null)
    {
        if (BoundRow(label, iniKey, $"{Unset(value.X, "0.###")}, {Unset(value.Y, "0.###")}, {Unset(value.Z, "0.###")}"))
        {
            return;
        }

        BeginRow(label, iniKey);
        VectorFields(value);
        EndRow();
    }

    /// <summary>
    /// A rotation as Euler angles in degrees: pitch (X), yaw (Y) and roll (Z). A bound row shows the angles of the INI
    /// line, so it converts nothing. Without a line, the engine uses no rotation. Only an unbound row converts the
    /// quaternion, and its note tells that the angles can differ from the INI text.
    /// </summary>
    public void Euler(string label, Quaternion value, string? iniKey = null)
    {
        if (BoundRow(label, iniKey, NoRotation))
        {
            return;
        }

        nextNote ??= EulerNote;
        BeginRow(label, iniKey);
        VectorFields(value.GetEulerDegrees());
        EndRow();
    }

    /// <summary>
    /// The size of a zone: one field for each value of the <c>size</c> line, with a label for each value in INI
    /// order. A sphere has a radius, a cylinder a radius and a height, a ring an outer radius, an inner radius and a
    /// height, and an ellipsoid and a box an X, Y and Z value. A line with another number of values, for example one
    /// value for a box, gets one text field.
    /// </summary>
    /// <param name="size">The size of the zone, as the game data holds it: a ring has (outer, height, inner).</param>
    public void Size(string label, ShapeKind shape, Vector3 size, string? iniKey = null)
    {
        if (BoundRow(label, iniKey, "1, 1, 1", count => SizeLabels(shape, count)))
        {
            return;
        }

        float[] values = shape switch
        {
            ShapeKind.Sphere => [size.X],
            ShapeKind.Cylinder => [size.X, size.Y],
            ShapeKind.Ring => [size.X, size.Z, size.Y],
            _ => [size.X, size.Y, size.Z],
        };
        BeginRow(label, iniKey);
        ComponentFields(values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)).ToArray(),
            SizeLabels(shape, values.Length) ?? AxisLabels, (_, text, width) => Field(text, width));
        EndRow();
    }

    /// <summary>
    /// The labels of the values of a <c>size</c> line of a zone with a shape, in INI order, or null when the shape does
    /// not read a line with that number of values one value at a time. The color of a label is the color of the gizmo
    /// axis that changes the value (see <see cref="Runtime.Scene.ZoneSizeAxes"/>).
    /// </summary>
    internal static IReadOnlyList<ComponentLabel>? SizeLabels(ShapeKind shape, int count) => (shape, count) switch
    {
        (ShapeKind.Sphere, 1) => [new("Radius", EditorTheme.AxisX)],
        (ShapeKind.Cylinder, 2) => [new("Radius", EditorTheme.AxisX), new("Height", EditorTheme.AxisY)],
        (ShapeKind.Ring, 3) =>
            [new("Outer", EditorTheme.AxisX), new("Inner", EditorTheme.AxisZ), new("Height", EditorTheme.AxisY)],
        (ShapeKind.Ellipsoid or ShapeKind.Box, 3) => AxisLabels,
        _ => null,
    };

    /// <summary>The text of a string resource, and its id dim at the right.</summary>
    public void Ids(string label, int ids, string? iniKey = null)
    {
        if (BoundRow(label, iniKey, "no text"))
        {
            return;
        }

        BeginRow(label, iniKey);
        IdsField(ids <= 0 ? null : window.Session?.Name(ids), ids);
        EndRow();
    }

    /// <summary>
    /// The plain text of an infocard. A long text shows its first lines and a link that shows all of it. The id is dim
    /// at the right of the last line.
    /// </summary>
    public void Info(string label, int ids, string? iniKey = null)
    {
        if (BoundRow(label, iniKey, "no text"))
        {
            return;
        }

        BeginRow(label, iniKey);
        var text = window.Session?.InfoText(ids) ?? "";
        if (text.Length == 0)
        {
            IdsField(null, ids);
            EndRow();
            return;
        }

        var width = ImGui.GetContentRegionAvail().X;
        var limit = InfoLines * ImGui.GetFontSize();
        var isLong = ImGui.CalcTextSize(text, null, false, width).Y > limit + 1;
        var expanded = expandedInfo.Contains(ids);
        // The text starts at the height of the text in the other fields.
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ImGui.GetStyle().FramePadding.Y);
        if (isLong && !expanded)
        {
            // A child window cuts the text after its first lines.
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.BeginChild("##info", new Vector2(width, limit), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav);
            ImGui.PopStyleVar();
            ImGui.TextWrapped(text);
            ImGui.EndChild();
        }
        else
        {
            ImGui.TextWrapped(text);
        }

        ImGui.PushFont(null, EditorTheme.CaptionSize);
        if (isLong)
        {
            if (ImGui.TextLink(expanded ? "Show less" : "Show more"))
            {
                if (!expandedInfo.Remove(ids))
                {
                    expandedInfo.Add(ids);
                }
            }

            ImGui.SameLine();
        }

        var number = ids.ToString(CultureInfo.InvariantCulture);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() +
                            Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(number).X));
        ImGui.TextDisabled(number);
        ImGui.PopFont();
        EndRow();
    }

    /// <summary>
    /// A link to another item: a field with the kind glyph and "nickname (Name)", and a button that selects the item. A
    /// null nickname shows a dim "None". A null target disables the button, for an item that the editor cannot select.
    /// A bound row shows the lines of the key in place of the field, with the button at the right.
    /// </summary>
    public void Reference(string label, ObjectKind kind, string? nickname, string? name, object? target,
        string? iniKey = null)
    {
        var text = nickname == null ? "None" : string.IsNullOrEmpty(name) ? nickname : $"{nickname} ({name})";
        var follow = new Follow(target, $"{kind.Label}: {text}");
        if (BoundRow(label, iniKey, "no value", follow: follow))
        {
            return;
        }

        BeginRow(label, iniKey);
        var style = ImGui.GetStyle();
        var height = ImGui.GetFrameHeight();
        var spacing = style.ItemInnerSpacing.X;
        var buttonWidth = ToolbarItems.IconWidth;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - buttonWidth - spacing);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(EditorTheme.Strip), style.FrameRounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border), style.FrameRounding);

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var position = min + style.FramePadding;
        var textEnd = max.X - style.FramePadding.X;
        var clip = new Vector4(min.X, min.Y, textEnd, max.Y);
        if (nickname != null)
        {
            font.RenderChar(drawList, fontSize, position, kind.Abgr, kind.Icon, ref clip);
            position.X += font.GetFontBaked(fontSize).FindGlyph(kind.Icon).AdvanceX + spacing;
        }

        // A long reference ends with "...". The tooltip shows all of it.
        TextFit.AddText(drawList, font, fontSize, position,
            ImGui.GetColorU32(nickname == null ? ImGuiCol.TextDisabled : ImGuiCol.Text), text, textEnd - position.X);
        ImGui.Dummy(new Vector2(width, height));
        if (nickname != null && ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(follow.Item);
        }

        ImGui.SameLine(0, spacing);
        FollowButton(follow);
        EndRow();
    }

    /// <summary>The button of a reference that selects the item. A null target disables it.</summary>
    private void FollowButton(Follow follow)
    {
        ImGui.BeginDisabled(follow.Target == null);
        if (ImGui.Button($"{EditorIcons.UpRightFromSquare}##follow",
                new Vector2(ToolbarItems.IconWidth, ImGui.GetFrameHeight())))
        {
            window.Selection = follow.Target;
        }

        ImGui.EndDisabled();
        ImGui.SetItemTooltip(follow.Target == null ? "The editor cannot select this item." : $"Select {follow.Item}");
    }

    /// <summary>The item of a reference that its button selects.</summary>
    /// <param name="Target">The item, or null when the editor cannot select it.</param>
    /// <param name="Item">The kind and the name of the item, for the tooltips.</param>
    private readonly record struct Follow(object? Target, string Item);

    /// <summary>A row with its own widgets. <paramref name="value"/> draws them in the value column.</summary>
    public void Row(string label, Action value)
    {
        BeginRow(label, null);
        value();
        EndRow();
    }

    /// <summary>A text link, for example a count that shows its items. Returns true when the user clicks it.</summary>
    public bool Link(string label, string text, string tooltip, string? iniKey = null)
    {
        BeginRow(label, iniKey);
        ImGui.AlignTextToFramePadding();
        var clicked = ImGui.TextLink(text);
        ImGui.SetItemTooltip(tooltip);
        EndRow();
        return clicked;
    }

    /// <summary>The name of an enum value.</summary>
    public void Enum<T>(string label, T value, string? iniKey = null) where T : struct, System.Enum =>
        Text(label, value.ToString(), iniKey);

    /// <summary>A chip for each flag that is set, or a dim "None". The tooltip lists all the flags. In a bound scope, the
    /// chips show the lines of the key.</summary>
    public void Flags<T>(string label, T value, string? iniKey = null) where T : struct, System.Enum
    {
        BeginRow(label, iniKey);
        var bits = FlagBits(iniKey, FlagTable<T>.Bits(value));
        if (bits == 0)
        {
            Field(null);
        }
        else
        {
            Chips(FlagTable<T>.Entries, bits);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(FlagTable<T>.Describe(bits));
        }

        EndRow();
    }

    /// <summary>
    /// A row with the INI value, then a read-only checkbox for each flag of <typeparamref name="T"/>, two in each row.
    /// In a bound scope, the value row edits the key, and the checkboxes show its lines.
    /// </summary>
    public void FlagChecks<T>(T value, string iniKey) where T : struct, System.Enum
    {
        var bits = FlagBits(iniKey, FlagTable<T>.Bits(value));
        var number = bits.ToString(CultureInfo.InvariantCulture);
        if (!BoundRow("Value", iniKey, number))
        {
            BeginRow("Value", iniKey);
            Field($"{iniKey} = {number}");
            EndRow();
        }

        var entries = FlagTable<T>.Entries;
        for (var i = 0; i < entries.Length; i++)
        {
            if (i % 2 == 0)
            {
                ImGui.TableNextRow();
            }

            ImGui.TableNextColumn();
            ImGui.PushID(row++);
            ReadOnlyCheckbox(entries[i].Name, (bits & entries[i].Value) != 0);
            ImGui.PopID();
        }
    }

    /// <summary>A color swatch and the color as "r, g, b" from 0 to 255, as in the INI files. A null color shows a dim
    /// "None".</summary>
    public void Color(string label, Color4? color, string? iniKey = null)
    {
        if (BoundRow(label, iniKey, color is { } c ? $"{Byte(c.R)}, {Byte(c.G)}, {Byte(c.B)}" : "no color"))
        {
            return;
        }

        BeginRow(label, iniKey);
        if (color is not { } value)
        {
            Field(null);
            EndRow();
            return;
        }

        var height = ImGui.GetFrameHeight();
        ImGui.ColorButton("##swatch", new Vector4(value.R, value.G, value.B, value.A),
            ImGuiColorEditFlags.NoPicker | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.AlphaOpaque,
            new Vector2(MathF.Round(height * 1.5f), height));
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        Field(value.A < 1
            ? $"{Byte(value.R)}, {Byte(value.G)}, {Byte(value.B)}, {Byte(value.A)}"
            : $"{Byte(value.R)}, {Byte(value.G)}, {Byte(value.B)}");
        EndRow();
    }

    /// <summary>A read-only checkbox.</summary>
    public void Bool(string label, bool value, string? iniKey = null)
    {
        if (BoundRow(label, iniKey, value ? "true" : "false"))
        {
            return;
        }

        BeginRow(label, iniKey);
        ReadOnlyCheckbox("##value", value);
        EndRow();
    }

    /// <summary>Starts a row: draws the label in the first column and moves to the value column.</summary>
    /// <param name="field">The key of a bound row, or null. A bound row gets its marks and its menu.</param>
    /// <param name="readOnly">Why a bound row cannot change its key, or null.</param>
    private void BeginRow(string label, string? iniKey, FieldBinding? field = null, string? readOnly = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        // A bound row has the id of its key, so that its field keeps its id when rows before it come or go. An active
        // field then stays with its key.
        if (field != null)
        {
            ImGui.PushID($"{field.Section}|{field.Name}".ToLowerInvariant());
        }
        else
        {
            ImGui.PushID(row);
        }

        row++;
        var note = nextNote;
        nextNote = null;

        // A long label is cut at the column edge. The tooltip shows all of it, the INI key and the note.
        ImGui.AlignTextToFramePadding();
        var labelMin = ImGui.GetCursorScreenPos();
        ImGui.PushStyleColor(ImGuiCol.Text,
            field?.Origin == ValueOrigin.ActiveMod ? EditorTheme.AccentText : EditorTheme.Label);
        ImGui.Text(label);
        ImGui.PopStyleColor();
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        if (field != null)
        {
            RowMenu(field);
            hovered |= RowMarks(field, readOnly, labelMin);
        }

        if (note != null)
        {
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextDisabled(EditorIcons.InfoCircle.ToString());
            hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        }

        if (hovered)
        {
            var tooltip = new StringBuilder(label);
            if (iniKey != null)
            {
                tooltip.Append("\nINI key: ").Append(iniKey);
            }

            if (field != null)
            {
                AppendMarks(tooltip, field, readOnly);
            }

            if (note != null)
            {
                tooltip.Append("\n\n").Append(note);
            }

            ImGui.SetTooltip(tooltip.ToString());
        }

        ImGui.TableNextColumn();
    }

    private static void EndRow() => ImGui.PopID();

    /// <summary>
    /// A read-only text field with the width of <paramref name="width"/> (-1 fills the column). Empty text shows the
    /// hint. When the text does not fit, the tooltip shows all of it.
    /// </summary>
    internal static void Field(string? value, float width = -1, string hint = "None")
    {
        var text = value ?? "";
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##value", hint, ref text, (uint)Encoding.UTF8.GetByteCount(text) + 1, FieldFlags);
        if (text.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip) &&
            ImGui.CalcTextSize(text).X > ImGui.GetItemRectSize().X - 2 * ImGui.GetStyle().FramePadding.X)
        {
            ImGui.SetTooltip(text);
        }
    }

    /// <summary>
    /// A field with the text of a string resource and its id dim at the right. An id that is not above 0 means no
    /// text, for example the -1 that the game data has for no name, and shows "None".
    /// </summary>
    private static void IdsField(string? text, int ids)
    {
        if (ids <= 0)
        {
            Field(null);
            return;
        }

        var number = ids.ToString(CultureInfo.InvariantCulture);
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var numberWidth = ImGui.CalcTextSize(number).X;
        Field(text, Math.Max(1, ImGui.GetContentRegionAvail().X - numberWidth - spacing), "No text for this id");
        ImGui.SameLine(0, spacing);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(number);
    }

    private static void VectorFields(Vector3 value) => ComponentFields(
    [
        value.X.ToString("0.###", CultureInfo.InvariantCulture),
        value.Y.ToString("0.###", CultureInfo.InvariantCulture),
        value.Z.ToString("0.###", CultureInfo.InvariantCulture),
    ], AxisLabels, (_, text, width) => Field(text, width));

    /// <summary>
    /// One field for each label, with the label in front of it in its color. A missing value gives an empty field. The
    /// fields share one row when every value fits. If not, each field gets its own row, so that no value is cut. For
    /// example, a position of 141000 does not fit in a third of a 360 pt Inspector.
    /// </summary>
    /// <param name="field">Draws field i with its text, in the width that it gets.</param>
    /// <param name="reserve">The width to leave free at the right of the fields.</param>
    private static void ComponentFields(IReadOnlyList<string> components, IReadOnlyList<ComponentLabel> labels,
        Action<int, string, float> field, float reserve = 0)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemInnerSpacing.X;
        var height = ImGui.GetFrameHeight();
        var drawList = ImGui.GetWindowDrawList();
        var texts = labels.Select((_, i) => i < components.Count ? components[i] : "").ToArray();
        var layout = ComponentLayout.Of(labels.Count,
            labels.Max(l => ImGui.CalcTextSize(l.Text).X),
            texts.Max(c => ImGui.CalcTextSize(c).X) + 2 * style.FramePadding.X,
            ImGui.GetContentRegionAvail().X - reserve, MathF.Truncate(AxisPrefixWidth * ImGuiHelper.Scale), spacing);
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0 && !layout.Stacked)
            {
                ImGui.SameLine(0, spacing);
            }

            // The label is centered in its prefix space, at the height of the field text.
            var min = ImGui.GetCursorScreenPos();
            var labelWidth = ImGui.CalcTextSize(labels[i].Text).X;
            var labelPosition = new Vector2(min.X + MathF.Truncate((layout.Prefix - labelWidth) / 2),
                min.Y + style.FramePadding.Y);
            drawList.AddText(labelPosition, ImGui.GetColorU32(labels[i].Color), labels[i].Text);
            ImGui.Dummy(new Vector2(layout.Prefix, height));
            ImGui.SameLine(0, 0);
            ImGui.PushID(i);
            field(i, texts[i], layout.Field);
            ImGui.PopID();
        }
    }

    /// <summary>
    /// Draws a rounded chip for each flag that is set. The chips flow from left to right and wrap at the column edge.
    /// </summary>
    private static void Chips((ulong Value, string Name)[] entries, ulong bits)
    {
        var scale = ImGuiHelper.Scale;
        var font = ImGui.GetFont();
        var fontSize = MathF.Round(EditorTheme.CaptionSize * scale);
        var avail = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight();
        var gap = MathF.Truncate(ChipGap * scale);
        var inset = MathF.Truncate(ChipInset * scale);
        var padding = MathF.Truncate(ChipPaddingX * scale);
        var background = ImGui.GetColorU32(EditorTheme.SelectionUnfocused);
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var x = 0f;
        var y = 0f;
        foreach (var (flag, name) in entries)
        {
            if ((bits & flag) == 0)
            {
                continue;
            }

            var textSize = font.CalcTextSizeA(fontSize, float.MaxValue, 0, name);
            var chipWidth = MathF.Ceiling(textSize.X) + 2 * padding;
            if (x > 0 && x + chipWidth > avail)
            {
                x = 0;
                y += height;
            }

            var min = start + new Vector2(x, y + inset);
            var max = min + new Vector2(chipWidth, height - 2 * inset);
            drawList.AddRectFilled(min, max, background, ChipRounding * scale);
            drawList.AddText(font, fontSize,
                new Vector2(min.X + padding, min.Y + MathF.Truncate((max.Y - min.Y - textSize.Y) / 2)), textColor,
                name);
            x += chipWidth + gap;
        }

        ImGui.Dummy(new Vector2(Math.Max(1, avail), y + height));
    }

    /// <summary>
    /// A checkbox that shows a value and ignores the mouse. It has the full alpha, so it is as easy to read as the
    /// other read-only fields.
    /// </summary>
    private static void ReadOnlyCheckbox(string label, bool value)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.DisabledAlpha, 1);
        ImGui.BeginDisabled();
        ImGui.Checkbox(label, ref value);
        ImGui.EndDisabled();
        ImGui.PopStyleVar();
    }

    /// <summary>A color channel from 0 to 1 as a number from 0 to 255.</summary>
    private static int Byte(float channel) => (int)MathF.Round(Math.Clamp(channel, 0, 1) * 255);

    /// <summary>The label in front of the field of one value of a line, for example X or Radius.</summary>
    internal readonly record struct ComponentLabel(string Text, Vector4 Color);

    /// <summary>How the labeled fields of the values of one line share the value cell.</summary>
    /// <param name="Prefix">The width of the label in front of each field.</param>
    /// <param name="Field">The width of each field.</param>
    /// <param name="Stacked">True when each field gets its own row, because a value does not fit next to the
    /// others.</param>
    internal readonly record struct ComponentLayout(float Prefix, float Field, bool Stacked)
    {
        /// <param name="count">The number of fields.</param>
        /// <param name="label">The width of the text of the widest label.</param>
        /// <param name="value">The width that the widest value needs in its field, with the padding of the field.</param>
        /// <param name="available">The width of the value cell for the fields.</param>
        /// <param name="minimumPrefix">The width of the label of an axis letter. A label that fits in it gets this
        /// width. A wider label gets <paramref name="spacing"/> at each side.</param>
        /// <param name="spacing">The space between two fields in one row.</param>
        public static ComponentLayout Of(int count, float label, float value, float available, float minimumPrefix,
            float spacing)
        {
            var prefix = label <= minimumPrefix ? minimumPrefix : MathF.Ceiling(label + 2 * spacing);
            var part = MathF.Floor((available - (count - 1) * spacing) / count);
            var stacked = count > 1 && part - prefix < value;
            return new ComponentLayout(prefix, MathF.Max(1, stacked ? available - prefix : part - prefix), stacked);
        }
    }

    /// <summary>The flags of an enum type: each value with one bit, and its names.</summary>
    private static class FlagTable<T> where T : struct, System.Enum
    {
        /// <summary>
        /// The values with one bit, from low to high. Names that have the same value share one entry, for example
        /// "Spacedust/Music".
        /// </summary>
        public static readonly (ulong Value, string Name)[] Entries = typeof(T)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (Value: Bits((T)f.GetValue(null)!), f.Name))
            .Where(f => BitOperations.PopCount(f.Value) == 1)
            .GroupBy(f => f.Value)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, string.Join("/", g.Select(f => f.Name))))
            .ToArray();

        public static ulong Bits(T value) => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));

        /// <summary>The flags that are set and the flags that are not set, as two lines of text.</summary>
        public static string Describe(ulong bits)
        {
            var set = Entries.Where(e => (bits & e.Value) != 0).Select(e => e.Name).ToArray();
            var clear = Entries.Where(e => (bits & e.Value) == 0).Select(e => e.Name).ToArray();
            return $"Set: {(set.Length == 0 ? "none" : string.Join(", ", set))}\n" +
                   $"Not set: {(clear.Length == 0 ? "none" : string.Join(", ", clear))}";
        }
    }
}
