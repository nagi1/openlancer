using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Assets;

/// <summary>
/// The drag and drop of the asset browser (section 8.1 of the M23 design): a tile of the Assets panel onto the Scene, a
/// Hierarchy row or an Inspector list field, and a Hierarchy row onto the Prop groups category. The payload is an index
/// into a table of this class that holds the dragged row. A drag never changes the selection (decision D269).
/// </summary>
/// <remarks>
/// A target that cannot take the dragged asset shows the reason under the cursor in the error color, and the drop does
/// nothing. In cutscene mode the Hierarchy takes no drop (review finding R20).
/// </remarks>
internal sealed class AssetDrag(EditorWindow window)
{
    /// <summary>The payload type of a tile of the Assets panel.</summary>
    public const string AssetType = "OL_ASSET";

    /// <summary>The payload type of a row of the Hierarchy.</summary>
    public const string RowType = "OL_ROW";

    /// <summary>The radius of the ghost circle of a model that the thumbnail renderer does not know, in meters.</summary>
    private const float DefaultRadius = 100;

    private const int CircleSegments = 48;

    private readonly Dictionary<int, object> table = [];
    private int next;

    /// <summary>The ghost of the drag over the Scene in this frame: the point, the reason of a refusal, the kind of the
    /// asset and the radius of its model.</summary>
    private (Vector3 Point, string? Refusal, ObjectKind Kind, float Radius)? ghost;

    /// <summary>True while a tile or a Hierarchy row is dragged. The thumbnails do not render meanwhile.</summary>
    public bool IsDragging => ImGui.GetDragDropPayload() is { } payload &&
                              (payload.IsDataType(AssetType) || payload.IsDataType(RowType));

    /// <summary>Sets the payload of a tile. Call it between <c>BeginDragDropSource</c> and
    /// <c>EndDragDropSource</c>. The text is the preview under the cursor.</summary>
    public unsafe void SourceOf(AssetRow row, string name, ObjectKind kind)
    {
        var index = Register(row);
        ImGui.SetDragDropPayload(AssetType, (IntPtr)(&index), sizeof(int));
        ImGui.Text($"{kind.Icon}  {name}");
    }

    /// <summary>Sets the payload of a Hierarchy row, for "Save as prop group" (Unity: drag a GameObject into the Project
    /// window).</summary>
    public unsafe void SourceOf(SectionRef section, string name, ObjectKind kind)
    {
        var index = Register(section);
        ImGui.SetDragDropPayload(RowType, (IntPtr)(&index), sizeof(int));
        ImGui.Text($"{kind.Icon}  {name}");
    }

    /// <summary>
    /// The Scene as a drop target, after the item that covers the view: a placeable asset spawns at the point of
    /// decision D275, and a loadout or a faction goes on the object under the cursor (decision D281).
    /// </summary>
    /// <param name="projection">The camera of the frame.</param>
    /// <param name="objectAt">The first object of the pick stack under a screen point, or null.</param>
    public void Scene(SceneProjection projection, Func<Vector2, SystemObject?> objectAt)
    {
        ghost = null;
        if (!ImGui.BeginDragDropTarget())
            return;
        if (Payload(AssetType) is AssetRow row)
        {
            var mouse = ImGui.GetMousePos();
            var point = ScenePlacement.Point(projection.Eye, projection.Ray(mouse).Direction, window.Scene.Pivot);
            var kind = ObjectKinds.Assets.Of(row);
            if (row.Asset.Kind is AssetKind.Loadout or AssetKind.Faction)
            {
                if (objectAt(mouse) is { } obj && window.Scene.SectionOf(obj) is { } section)
                {
                    Hint($"{kind.Icon}  {ComponentText(row)} of {section.Id.Key}");
                    if (Accept(AssetType))
                        window.SceneEdits.SetComponent(section, row);
                }
                else
                    Refuse(row.NotPlaceable ?? "Drop it on an object.");
            }
            else if (SpawnRefusal(row) is { } refusal)
            {
                ghost = (point, refusal, kind, DefaultRadius);
                Refuse(refusal);
            }
            else
            {
                ghost = (point, null, kind, window.Assets.RadiusOf(row.Asset) ?? DefaultRadius);
                if (Accept(AssetType))
                    window.SceneEdits.Spawn(row, point, null);
            }
        }
        ImGui.EndDragDropTarget();
    }

    /// <summary>
    /// A Hierarchy row or the empty space below the rows as a drop target (section 8.1): a placeable asset spawns at the
    /// position of the object of the row with <c>parent</c>, or at the pivot for another row and for the empty space. A
    /// loadout or a faction goes on the object of the row.
    /// </summary>
    /// <param name="item">The item of the row, or null for a group row and for the empty space.</param>
    public void Hierarchy(object? item)
    {
        if (window.CutsceneMode || !ImGui.BeginDragDropTarget())
            return;
        if (Payload(AssetType) is AssetRow row)
        {
            var section = item is SystemObject ? window.Scene.SectionOf(item) : null;
            if (row.Asset.Kind is AssetKind.Loadout or AssetKind.Faction)
            {
                if (section != null)
                {
                    if (Accept(AssetType))
                        window.SceneEdits.SetComponent(section, row);
                }
                else
                    Refuse(row.NotPlaceable ?? "Drop it on an object.");
            }
            else if (SpawnRefusal(row) is { } refusal)
                Refuse(refusal);
            else if (Accept(AssetType))
            {
                if (item is SystemObject obj && section != null)
                    window.SceneEdits.Spawn(row, obj.Position, section);
                else
                    window.SceneEdits.Spawn(row, window.Scene.Pivot, null);
            }
        }
        ImGui.EndDragDropTarget();
    }

    /// <summary>
    /// An Inspector list field as a drop target: it takes a tile of an entry of a list of its rule, as Unity drops an
    /// asset on an object field (decision D281). The drop sets the value as a pick of the picker does.
    /// </summary>
    /// <param name="lists">The lists of the rule of the field.</param>
    /// <param name="label">The label of the list, for the reason of a refusal.</param>
    /// <param name="set">Sets the value of the field.</param>
    public void Field(IReadOnlyList<string> lists, string label, Action<string> set)
    {
        if (!ImGui.BeginDragDropTarget())
            return;
        if (Payload(AssetType) is AssetRow row)
        {
            if (row.List != null && lists.Contains(row.List, StringComparer.OrdinalIgnoreCase))
            {
                if (Accept(AssetType))
                    set(row.Asset.Id);
            }
            else
                Refuse($"This field takes {Article(label)} {label.ToLowerInvariant()}.");
        }
        ImGui.EndDragDropTarget();
    }

    /// <summary>The Prop groups category of the Assets panel as a drop target: a Hierarchy row opens "Save as prop
    /// group" for its item.</summary>
    public void Groups()
    {
        if (!ImGui.BeginDragDropTarget())
            return;
        if (Payload(RowType) is SectionRef section && Accept(RowType))
            window.SceneEdits.SaveGroup(section);
        ImGui.EndDragDropTarget();
    }

    /// <summary>
    /// Draws the ghost of a drag over the Scene (section 8.3): a circle on the grid plane below the point, a line from the
    /// point to the plane when the point is not on it, the kind glyph, and the three values of the point or the reason
    /// of a refusal.
    /// </summary>
    public void DrawGhost(ImDrawListPtr drawList, SceneProjection projection)
    {
        if (ghost is not var (point, refusal, kind, radius))
            return;
        var color = ImGui.GetColorU32(refusal == null ? EditorTheme.Accent : EditorTheme.Error);
        var ground = point with { Y = 0 };
        var previous = (Vector2?)null;
        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = 2 * MathF.PI * i / CircleSegments;
            var world = ground + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * radius;
            Vector2? screen = projection.Project(world, out var at, out _) ? at : null;
            if (previous is { } a && screen is { } b)
                drawList.AddLine(a, b, color, MathF.Max(1, ImGuiHelper.Scale));
            previous = screen;
        }
        if (!projection.Project(point, out var center, out _))
            return;
        if (!ScenePlacement.OnPlane(point) && projection.Project(ground, out var foot, out _))
            drawList.AddLine(center, foot, color, MathF.Max(1, ImGuiHelper.Scale));
        var size = MathF.Round(18 * ImGuiHelper.Scale);
        Glyphs.Draw(drawList, kind.Icon, size, center, refusal == null ? kind.Abgr : color);
        var caption = refusal ?? string.Create(CultureInfo.InvariantCulture, $"{point.X:0}, {point.Y:0}, {point.Z:0}");
        drawList.AddText(center + new Vector2(size, -ImGui.GetFontSize() / 2), color, caption);
    }

    /// <summary>Why a tile cannot spawn now, or null: the reason of its row, or why the Scene takes no Create.</summary>
    private string? SpawnRefusal(AssetRow row) =>
        row.Asset.Kind is AssetKind.SolarArchetype or AssetKind.Ship or AssetKind.Asteroid or AssetKind.Model or
            AssetKind.PropGroup
            ? row.NotPlaceable ?? window.SceneEdits.Unavailable(Data.Editing.Scene.SceneAction.Create, null)
            : row.NotPlaceable ?? "The Scene does not place this asset.";

    private static string ComponentText(AssetRow row) =>
        row.Asset.Kind == AssetKind.Loadout ? $"Set the loadout {row.Asset.Id}" : $"Set the faction {row.Asset.Id}";

    /// <summary>The row or the section of the payload of a type, or null when the drag has another payload.</summary>
    private unsafe object? Payload(string type)
    {
        var payload = ImGui.GetDragDropPayload();
        if (payload == null || !payload.IsDataType(type) || payload.DataSize != sizeof(int))
            return null;
        return table.GetValueOrDefault(*(int*)payload.Data);
    }

    /// <summary>True when the user drops the payload of the type on the target in this frame.</summary>
    private static bool Accept(string type) => ImGui.AcceptDragDropPayload(type) is { Delivery: true };

    private int Register(object item)
    {
        // One drag runs at a time, and its source sets the payload again in each frame.
        table.Clear();
        table[++next] = item;
        return next;
    }

    /// <summary>The reason of a refusal under the cursor, in the error color.</summary>
    private static void Refuse(string reason)
    {
        ImGui.BeginTooltip();
        ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.Ban}  {reason}");
        ImGui.EndTooltip();
    }

    /// <summary>A note under the cursor about what the drop does.</summary>
    private static void Hint(string text)
    {
        ImGui.BeginTooltip();
        ImGui.Text(text);
        ImGui.EndTooltip();
    }

    private static string Article(string label) =>
        label.Length > 0 && "aeiouAEIOU".Contains(label[0]) ? "an" : "a";
}
