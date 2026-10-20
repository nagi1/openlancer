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
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// A 3D view of one star system. It shows a copy of the system, so that later edits do not change the loaded data.
/// </summary>
internal sealed class SceneView : IDisposable
{
    /// <summary>The id of the panel. The layout reports its focus by this id.</summary>
    public const string PanelId = "scene";

    private static readonly Color4 GridColor = new(EditorTheme.GridLine);
    private static readonly Color4 AxisXColor = new(EditorTheme.AxisX);
    private static readonly Color4 AxisZColor = new(EditorTheme.AxisZ);
    private static readonly Color4 SelectionColor = new(EditorTheme.Accent);

    // The opacity of the zone volumes: the fields and nebulae, and the selected or hovered item. A selected volume that
    // holds the camera keeps the normal opacity, so that it does not hide the view. The accent outline shows the
    // selection (decision D161 of the M22 design).
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
    private readonly PickCycle cycle = new();
    private readonly TransformGizmo transform;
    private readonly MissionLayer missionLayer;
    private SystemRenderer? renderer;
    private GameWorld? world;
    private GameResourceManager? resources;
    private ZoneRenderer? zoneRenderer;
    private SceneSync? sync;

    // The cameras and the highlights of the draw that runs now: the draw of the panel or an image of the agent. The
    // hooks of the renderer read them.
    private DrawPass pass;

    // The item under the mouse in this frame. Its zone volume shows also when the Zones toggle is off.
    private object? hovered;

    // The section to select when the scene shows its item (see SelectWhenShown), or null.
    private SectionRef? pendingSelection;
    private Vector2 dragStart;

    // The scene button was active in the last frame: an orbit, a pan, a fly or a click is in progress.
    private bool buttonActive;
    private Vector3 systemCenter;
    private float systemRadius;

    public SceneView(EditorWindow window)
    {
        this.window = window;
        overlay = new SceneOverlay(window, objects);
        missionLayer = new MissionLayer(window);
        overlay.Labels = missionLayer.Label;
        picker = new SystemPicker(window);
        scenePicker = new ScenePicker(gizmo, overlay.Markers, objects);
        transform = new TransformGizmo(window, (session, item) => item is SectionRef section
            ? (GizmoTarget.Of(session.Edits, section, session.NoEditReason, out var reason), reason)
            : (null, null), GizmoSteps.System);
        grid = new GridRenderer(window.RenderContext);
        pass = new DrawPass(camera, sky, false, null, null);
    }

    /// <summary>What one draw of the world shows: the camera, the sky camera of the starsphere, the zone volumes and
    /// the highlighted items.</summary>
    /// <param name="Zones">The volumes of the fields, nebulae and zones and the patrol paths show (the Zones
    /// toggle).</param>
    /// <param name="Selected">The selected item, which shows in the accent color, or null.</param>
    /// <param name="Hovered">The item under the mouse, whose zone volume shows, or null.</param>
    private readonly record struct DrawPass(ICamera Camera, ICamera Sky, bool Zones, object? Selected, object? Hovered);

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
        ("Click", "Select. Objects first, then nebulae and asteroid fields, then zones"),
        ("Click again at the same place", "Select the next item under the mouse"),
        ("Alt + click", "Select the next item under the mouse"),
        ("Click an axis end", "Look along the axis"),
        ("F", "Focus on the selection"),
        ("W, E and R", "Move, rotate or scale tool of the gizmo, when the selected item takes it"),
        ("X", "Local or World axes of a move or a turn. A scale always uses Local"),
        ("Ctrl, while dragging a handle", "Snap when the snap is off, or do not snap when it is on"),
        ("Esc or right click, while dragging a handle", "Cancel the drag"),
    ];

    public StarSystem? System { get; private set; }

    /// <summary>The mission layer: the items of the open mission in the open system (section 7.2 of the M6a
    /// design).</summary>
    public MissionLayer MissionItems => missionLayer;

    /// <summary>The overlay of the markers and labels. The cross-links of the Mission tab set its highlighted
    /// items.</summary>
    public SceneOverlay Overlay => overlay;

    /// <summary>The screen rectangle of the scene in the last frame that drew it, or null.</summary>
    public (Vector2 Min, Vector2 Max)? LastRect { get; private set; }

    /// <summary>The frame number of the last frame that drew the scene.</summary>
    public int LastFrame { get; private set; } = -1;

    /// <summary>A double-click on an item of the scene, for example a mission marker or an object. The Mission tab
    /// frames the first trigger that uses it (section 7.3 of the M6a design).</summary>
    public event Action<object>? DoubleClicked;

    private SceneProjection? lastProjection;

    /// <summary>The screen point of a world position with the camera of the last frame, also behind the camera (see
    /// <see cref="SceneProjection.Toward"/>). Null when the scene did not draw.</summary>
    public Vector2? Toward(Vector3 position) => lastProjection?.Toward(position);

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

    /// <summary>True while the camera moves or a drag of the gizmo runs: the scene button is active for an orbit, a pan,
    /// a fly or a click. The thumbnails of the Assets panel wait meanwhile (section 7.2 of the M23 design).</summary>
    public bool IsMoving => buttonActive || transform.IsDragging;

    /// <summary>True when a section of the open system waits for the reload of the game data, for example an object of
    /// a new static prop archetype (decisions D248 and D282 of the M23 design).</summary>
    public bool WaitsForReload => sync?.WaitsForReload == true;

    /// <summary>The fly speed of the camera, in world units per second.</summary>
    public float CameraSpeed => controller.Speed;

    /// <summary>The kind of an item. A zone of the open system gets the kind of its role.</summary>
    public ObjectKind? KindOf(object? item) => Index?.KindOf(item) ?? ObjectKinds.Of(item);

    /// <summary>The section of W that an object, a zone, a light, a nebula or a field of the open system shows, or
    /// null.</summary>
    public SectionRef? SectionOf(object item) => sync?.SectionOf(item);

    /// <summary>The object, zone, light, nebula or field of the open system that a section of W sets, or null. A section
    /// of the fields of the system gives the system.</summary>
    public object? ItemOf(SectionRef section) => sync?.ItemOf(section);

    /// <summary>The orbit point of the camera, where Create puts a new item (decision D234 of the M3 design).</summary>
    public Vector3 Pivot => controller.Pivot;

    /// <summary>
    /// Selects the item of a section as soon as the scene shows it (decision D249 of the M3 design): Create, Duplicate and
    /// the undo of a delete select the new item. Until then the selection is the section. The first flush that maps the
    /// section to an item selects that item.
    /// </summary>
    public void SelectWhenShown(SectionRef section)
    {
        pendingSelection = section;
        window.Selection = ItemOf(section) is { } item and not StarSystem ? item : section;
        if (window.Selection is not SectionRef)
        {
            pendingSelection = null;
        }
    }

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
        Nebula or AsteroidField => Index?.VolumeOf(item) != null,
        _ => false,
    };

    /// <summary>
    /// Opens a system of the game data of a session. The scene shows a clone of the system, with the unsaved edits of
    /// W, and it follows each later change of W.
    /// </summary>
    /// <param name="keepCamera">True to keep the camera pose, for an open of the same system after a reload of the game
    /// data (decision D282 of the M23 design). Else the camera frames the whole system.</param>
    /// <remarks>The engine lines of the system load and of the catch-up of W form a batch of the Console, which compares
    /// them with the base run (section 3.4 of the console filter design).</remarks>
    public void Open(StarSystem source, EditorSession session, bool keepCamera = false)
    {
        var batch = window.BeginConsoleBatch();
        try
        {
            Load(source, session, keepCamera);
        }
        finally
        {
            ConsoleLog.EndBatch(batch);
        }
    }

    private void Load(StarSystem source, EditorSession session, bool keepCamera)
    {
        Close();
        var data = session.GameData!;
        var resources = session.Resources;
        var system = source.Clone();
        this.resources = resources;
        // The engine starsphere pass uses the near plane of the scene camera, which is too far at the whole-system view.
        // So the view draws the starsphere in its own pass (DrawBackground).
        renderer = new SystemRenderer(camera, resources, window) { DrawStarsphere = false };
        world = new GameWorld(renderer, null, resources, null, true) { DefaultObjectType = EquipmentType.Editor };
        data.LoadAllSystem(system);
        world.LoadSystem(system, resources, null, false);
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
        pendingSelection = null;
        // The text scan of the story mentions builds in a worker task, so a delete or a rename does not wait for it
        // (decision D261 of the M3 design).
        session.Edits.StartStoryMentions();
        var center = system.Objects.Count == 0
            ? Vector3.Zero
            : system.Objects.Aggregate(Vector3.Zero, (sum, o) => sum + o.Position) / system.Objects.Count;
        var radius = system.Objects.Count == 0 ? 10_000 : system.Objects.Max(o => Vector3.Distance(o.Position, center));
        systemCenter = center;
        systemRadius = radius;
        if (!keepCamera)
        {
            controller.Frame(center, radius);
        }
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
        missionLayer.Reset();
        lastProjection = null;
        LastRect = null;
        picker.Reset();
        cycle.Reset();
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

    /// <summary>Moves the camera to the selected item. A nebula or a field frames its zone.</summary>
    public void Focus(object? item)
    {
        switch (item)
        {
            case Nebula or AsteroidField when SceneItems.ZoneOf(item) is { } area:
                Focus(area);
                break;
            case SystemObject obj:
                controller.Focus(obj.Position, 3 * Radius(obj));
                break;
            case Zone zone:
                controller.Focus(zone.Position, 2 * zone.Size.Length());
                break;
            case LightSource light:
                controller.Focus(light.Light.Position, 500);
                break;
            case Data.Missions.MissionRef mission when missionLayer.PlaceOf(mission) is var (position, radius):
                controller.Focus(position, radius);
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

        // The cross-links of the Mission tab highlight items only in the Scene part of that tab (section 7.3 of the M6a
        // design). The Mission tab sets them again in each of its frames.
        if (window.CenterMode != CenterMode.Mission)
        {
            overlay.Highlighted = [];
            missionLayer.Highlight = new HashSet<Data.Missions.MissionRef>();
        }

        // Flush A: the changes of W since the last frame, before this frame reads the scene.
        sync!.Flush();
        FollowSelection();
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

        // The button or the Dummy takes a tile of the Assets panel (section 8.1 of the M23 design). The camera has the
        // pose of the last frame, which is the pose of this frame during a drag.
        var dropProjection = new SceneProjection(camera, origin, size, ImGuiHelper.Scale);
        window.AssetDrag.Scene(dropProjection, mouse => ObjectAt(dropProjection, mouse));

        buttonActive = active;
        var viewKeys = GizmoInput.ReadsViewKeys(owner, sceneHovered, ImGui.IsWindowHovered(),
            ImGui.IsMouseHoveringRect(origin, origin + size));
        controller.HandleInput(window.Keyboard, viewKeys, active, elapsed);
        controller.Apply(camera, sky, width, height);
        var projection = new SceneProjection(camera, origin, size, ImGuiHelper.Scale);
        lastProjection = projection;
        LastRect = (origin, origin + size);
        LastFrame = ImGui.GetFrameCount();
        overlay.Update(projection, Index);
        // The items of the open mission are markers too, so the picker and the pick cycle find them (section 7.2 of the
        // M6a design). They have their own toggle, "Mission items".
        if (missionLayer.Active)
        {
            missionLayer.Update(Index, NamesVersion);
            overlay.Markers.Append(projection, window.Selection, missionLayer.Markers(window.Settings.ShowMissionItems));
        }
        gizmo.Update(projection);
        Pick(projection, Index, activated, deactivated);
        if (activated && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            // The release of the second press keeps the item of the first click, which the double-click acts on.
            cycle.DoublePress();
            var stack = scenePicker.Pick(projection, ImGui.GetMousePos(), Index,
                new PickView(controller.Position, window.Settings.ShowZones, window.Settings.ShowNebulae,
                    window.Settings.ShowFog)).Items;
            var doubleClicked = stack.FirstOrDefault(i => Equals(i, window.Selection)) ?? stack.FirstOrDefault();
            if (doubleClicked != null)
            {
                DoubleClicked?.Invoke(doubleClicked);
            }
        }
        if (viewKeys && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F))
        {
            Focus(window.Selection);
        }

        transform.Keys(ImGui.IsWindowHovered() || ImGui.IsWindowFocused());
        // F2, Ctrl+D and Delete act on the selection, as in the Unity Scene view (decision D255 of the M3 design).
        if (ImGui.IsWindowFocused() && !transform.IsDragging)
        {
            window.SceneEdits.Keys(window.Selection);
        }

        // No hover while the camera moves or the gizmo has the mouse, so that the markers that pass under the mouse do
        // not flash. The axis widget is on top, so a marker below it gets no hover.
        var mouse = ImGui.GetMousePos();
        var hoverMouse = sceneHovered && !active ? mouse : (Vector2?)null;
        hovered = hoverMouse is { } point && gizmo.HitTest(point) == null ? overlay.Markers.HitTest(point) : null;
        overlay.Draw(hovered, gizmo.Bounds, hoverMouse is { } at ? cycle.Hint(at, window.Selection) : null);
        // The gizmo draws over the markers and the labels. A handle over the axis widget wins over the widget.
        transform.Draw(camera, projection);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(origin, origin + size, true);
        gizmo.Draw(drawList, hoverMouse);
        window.AssetDrag.DrawGhost(drawList, projection);
        drawList.PopClipRect();

        // Flush B: the changes of this frame so far, before the renderer starts its worker tasks. So a step of a drag
        // moves the item in the same frame.
        sync.Flush();
        ApplyViewOptions();
        world.RenderUpdate(elapsed);
    }

    /// <summary>Gives the renderer the view toggles of the user and the background and the ambient light of the
    /// system.</summary>
    private void ApplyViewOptions()
    {
        renderer!.DrawNebulae = window.Settings.ShowNebulae;
        renderer.DrawFog = window.Settings.ShowFog;
        renderer.BackgroundOverride = System!.BackgroundColor;
        renderer.SystemLighting.Ambient = new Color4(System.AmbientColor, 1);
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

        FollowSelection();
    }

    /// <summary>
    /// Follows the selection after a change of the items (decision D249 of the M3 design): the item of a section that
    /// waits for the scene becomes the selection, and a selected item that the scene does not have now leaves the
    /// selection empty, as a delete in Unity does.
    /// </summary>
    private void FollowSelection()
    {
        if (pendingSelection != null && ItemOf(pendingSelection) is { } item and not StarSystem)
        {
            if (window.Selection is SectionRef waiting && waiting.Equals(pendingSelection))
            {
                window.Selection = item;
            }

            pendingSelection = null;
        }

        if (window.Selection is SystemObject or Zone or LightSource or Nebula or AsteroidField && !Contains(window.Selection))
        {
            window.Selection = null;
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
        new("fog", EditorIcons.Smog, "Fog: the fog of the nebula that holds the camera", s => s.ShowFog,
            (s, v) => s.ShowFog = v),
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
        // The toggle "Mission items", its info icon and the menu "Mission systems" show only for an open mission.
        var missionToggle = !missionLayer.Active ? 0
            : window.Missions.Open is { } open && Data.Missions.MissionSummary.TriggerSystems(open.Index).Count > 1 ? 3
            : 2;
        var others = (ViewOptions.Length + missionToggle + TransformGizmo.ToolbarItemCount + 2) * item +
                     2 * (1 + spacing) + spacing;
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

        // The toggle "Mission items" has an effect only while a mission is open, so it shows only then (UI rule 4).
        if (missionLayer.Active)
        {
            settings.ShowMissionItems = ToolbarItems.Toggle("mission-items", EditorIcons.Flag,
                "Mission items: show the ships, points, volumes and paths of the open mission in this system.",
                settings.ShowMissionItems);
            ImGui.SameLine(0, 0);
            Ui.Fields.InfoIcon.Draw("mission-items-help", Data.Missions.MissionSchema.Retail.Help["missionItems"]);
            ImGui.SameLine();
            MissionSystems();
        }

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

    /// <summary>
    /// The menu "Mission systems" (section 7.4 of the M6a design): each system of the open mission with its trigger
    /// count. It shows only when the mission names more than one system. A click opens the system.
    /// </summary>
    private void MissionSystems()
    {
        if (window.Missions.Open is not { } analysis ||
            Data.Missions.MissionSummary.TriggerSystems(analysis.Index) is not { Count: > 1 } systems)
        {
            return;
        }

        if (ToolbarItems.Button("mission-systems", EditorIcons.Map, "Mission systems: open a system of the mission."))
        {
            ImGui.OpenPopup("##mission-systems");
        }

        ImGui.SameLine();
        if (!ImGui.BeginPopup("##mission-systems"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        ImGui.TextDisabled("Mission systems");
        foreach (var (nickname, triggers) in systems)
        {
            var system = window.Session?.GameData?.Items.Systems.Get(nickname);
            if (ImGui.MenuItem(Missions.MissionView.SystemItem(window.Session, nickname, triggers), "",
                    System?.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase) == true, system != null))
            {
                window.OpenSystem(system!);
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
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
    /// A click without a drag selects an item of the stack that <see cref="ScenePicker"/> finds under the mouse.
    /// <see cref="PickCycle"/> tells which item. A click selects the first item. A click again at the same place, or an
    /// Alt+click, selects the next item. A click on empty space clears the selection. A click on an axis end of the axis widget looks at the
    /// scene from that side. An Alt+drag stays an orbit.
    /// </summary>
    /// <param name="activated">The scene button became active in this frame.</param>
    /// <param name="deactivated">The scene button stopped being active in this frame.</param>
    private void Pick(SceneProjection projection, SystemIndex index, bool activated, bool deactivated)
    {
        if (activated && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            dragStart = ImGui.GetMousePos();
        }

        var mouse = ImGui.GetMousePos();
        if (!deactivated || !ImGui.IsMouseReleased(ImGuiMouseButton.Left) ||
            Vector2.Distance(dragStart, mouse) > PickCycle.SamePlace)
        {
            return;
        }

        var settings = window.Settings;
        var pick = scenePicker.Pick(projection, mouse, index,
            new PickView(controller.Position, settings.ShowZones, settings.ShowNebulae, settings.ShowFog));
        if (pick.Axis is { } axis)
        {
            controller.LookAlong(-axis);
            return;
        }

        var (changes, selection) = cycle.Click(pick.Items, mouse,
            new CameraPose(controller.Position, controller.Orientation), window.Selection, ImGui.GetIO().KeyAlt);
        if (changes)
        {
            window.Selection = selection;
        }
    }

    /// <summary>The first object of the pick stack under a screen point, for a dropped loadout or faction (decision D281
    /// of the M23 design), or null.</summary>
    private SystemObject? ObjectAt(SceneProjection projection, Vector2 mouse)
    {
        if (Index == null)
        {
            return null;
        }

        var settings = window.Settings;
        return scenePicker.Pick(projection, mouse, Index,
                new PickView(controller.Position, settings.ShowZones, settings.ShowNebulae, settings.ShowFog))
            .Items.OfType<SystemObject>().FirstOrDefault();
    }

    private void DrawWorld(int x, int y, int width, int height)
    {
        if (renderer == null)
        {
            return;
        }

        var context = window.RenderContext;
        var clearColor = context.ClearColor;
        pass = new DrawPass(camera, sky, window.Settings.ShowZones, window.Selection, hovered);
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
    /// Renders the open system into a new texture from a camera of the agent (section 7.7 of the MCP1 design). The camera
    /// of the user does not move. The image has the view toggles of the user, except the Zones toggle, which the call
    /// gives. It shows no selection and no hover. The scene first applies the changes of W, so the image shows W. Call it
    /// on the UI thread and never in the draw-list callback of the Scene, because the renderer shares the command buffer
    /// of the window.
    /// </summary>
    /// <returns>The texture. The caller owns it. Its rows have the order of a render target, the bottom row
    /// first.</returns>
    /// <exception cref="InvalidOperationException">No system is open.</exception>
    public Texture2D RenderShot(ShotCamera shot, int width, int height, bool zones)
    {
        if (world == null || renderer == null || System == null)
        {
            throw new InvalidOperationException("No system is open.");
        }

        sync!.Flush();
        var context = window.RenderContext;
        var restoreTarget = context.RenderTarget;
        var clearColor = context.ClearColor;
        var userPass = pass;
        var lens = ShotLens.Of(shot, width, height);
        var target = new RenderTarget2D(context, width, height);
        var drawn = false;
        pass = new DrawPass(lens, new ShotLens(shot, width, height, StarspherePass.SkyRange), zones, null, null);
        renderer.Camera = lens;
        context.RenderTarget = target;
        context.PushViewport(0, 0, width, height);
        try
        {
            ApplyViewOptions();
            // The asteroid fields place their asteroids around the camera of the update, so the image updates the world
            // with its own camera. The next draw of the panel updates the world again with the camera of the user.
            world.RenderUpdate(0);
            context.Cull = true;
            context.DepthEnabled = true;
            context.Wireframe = window.Settings.Wireframe;
            renderer.Draw(width, height);
            drawn = true;
        }
        finally
        {
            // The window draws on after a failed image, so the render state goes back in each case.
            context.Wireframe = false;
            context.PopViewport();
            context.ClearColor = clearColor;
            context.DepthEnabled = false;
            context.BlendMode = BlendMode.Normal;
            context.Cull = false;
            context.RenderTarget = restoreTarget;
            renderer.Camera = camera;
            pass = userPass;
            target.Dispose(keepTexture: drawn);
        }

        return target.Texture;
    }

    /// <summary>The items that an image of the agent can show, with their bounding spheres: each object of the open
    /// system, and each zone when the zone volumes show.</summary>
    public IEnumerable<(object Item, Vector3 Center, float Radius)> ShotItems(bool zones)
    {
        foreach (var obj in objects.Keys)
        {
            yield return (obj, obj.Position, Radius(obj));
        }

        if (zones && System != null)
        {
            foreach (var zone in System.Zones)
            {
                yield return (zone, zone.Position, ZoneRadius(zone));
            }
        }
    }

    /// <summary>The bounding sphere of an item of the open system, for a frame of an image of the agent, or null for an
    /// item that the scene does not show. A light is a point, so it gets a small sphere.</summary>
    public (Vector3 Center, float Radius)? SphereOf(object item) => item switch
    {
        SystemObject obj when objects.ContainsKey(obj) => (obj.Position, Radius(obj)),
        Zone zone when Contains(zone) => (zone.Position, ZoneRadius(zone)),
        LightSource light when Contains(light) => (light.Light.Position, LightRadius),
        Nebula or AsteroidField when Contains(item) && SceneItems.ZoneOf(item) is { } area =>
            (area.Position, ZoneRadius(area)),
        _ => null,
    };

    /// <summary>The radius of the sphere of a light in a frame of the agent, in meters.</summary>
    private const float LightRadius = 100;

    /// <summary>The radius of the sphere that holds the volume of a zone, as the Scene draws the volume. A sphere and an
    /// ellipsoid have the size as their radii, a box has the size as its full extent, and a cylinder and a ring have
    /// (outer radius, height).</summary>
    internal static float ZoneRadius(Zone zone) => zone.Shape switch
    {
        ShapeKind.Sphere => zone.Size.X,
        ShapeKind.Ellipsoid => Math.Max(zone.Size.X, Math.Max(zone.Size.Y, zone.Size.Z)),
        ShapeKind.Box => (zone.Size / 2).Length(),
        _ => new Vector2(zone.Size.X, zone.Size.Y / 2).Length(),
    };

    /// <summary>
    /// Draws the starsphere, then the grid. The renderer calls this after the opaque objects and before the transparent
    /// objects, at the place of its own starsphere pass. So planets hide the grid and the starsphere, and nebulae tint
    /// them.
    /// </summary>
    private void DrawBackground()
    {
        StarspherePass.Draw(renderer!, pass.Sky, window.Settings.ShowStarsphere);
        if (window.Settings.ShowGrid)
        {
            grid.Draw(window.RenderContext, pass.Camera, GridColor, AxisXColor, AxisZColor);
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
        if (missionLayer.Active && renderer != null)
        {
            missionLayer.DrawLines(renderer.DebugRenderer, window.Settings.ShowMissionItems);
        }

        DrawSelection();
    }

    /// <summary>
    /// Draws the volumes of the fields and nebulae when the Zones toggle is on, and the volume of the selected and the
    /// hovered item always: a zone, or the zone of a nebula or a field. The volume of a nebula or a field has the color
    /// of its kind, so a shared zone has the color of the selected kind. A volume that holds the camera shows its inside
    /// faces.
    /// </summary>
    private void DrawZones()
    {
        if (zoneRenderer == null || Index == null)
        {
            return;
        }

        zoneRenderer.Begin(pass.Camera.Position);
        var selected = Highlight(pass.Selected);
        var hover = Highlight(pass.Hovered);
        if (pass.Zones)
        {
            foreach (var zone in Index.VolumeZones)
            {
                if (!ReferenceEquals(zone, selected?.Zone) && !ReferenceEquals(zone, hover?.Zone))
                {
                    AddZone(zone, VolumeAlpha, Index.KindOf(zone)!);
                }
            }
        }

        if (selected is var (selectedZone, selectedKind))
        {
            // A selected volume that holds the camera keeps the normal opacity (decision D161 of the M22 design).
            AddZone(selectedZone, selectedZone.ContainsPoint(pass.Camera.Position) ? VolumeAlpha : HighlightAlpha,
                selectedKind);
        }

        if (hover is var (hoverZone, hoverKind) && !ReferenceEquals(hoverZone, selected?.Zone))
        {
            AddZone(hoverZone, HighlightAlpha, hoverKind);
        }

        zoneRenderer.Finish();
    }

    /// <summary>The zone that an item of the open system highlights, and the kind that gives its color. A zone highlights
    /// itself in the color of its role. A nebula or a field highlights its zone in the color of the item. Null for another
    /// item.</summary>
    private (Zone Zone, ObjectKind Kind)? Highlight(object? item) =>
        item is Zone or Nebula or AsteroidField && Contains(item) && SceneItems.ZoneOf(item) is { } zone &&
        Index!.KindOf(item) is { } kind
            ? (zone, kind)
            : null;

    private bool IsHighlighted(Zone zone) => ReferenceEquals(zone, pass.Selected) || ReferenceEquals(zone, pass.Hovered);

    /// <summary>Adds the volume of a zone in the color of a kind.</summary>
    private void AddZone(Zone zone, float alpha, ObjectKind kind)
    {
        var color = new Color4(kind.Color with { W = alpha });
        var inside = zone.ContainsPoint(pass.Camera.Position);
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
        var showZones = pass.Zones;
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

        if (pass.Selected is LightSource light && Contains(light))
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
    /// Draws the selection in the accent color. A selected object shows its wireframe. A selected nebula, field or zone
    /// shows the outline of its zone (decision D163 of the M22 design). A model without wireframe data, for example a
    /// planet sphere, gets its oriented bounding box. A patrol path keeps its line.
    /// </summary>
    private void DrawSelection()
    {
        if (renderer != null && Highlight(pass.Selected) is var (zone, _) &&
            !(pass.Selected is Zone && Index!.PatrolPaths.Contains(zone)))
        {
            foreach (var (a, b) in ZoneOutline.Lines(zone))
            {
                renderer.DebugRenderer.DrawLine(a, b, SelectionColor);
            }

            return;
        }

        if (pass.Selected is not SystemObject selected || !objects.TryGetValue(selected, out var obj) ||
            obj.RenderComponent is not ModelRenderer { Model: { } model } || renderer == null)
        {
            return;
        }

        var transform = obj.Transform.Matrix();
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

    /// <summary>
    /// The section that the gizmo moves, or null for another selection. For an object, a zone or a light of the open
    /// system, it is the section of the item. For a nebula or a field, it is the section of its zone. As Unity moves the
    /// Transform of the selected GameObject, the gizmo moves the zone of the item (decision D159 of the M22 design).
    /// </summary>
    private SectionRef? SelectedSection() => window.Selection switch
    {
        Nebula or AsteroidField when Contains(window.Selection) && SceneItems.ZoneOf(window.Selection) is { } zone =>
            SectionOf(zone),
        (SystemObject or Zone or LightSource) and var item when Contains(item) => SectionOf(item),
        _ => null,
    };

    private float Radius(SystemObject obj) =>
        ScenePicker.Radius(objects.TryGetValue(obj, out var go) &&
                           go.RenderComponent is ModelRenderer { Model: { } model } ? model : null);

    public void Dispose()
    {
        Close();
        grid.Dispose();
    }
}
