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
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;

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

    // The camera of the starsphere pass: the view of the scene camera with a near plane of its own.
    private readonly LookAtCamera sky = new();
    private readonly SceneCamera controller = new();
    private readonly Dictionary<SystemObject, GameObject> objects = [];
    private readonly SceneOverlay overlay;
    private readonly SystemPicker picker;
    private readonly GridRenderer grid;
    private readonly AxisGizmo gizmo = new();
    private readonly ScenePicker scenePicker;
    private readonly TransformGizmo transform;
    private SystemRenderer? renderer;
    private GameWorld? world;
    private GameResourceManager? resources;
    private ZoneRenderer? zoneRenderer;
    private SceneSync? sync;

    // The item under the mouse in this frame. Its zone volume shows also when the Zones toggle is off.
    private object? hovered;
    private Vector2 dragStart;

    // The scene button was active in the last frame: an orbit, a pan, a fly or a click is in progress.
    private bool buttonActive;
    private Vector3 systemCenter;
    private float systemRadius;

    public SceneView(EditorWindow window)
    {
        this.window = window;
        overlay = new SceneOverlay(window, objects);
        picker = new SystemPicker(window);
        scenePicker = new ScenePicker(gizmo, overlay.Markers, objects);
        transform = new TransformGizmo(window, (session, item) => item is SectionRef section
            ? (GizmoTarget.Of(session.Edits, section, session.NoEditReason, out var reason), reason)
            : (null, null), GizmoSteps.System);
        grid = new GridRenderer(window.RenderContext);
    }

    /// <summary>The mouse and keyboard controls of the view and the gizmo, for the help popover and the status
    /// bar.</summary>
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
        ("W, E and R", "Move, rotate or scale tool of the gizmo, when the selected item takes it"),
        ("X", "Local or World axes of a move or a turn. A scale always uses Local"),
        ("Ctrl, while dragging a handle", "Snap when the snap is off, or do not snap when it is on"),
        ("Esc or right click, while dragging a handle", "Cancel the drag"),
    ];

    public StarSystem? System { get; private set; }

    /// <summary>The shared data of the open system, or null when no system is open.</summary>
    public SystemIndex? Index { get; private set; }

    /// <summary>A number that changes when a name of an item of the open system changes, so that the Hierarchy reads
    /// the names again.</summary>
    public int NamesVersion { get; private set; }

    public Vector3 CameraPosition => controller.Position;

    /// <summary>The turn of the camera (see <see cref="SceneCamera.Orientation"/>).</summary>
    public Quaternion CameraOrientation => controller.Orientation;

    /// <summary>True while a drag of the gizmo is open (see <see cref="TransformGizmo.IsDragging"/>).</summary>
    public bool IsDragging => transform.IsDragging;

    /// <summary>The fly speed of the camera, in world units per second.</summary>
    public float CameraSpeed => controller.Speed;

    /// <summary>The kind of an item. A zone of the open system gets the kind of its role.</summary>
    public ObjectKind? KindOf(object? item) => Index?.KindOf(item) ?? ObjectKinds.Of(item);

    /// <summary>The section of W that an object, a zone or a light of the open system shows, or null.</summary>
    public SectionRef? SectionOf(object item) => sync?.SectionOf(item);

    /// <summary>Why the scene does not show the W state of a section of the open system, for example a missing archetype
    /// or an unknown attenuation curve. Empty when it shows it.</summary>
    public IReadOnlyList<Diagnostic> Problems(SectionRef section) => sync?.Problems(section) ?? [];

    /// <summary>Ends an open drag of the gizmo with the line of its last frame (a forced end, see
    /// <see cref="TransformGizmo.End"/>).</summary>
    public void EndDrag() => transform.End();

    /// <summary>True while W has changes that the scene did not apply yet. The scene applies them at its next flush (see
    /// <see cref="SceneSync.Flush"/>).</summary>
    public bool IsBehind => sync?.IsBehind == true;

    /// <summary>
    /// The shapes of the open system for the start of Play from camera (see <see cref="SpawnPoint.Shapes"/>). They have
    /// the unsaved edits that the scene shows. Null when the open system is not the system with that nickname.
    /// </summary>
    public IReadOnlyList<SpawnShape>? SpawnShapes(string system) =>
        System?.Nickname.Equals(system, StringComparison.OrdinalIgnoreCase) == true
            ? SpawnPoint.Shapes(objects, System.Zones,
                item => window.Session?.ItemName(item) is { Length: > 0 } name ? name : item.Nickname)
            : null;

    /// <summary>True when the item is part of the open system, so that the camera can focus on it.</summary>
    public bool Contains(object item) => item switch
    {
        SystemObject obj => objects.ContainsKey(obj),
        Zone zone => System?.Zones.Contains(zone) == true,
        LightSource light => System?.LightSources.Contains(light) == true,
        _ => false,
    };

    /// <summary>
    /// Opens a system of the game data of a session. The scene shows a clone of the system, with the unsaved edits of
    /// W, and it follows each later change of W.
    /// </summary>
    public void Open(StarSystem source, EditorSession session)
    {
        Close();
        var data = session.GameData!;
        var resources = session.Resources;
        var system = source.Clone();
        this.resources = resources;
        // The engine starsphere pass uses the near plane of the scene camera, which is too far at the whole-system view.
        // So the view draws the starsphere in its own pass (DrawBackground).
        renderer = new SystemRenderer(camera, resources, window) { DrawStarsphere = false };
        world = new GameWorld(renderer, null, resources, null, true);
        data.LoadAllSystem(system);
        world.LoadSystem(system, resources, null, false, false);
        renderer.LoadLights(system);
        renderer.LoadStarspheres(system);
        renderer.LoadZones(system.AsteroidFields, system.Nebulae);
        renderer.OpaqueHook = DrawBackground;
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
        sync = new SceneSync(window, session, data.Items, resources, system, world, renderer, objects, IndexChanged);
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
        // A drag ends before the scene stops following W, so that W keeps the line of its last frame.
        transform.End();
        sync?.Dispose();
        sync = null;
        objects.Clear();
        overlay.Reset();
        picker.Reset();
        world?.Dispose();
        // With fork commit OL-0022, the renderer also frees the meshes of the asteroid fields.
        renderer?.Dispose();
        zoneRenderer?.Dispose();
        world = null;
        renderer = null;
        zoneRenderer = null;
        resources = null;
        hovered = null;
        buttonActive = false;
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

        // Flush A: the changes of W since the last frame, before this frame reads the scene.
        sync!.Flush();
        ImGuiHelper.AnimatingElement();
        var avail = ImGui.GetContentRegionAvail();
        var width = Math.Max(64, (int)avail.X);
        var height = Math.Max(64, (int)avail.Y);
        var origin = ImGui.GetCursorScreenPos();

        // The 3D scene draws inside the ImGui draw list, at the position of this window.
        ImGui.GetWindowDrawList().AddCallback((_, _) => DrawWorld((int)origin.X, (int)origin.Y, width, height),
            IntPtr.Zero);

        // The frame order of section 10.2 of the design. First the drag of the gizmo ends, cancels or clears. Then the
        // gizmo or the scene button gets the mouse.
        var size = new Vector2(width, height);
        transform.BeginFrame(SelectedSection());
        var owner = transform.Owner(buttonActive);
        bool sceneHovered = false, active = false, activated = false, deactivated = false;
        if (owner == SceneMouse.Button)
        {
            ImGui.InvisibleButton("##scene", size,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight |
                ImGuiButtonFlags.MouseButtonMiddle);
            sceneHovered = ImGui.IsItemHovered();
            active = ImGui.IsItemActive();
            activated = ImGui.IsItemActivated();
            deactivated = ImGui.IsItemDeactivated();
        }
        else
        {
            // A Dummy takes no click, so ImGuizmo can start its drag (ImGuizmo.cpp:1615-1621). The scene window does
            // not move under the drag, because its root window has NoMove.
            ImGui.Dummy(size);
        }

        buttonActive = active;
        var viewKeys = GizmoInput.ReadsViewKeys(owner, sceneHovered, ImGui.IsWindowHovered(),
            ImGui.IsMouseHoveringRect(origin, origin + size));
        controller.HandleInput(window.Keyboard, viewKeys, active, elapsed);
        controller.Apply(camera, sky, width, height);
        var projection = new SceneProjection(camera, origin, size, ImGuiHelper.Scale);
        overlay.Update(projection, Index);
        gizmo.Update(projection);
        Pick(projection, Index, activated, deactivated);
        if (viewKeys && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F))
        {
            Focus(window.Selection);
        }

        transform.Keys(ImGui.IsWindowHovered() || ImGui.IsWindowFocused());

        // No hover while the camera moves or the gizmo has the mouse, so that the markers that pass under the mouse do
        // not flash. The axis widget is on top, so a marker below it gets no hover.
        var mouse = ImGui.GetMousePos();
        var hoverMouse = sceneHovered && !active ? mouse : (Vector2?)null;
        hovered = hoverMouse is { } point && gizmo.HitTest(point) == null ? overlay.Markers.HitTest(point) : null;
        overlay.Draw(hovered, gizmo.Bounds);
        // The gizmo draws over the markers and the labels. A handle over the axis widget wins over the widget.
        transform.Draw(camera, projection);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(origin, origin + size, true);
        gizmo.Draw(drawList, hoverMouse);
        drawList.PopClipRect();

        // Flush B: the changes of this frame so far, before the renderer starts its worker tasks. So a step of a drag
        // moves the item in the same frame.
        sync.Flush();
        renderer.DrawNebulae = window.Settings.ShowNebulae;
        renderer.BackgroundOverride = System.BackgroundColor;
        renderer.SystemLighting.Ambient = new Color4(System.AmbientColor, 1);
        world.RenderUpdate(elapsed);
    }

    /// <summary>
    /// A change of W changed what the index or the names of the open system read (see
    /// <see cref="ISceneHost.IndexChanged"/>).
    /// </summary>
    private void IndexChanged(SceneEffect effects)
    {
        if ((effects & (SceneEffect.Roles | SceneEffect.Lanes)) != 0)
        {
            Index = new SystemIndex(System!, Index!.Source);
        }

        if ((effects & SceneEffect.Label) != 0)
        {
            overlay.ResetLabels();
            NamesVersion++;
        }
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
        // items are Frame system, the gizmo items, the view toggles, the help button and two separators.
        var others = (ViewOptions.Length + TransformGizmo.ToolbarItemCount + 2) * item + 2 * (1 + spacing) + spacing;
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
        transform.Toolbar(SelectedSection());
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
        if (ToolbarItems.Button("help", EditorIcons.QuestionCircle, "Scene controls"))
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

    /// <summary>Draws the controls of the view and the gizmo as a table of inputs and actions.</summary>
    private static void ControlsTable()
    {
        ImGui.Text("Scene controls");
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
    /// A click without a drag selects what <see cref="ScenePicker"/> finds under the mouse, and a click on empty space
    /// clears the selection. A click on an axis end of the axis widget looks at the scene from that side.
    /// </summary>
    /// <param name="activated">The scene button became active in this frame.</param>
    /// <param name="deactivated">The scene button stopped being active in this frame.</param>
    private void Pick(SceneProjection projection, SystemIndex index, bool activated, bool deactivated)
    {
        if (activated && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            dragStart = ImGui.GetMousePos();
        }

        if (!deactivated || !ImGui.IsMouseReleased(ImGuiMouseButton.Left) || ImGui.GetIO().KeyAlt ||
            Vector2.Distance(dragStart, ImGui.GetMousePos()) > 4)
        {
            return;
        }

        var pick = scenePicker.Pick(projection, ImGui.GetMousePos(), index, window.Settings.ShowZones);
        if (pick.Axis is { } axis)
        {
            controller.LookAlong(-axis);
            return;
        }

        window.Selection = pick.Item;
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
    /// Draws the starsphere, then the grid. The renderer calls this after the opaque objects and before the transparent
    /// objects, at the place of its own starsphere pass. So planets hide the grid and the starsphere, and nebulae tint
    /// them.
    /// </summary>
    private void DrawBackground()
    {
        StarspherePass.Draw(renderer!, sky, window.Settings.ShowStarsphere);
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
                var (start, end) = SystemIndex.PathLine(zone);
                lines.DrawLine(start, end, highlighted ? PatrolHighlightColor : PatrolColor);
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

    /// <summary>The section of the selected item when it is an object, a zone or a light of the open system, or
    /// null.</summary>
    private SectionRef? SelectedSection() =>
        window.Selection is (SystemObject or Zone or LightSource) and var item && Contains(item)
            ? SectionOf(item)
            : null;

    private float Radius(SystemObject obj) =>
        ScenePicker.Radius(objects.TryGetValue(obj, out var go) &&
                           go.RenderComponent is ModelRenderer { Model: { } model } ? model : null);

    public void Dispose()
    {
        Close();
        grid.Dispose();
    }
}
