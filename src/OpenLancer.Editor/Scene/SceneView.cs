using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Graphics;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.Render.Cameras;
using LibreLancer.Resources;
using LibreLancer.World;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// A 3D view of one star system. It shows a copy of the system, so that later edits do not change the loaded data.
/// </summary>
internal sealed class SceneView : IDisposable
{
    private static readonly Color4 GridColor = new(EditorTheme.GridLine);
    private static readonly Color4 AxisXColor = new(EditorTheme.AxisX);
    private static readonly Color4 AxisZColor = new(EditorTheme.AxisZ);
    private static readonly Color4 SelectionColor = new(EditorTheme.Accent);

    // The opacity of the zone volumes: the fields and nebulae, and the selected or hovered zone.
    private const float VolumeAlpha = 0.15f;
    private const float HighlightAlpha = 0.35f;

    // The patrol paths are faint, because one system can have hundreds of them (Li01 has 267). The selected path is
    // clear.
    private static readonly Color4 PatrolColor = new(ObjectKinds.Of(ZoneRole.PatrolPath).Color with { W = 0.2f });
    private static readonly Color4 PatrolHighlightColor =
        new(ObjectKinds.Of(ZoneRole.PatrolPath).Color with { W = 0.9f });
    private static readonly Color4 LaneColor = new(ObjectKinds.TradeLane.Color with { W = 0.6f });
    private const int RangeSegments = 64;

    private readonly EditorWindow window;
    private readonly LookAtCamera camera = new() { GameFOV = true };
    private readonly SceneCamera controller = new();
    private readonly Dictionary<SystemObject, GameObject> objects = [];
    private readonly SceneOverlay overlay;
    private readonly SystemPicker picker;
    private readonly GridRenderer grid;
    private readonly AxisGizmo gizmo = new();
    private SystemRenderer? renderer;
    private GameWorld? world;
    private GameResourceManager? resources;
    private ZoneRenderer? zoneRenderer;

    // The item under the mouse in this frame. Its zone volume shows also when the Zones toggle is off.
    private object? hovered;
    private Vector2 dragStart;
    private Vector3 systemCenter;
    private float systemRadius;

    public SceneView(EditorWindow window)
    {
        this.window = window;
        overlay = new SceneOverlay(window, objects);
        picker = new SystemPicker(window);
        grid = new GridRenderer(window.RenderContext);
    }

    /// <summary>The mouse and keyboard controls of the view, for the help popover and the status bar.</summary>
    public static readonly (string Input, string Action)[] Controls =
    [
        ("Right mouse drag", "Look around"),
        ("W A S D, Q and E", "Fly, while the right mouse button is down"),
        ("Wheel, while flying", "Set the fly speed"),
        ("Shift, while flying", "Fly four times faster"),
        ("Alt + left mouse drag", "Orbit"),
        ("Middle mouse drag", "Pan"),
        ("Wheel", "Zoom"),
        ("Click", "Select"),
        ("Click an axis end", "Look along the axis"),
        ("F", "Focus on the selection"),
    ];

    public StarSystem? System { get; private set; }

    /// <summary>The shared data of the open system, or null when no system is open.</summary>
    public SystemIndex? Index { get; private set; }

    public Vector3 CameraPosition => controller.Position;

    /// <summary>The fly speed of the camera, in world units per second.</summary>
    public float CameraSpeed => controller.Speed;

    /// <summary>The kind of an item. A zone of the open system gets the kind of its role.</summary>
    public ObjectKind? KindOf(object? item) => Index?.KindOf(item) ?? ObjectKinds.Of(item);

    /// <summary>True when the item is part of the open system, so that the camera can focus on it.</summary>
    public bool Contains(object item) => item switch
    {
        SystemObject obj => objects.ContainsKey(obj),
        Zone zone => System?.Zones.Contains(zone) == true,
        LightSource light => System?.LightSources.Contains(light) == true,
        _ => false,
    };

    public void Open(StarSystem source, GameDataManager data, GameResourceManager resources)
    {
        Close();
        var system = source.Clone();
        this.resources = resources;
        renderer = new SystemRenderer(camera, resources, window);
        world = new GameWorld(renderer, null, resources, null, true);
        data.LoadAllSystem(system);
        world.LoadSystem(system, resources, null, false, false);
        renderer.LoadLights(system);
        renderer.LoadStarspheres(system);
        renderer.LoadZones(system.AsteroidFields, system.Nebulae);
        renderer.OpaqueHook = DrawGrid;
        renderer.PhysicsHook = DrawHelpers;
        zoneRenderer = new ZoneRenderer(window.RenderContext, resources);
        foreach (var obj in world.Objects)
        {
            if (obj.SystemObject != null)
            {
                objects[obj.SystemObject] = obj;
            }
        }

        System = system;
        Index = new SystemIndex(system, source);
        var center = system.Objects.Count == 0
            ? Vector3.Zero
            : system.Objects.Aggregate(Vector3.Zero, (sum, o) => sum + o.Position) / system.Objects.Count;
        var radius = system.Objects.Count == 0 ? 10_000 : system.Objects.Max(o => Vector3.Distance(o.Position, center));
        systemCenter = center;
        systemRadius = radius;
        controller.Frame(center, radius);
    }

    /// <summary>
    /// Closes the open system. It also forgets the system list of the game data, because a closed profile closes its
    /// system.
    /// </summary>
    public void Close()
    {
        objects.Clear();
        overlay.Reset();
        picker.Reset();
        world?.Dispose();
        // SystemRenderer.Dispose does not dispose the asteroid field meshes. Only LoadZones disposes them, so an empty
        // load frees their vertex buffers before the renderer goes.
        renderer?.LoadZones(null, null);
        renderer?.Dispose();
        zoneRenderer?.Dispose();
        world = null;
        renderer = null;
        zoneRenderer = null;
        resources = null;
        hovered = null;
        System = null;
        Index = null;
    }

    public void Update(double elapsed) => world?.Update(elapsed);

    /// <summary>Moves the camera so that the whole system is in view.</summary>
    public void FrameSystem()
    {
        if (System != null)
        {
            controller.Frame(systemCenter, systemRadius);
        }
    }

    /// <summary>Moves the camera to the selected object.</summary>
    public void Focus(object? item)
    {
        switch (item)
        {
            case SystemObject obj:
                controller.Focus(obj.Position, 3 * Radius(obj));
                break;
            case Zone zone:
                controller.Focus(zone.Position, 2 * zone.Size.Length());
                break;
            case LightSource light:
                controller.Focus(light.Light.Position, 500);
                break;
        }
    }

    /// <summary>Draws the view into the current ImGui window, and reads the mouse and keyboard over it.</summary>
    public unsafe void Draw(double elapsed)
    {
        if (window.Session?.GameData == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (world == null || renderer == null || System == null || Index == null)
        {
            PanelStates.Message("No system is open.", "Select a system in the list of the toolbar.");
            return;
        }

        ImGuiHelper.AnimatingElement();
        var avail = ImGui.GetContentRegionAvail();
        var width = Math.Max(64, (int)avail.X);
        var height = Math.Max(64, (int)avail.Y);
        var origin = ImGui.GetCursorScreenPos();

        // The 3D scene draws inside the ImGui draw list, at the position of this window.
        ImGui.GetWindowDrawList().AddCallback((_, _) => DrawWorld((int)origin.X, (int)origin.Y, width, height),
            IntPtr.Zero);
        ImGui.InvisibleButton("##scene", new Vector2(width, height),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var sceneHovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        controller.HandleInput(window.Keyboard, sceneHovered, active, elapsed);
        controller.Apply(camera, width, height);
        var size = new Vector2(width, height);
        overlay.Update(camera, origin, size, Index);
        gizmo.Update(camera.View, origin, size);
        Pick(origin, width, height);
        if (sceneHovered && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F))
        {
            Focus(window.Selection);
        }

        // No hover while the camera moves, so that the markers that pass under the mouse do not flash. The axis
        // widget is on top, so a marker below it gets no hover.
        var mouse = ImGui.GetMousePos();
        var hoverMouse = sceneHovered && !active ? mouse : (Vector2?)null;
        hovered = hoverMouse is { } point && gizmo.HitTest(point) == null ? overlay.HitTest(point) : null;
        overlay.Draw(hovered, gizmo.Bounds);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(origin, origin + size, true);
        gizmo.Draw(drawList, hoverMouse);
        drawList.PopClipRect();

        renderer.DrawNebulae = window.Settings.ShowNebulae;
        renderer.DrawStarsphere = window.Settings.ShowStarsphere;
        renderer.BackgroundOverride = System.BackgroundColor;
        renderer.SystemLighting.Ambient = new Color4(System.AmbientColor, 1);
        world.RenderUpdate(elapsed);
    }

    /// <summary>A view toggle of the toolbar strip, for one setting.</summary>
    private sealed record ViewOption(string Id, char Icon, string Label, Func<EditorSettings, bool> Get,
        Action<EditorSettings, bool> Set);

    /// <summary>The view toggles, in the order of the toolbar strip.</summary>
    private static readonly ViewOption[] ViewOptions =
    [
        new("markers", EditorIcons.MapMarkerAlt, "Markers", s => s.ShowMarkers, (s, v) => s.ShowMarkers = v),
        new("labels", EditorIcons.Tag, "Labels", s => s.ShowLabels, (s, v) => s.ShowLabels = v),
        new("grid", EditorIcons.BorderAll, "Grid", s => s.ShowGrid, (s, v) => s.ShowGrid = v),
        new("zones", EditorIcons.VectorSquare, "Zones: field and nebula volumes, patrol paths and zone markers",
            s => s.ShowZones, (s, v) => s.ShowZones = v),
        new("lights", EditorIcons.Lightbulb, "Lights", s => s.ShowLights, (s, v) => s.ShowLights = v),
        new("nebulae", EditorIcons.Cloud, "Nebulae", s => s.ShowNebulae, (s, v) => s.ShowNebulae = v),
        new("starsphere", EditorIcons.Star, "Starsphere", s => s.ShowStarsphere, (s, v) => s.ShowStarsphere = v),
        new("wireframe", EditorIcons.Th, "Wireframe", s => s.Wireframe, (s, v) => s.Wireframe = v),
    ];

    /// <summary>
    /// Draws the items of the toolbar strip: the system list, Frame system, the view toggles and the help button. It
    /// draws also when no system is open, so that the user can open one.
    /// </summary>
    public void Toolbar()
    {
        var settings = window.Settings;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var item = ToolbarItems.IconWidth + spacing;
        // The system list gets the space that the other items leave, between its minimum and maximum width. The other
        // items are Frame system, the view toggles, the help button and a separator.
        var others = (ViewOptions.Length + 2) * item + 1 + 2 * spacing;
        var scale = ImGuiHelper.Scale;
        picker.Draw(Math.Clamp(ImGui.GetContentRegionAvail().X - others, SystemPicker.MinWidth * scale,
            SystemPicker.MaxWidth * scale));
        ImGui.SameLine();
        if (ToolbarItems.Button("frame", EditorIcons.CompressArrowsAlt, "Frame system", System != null))
        {
            FrameSystem();
        }

        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();

        // The toggles that do not fit go into a menu behind a More button, so that the help button stays in view.
        // Each toggle takes one item width, and the help button takes the last one.
        var room = ImGui.GetContentRegionAvail().X - ToolbarItems.IconWidth;
        var shown = ViewOptions.Length * item <= room ? ViewOptions.Length : Math.Max(0, (int)(room / item) - 1);
        for (var i = 0; i < shown; i++)
        {
            var option = ViewOptions[i];
            if (i > 0)
            {
                ImGui.SameLine();
            }

            option.Set(settings, ToolbarItems.Toggle(option.Id, option.Icon, option.Label, option.Get(settings)));
        }

        if (shown < ViewOptions.Length)
        {
            if (shown > 0)
            {
                ImGui.SameLine();
            }

            if (ToolbarItems.Button("more", EditorIcons.EllipsisV, "More view options"))
            {
                ImGui.OpenPopup("##view-options");
            }

            // The menu opens below the button.
            ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + 2 * scale),
                ImGuiCond.Appearing);
            if (ImGui.BeginPopup("##view-options"))
            {
                EditorTheme.PushMenuColors();
                for (var i = shown; i < ViewOptions.Length; i++)
                {
                    var option = ViewOptions[i];
                    var value = option.Get(settings);
                    if (ImGui.MenuItem($"{option.Icon}  {option.Label}", "", value))
                    {
                        option.Set(settings, !value);
                    }
                }

                EditorTheme.PopMenuColors();
                ImGui.EndPopup();
            }
        }

        // The help button is at the right end of the strip.
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() +
                            Math.Max(0, ImGui.GetContentRegionAvail().X - ToolbarItems.IconWidth));
        if (ToolbarItems.Button("help", EditorIcons.QuestionCircle, "Camera controls"))
        {
            ImGui.OpenPopup("##controls");
        }

        // The popover opens below the button, with its right edge at the right edge of the button.
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMax().X, ImGui.GetItemRectMax().Y + 2 * scale),
            ImGuiCond.Appearing, new Vector2(1, 0));
        if (ImGui.BeginPopup("##controls"))
        {
            ControlsTable();
            ImGui.EndPopup();
        }
    }

    /// <summary>Draws the camera controls as a table of inputs and actions.</summary>
    private static void ControlsTable()
    {
        ImGui.Text("Camera controls");
        ImGui.Separator();
        if (!ImGui.BeginTable("##controls", 2, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        foreach (var (input, action) in Controls)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(input);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(action);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// A click without a drag selects the object under the mouse. A marker wins over a model. A click on empty space
    /// clears the selection.
    /// </summary>
    private void Pick(Vector2 origin, int width, int height)
    {
        if (ImGui.IsItemActivated() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            dragStart = ImGui.GetMousePos();
        }

        if (!ImGui.IsItemDeactivated() || !ImGui.IsMouseReleased(ImGuiMouseButton.Left) || ImGui.GetIO().KeyAlt ||
            Vector2.Distance(dragStart, ImGui.GetMousePos()) > 4)
        {
            return;
        }

        // The axis widget is on top of the markers. A click on an axis end looks at the scene from that side.
        if (gizmo.HitTest(ImGui.GetMousePos()) is { } axis)
        {
            controller.LookAlong(-axis);
            return;
        }

        if (overlay.HitTest(ImGui.GetMousePos()) is { } marker)
        {
            window.Selection = marker;
            return;
        }

        var mouse = ImGui.GetMousePos() - origin;
        var hit = world!.GetSelection(camera, null!, mouse.X, mouse.Y, width, height);
        window.Selection = hit?.SystemObject;
    }

    private void DrawWorld(int x, int y, int width, int height)
    {
        if (renderer == null)
        {
            return;
        }

        var context = window.RenderContext;
        var clearColor = context.ClearColor;
        context.PushViewport(x, y, width, height);
        context.PushScissor(new Rectangle(x, y, width, height), false);
        context.Cull = true;
        context.DepthEnabled = true;
        context.Wireframe = window.Settings.Wireframe;
        renderer.Draw(width, height);
        context.Wireframe = false;
        context.PopViewport();
        context.PopScissor();
        context.ClearColor = clearColor;
        context.DepthEnabled = false;
        context.BlendMode = BlendMode.Normal;
        context.Cull = false;
    }

    /// <summary>
    /// Draws the grid. The renderer calls this after the opaque objects and before the transparent objects, so planets
    /// hide the grid and nebulae tint it.
    /// </summary>
    private void DrawGrid()
    {
        if (window.Settings.ShowGrid)
        {
            grid.Draw(window.RenderContext, camera, GridColor, AxisXColor, AxisZColor);
        }
    }

    /// <summary>
    /// Draws the editor helpers in the 3D scene: the zone volumes, the patrol paths, the trade lanes, the range of the
    /// selected light and the selection wireframe. The renderer calls this after the transparent objects. It clears
    /// its line list at the start of each frame, so the helper lines must go into the list here, in the hook.
    /// </summary>
    private void DrawHelpers()
    {
        DrawZones();
        DrawLines();
        DrawSelection();
    }

    /// <summary>
    /// Draws the volumes of the fields and nebulae when the Zones toggle is on, and the volume of the selected and the
    /// hovered zone always. A volume that holds the camera shows its inside faces.
    /// </summary>
    private void DrawZones()
    {
        if (zoneRenderer == null || Index == null)
        {
            return;
        }

        zoneRenderer.Begin(camera.Position);
        if (window.Settings.ShowZones)
        {
            foreach (var zone in Index.VolumeZones)
            {
                if (!IsHighlighted(zone))
                {
                    AddZone(zone, VolumeAlpha);
                }
            }
        }

        if (window.Selection is Zone selected && Contains(selected))
        {
            AddZone(selected, HighlightAlpha);
        }

        if (hovered is Zone hover && !ReferenceEquals(hover, window.Selection))
        {
            AddZone(hover, HighlightAlpha);
        }

        zoneRenderer.Finish();
    }

    private bool IsHighlighted(Zone zone) => ReferenceEquals(zone, window.Selection) || ReferenceEquals(zone, hovered);

    /// <summary>Adds the volume of a zone in the color of its role.</summary>
    private void AddZone(Zone zone, float alpha)
    {
        var color = new Color4(Index!.KindOf(zone)!.Color with { W = alpha });
        var inside = zone.ContainsPoint(camera.Position);
        var size = zone.Size;
        switch (zone.Shape)
        {
            case ShapeKind.Sphere:
                zoneRenderer!.DrawSphere(zone.Position, size.X, zone.RotationMatrix, color, inside);
                break;
            case ShapeKind.Ellipsoid:
                zoneRenderer!.DrawEllipsoid(zone.Position, size, zone.RotationMatrix, color, inside);
                break;
            case ShapeKind.Box:
                zoneRenderer!.DrawCube(zone.Position, size, zone.RotationMatrix, color, inside);
                break;
            case ShapeKind.Cylinder:
                zoneRenderer!.DrawCylinder(zone.Position, size.X, size.Y, zone.RotationMatrix, color, inside);
                break;
            case ShapeKind.Ring:
                // A ring zone stores its size as (outer radius, height, inner radius).
                zoneRenderer!.DrawRing(zone.Position, size.Z, size.X, size.Y, zone.RotationMatrix, color, inside);
                break;
        }
    }

    /// <summary>
    /// Adds the lines of the frame: the patrol paths when the Zones toggle is on, the selected or hovered patrol path
    /// always, the trade lanes, and the range circles of the selected light.
    /// </summary>
    private void DrawLines()
    {
        if (renderer == null || Index == null)
        {
            return;
        }

        var lines = renderer.DebugRenderer;
        var showZones = window.Settings.ShowZones;
        foreach (var zone in Index.PatrolPaths)
        {
            var highlighted = IsHighlighted(zone);
            if (showZones || highlighted)
            {
                // The path runs along the axis of the cylinder, through its center.
                var half = Vector3.TransformNormal(new Vector3(0, zone.Size.Y / 2, 0), zone.RotationMatrix);
                lines.DrawLine(zone.Position - half, zone.Position + half,
                    highlighted ? PatrolHighlightColor : PatrolColor);
            }
        }

        foreach (var lane in Index.Lanes)
        {
            for (var i = 1; i < lane.Rings.Count; i++)
            {
                lines.DrawLine(lane.Rings[i - 1].Position, lane.Rings[i].Position, LaneColor);
            }
        }

        if (window.Selection is LightSource light && Contains(light))
        {
            var color = new Color4(ObjectKinds.LightColor(light.Light.Color));
            var center = light.Light.Position;
            var range = light.Light.Range;
            Circle(lines, center, Vector3.UnitX * range, Vector3.UnitY * range, color);
            Circle(lines, center, Vector3.UnitX * range, Vector3.UnitZ * range, color);
            Circle(lines, center, Vector3.UnitY * range, Vector3.UnitZ * range, color);
        }
    }

    /// <summary>A circle through center + u and center + v, as line segments.</summary>
    private static void Circle(LineRenderer lines, Vector3 center, Vector3 u, Vector3 v, Color4 color)
    {
        var previous = center + u;
        for (var i = 1; i <= RangeSegments; i++)
        {
            var angle = 2 * MathF.PI * i / RangeSegments;
            var point = center + MathF.Cos(angle) * u + MathF.Sin(angle) * v;
            lines.DrawLine(previous, point, color);
            previous = point;
        }
    }

    /// <summary>
    /// Draws the wireframe of the selected object in the accent color. A model without wireframe data, for example a
    /// planet sphere, gets its oriented bounding box.
    /// </summary>
    private void DrawSelection()
    {
        if (window.Selection is not SystemObject selected || !objects.TryGetValue(selected, out var obj) ||
            obj.RenderComponent is not ModelRenderer { Model: { } model } || renderer == null)
        {
            return;
        }

        var transform = obj.WorldTransform.Matrix();
        var drawn = false;
        // DrawVWire keeps the blend mode of the transparent pass, which can be additive. The wireframe must have the
        // accent color, so it draws with normal blending.
        var context = window.RenderContext;
        var blendMode = context.BlendMode;
        context.BlendMode = BlendMode.Normal;
        foreach (var part in model.AllParts)
        {
            if (part.Wireframe is { } wire && resources?.FindMesh(wire.MeshCRC)?.VertexResource is { } vertices)
            {
                renderer.DebugRenderer.DrawVWire(wire, vertices, part.LocalTransform.Matrix() * transform,
                    SelectionColor);
                drawn = true;
            }
        }

        context.BlendMode = blendMode;

        if (drawn)
        {
            return;
        }

        var corners = model.GetBoundingBox().GetCorners().Select(c => Vector3.Transform(c, transform)).ToArray();
        // BoundingBox.GetCorners gives the near face (0-3) and then the far face (4-7).
        for (int i = 0; i < 4; i++)
        {
            renderer.DebugRenderer.DrawLine(corners[i], corners[(i + 1) % 4], SelectionColor);
            renderer.DebugRenderer.DrawLine(corners[i + 4], corners[(i + 1) % 4 + 4], SelectionColor);
            renderer.DebugRenderer.DrawLine(corners[i], corners[i + 4], SelectionColor);
        }
    }

    private float Radius(SystemObject obj) =>
        objects.TryGetValue(obj, out var go) && go.RenderComponent is ModelRenderer { Model: { } model }
            ? Math.Max(model.GetRadius(), 50)
            : 500;

    public void Dispose()
    {
        Close();
        grid.Dispose();
    }
}
