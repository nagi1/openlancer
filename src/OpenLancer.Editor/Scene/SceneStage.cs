using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Graphics;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.World;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Scene;

/// <summary>What one draw of a stage shows: the cameras, the zone volumes, the highlighted items and the lines of the
/// panel.</summary>
/// <param name="Sky">The camera of the starsphere pass.</param>
/// <param name="Zones">The volumes of the fields, nebulae and zones and the patrol paths show (the Zones
/// toggle).</param>
/// <param name="Selected">The selected item, which shows in the accent color, or null.</param>
/// <param name="Hovered">The item under the mouse, whose zone volume shows, or null.</param>
/// <param name="PanelLines">The lines of the panel show (see <see cref="SceneStage.PanelLines"/>).</param>
internal readonly record struct StagePass(
    ICamera Camera,
    ICamera Sky,
    bool Zones,
    object? Selected,
    object? Hovered,
    bool PanelLines);

/// <summary>
/// The 3D world of one star system that the editor draws. It holds a clone of the system with the unsaved edits of W,
/// the world objects and the renderer. It also draws the helpers of the editor: the starsphere, the grid, the zone
/// volumes, the patrol paths, the trade lanes and the selection. The Scene panel draws the stage of the open system. The
/// agent render service draws a stage of another system into an image. So that image does not change the view of the
/// user (section 7.7 of the MCP1 design).
/// </summary>
/// <remarks>Use a stage on the UI thread only. A stage follows each change of W until it is disposed.</remarks>
internal sealed class SceneStage : IDisposable
{
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

    /// <summary>The radius of the sphere of a light in a frame of the agent, in meters.</summary>
    private const float LightRadius = 100;

    private readonly EditorWindow window;
    private readonly ICamera camera;
    private readonly GameResourceManager resources;
    private readonly SystemRenderer renderer;
    private readonly GameWorld world;
    private readonly ZoneRenderer zoneRenderer;
    private readonly GridRenderer grid;
    private readonly IDictionary<SystemObject, GameObject> objects;
    private readonly SceneSync sync;
    private readonly Action<SceneEffect>? indexChanged;

    // The cameras and the highlights of the draw that runs now: the draw of the panel or an image of the agent. The
    // hooks of the renderer read them.
    private StagePass pass;

    /// <summary>
    /// Loads a system of the game data of a session into a new world. Call <see cref="CatchUp"/> next, so that the stage
    /// shows the unsaved edits of W.
    /// </summary>
    /// <param name="camera">The camera of the renderer, for example the camera of the Scene panel. An image uses its own
    /// camera, then gives the renderer this camera again.</param>
    /// <param name="grid">The grid of the ecliptic plane. The caller owns it.</param>
    /// <param name="objects">The world object of each object of the system. The stage fills the map. The stage adds and
    /// removes the world objects of new and deleted objects, so the views that share the map see them.</param>
    /// <param name="mainView">True for the stage of the Scene panel: the item names of the session follow its sections,
    /// and the Console shows its problems. The stage of an image does neither.</param>
    /// <param name="indexChanged">Runs after a change of W changed the index or the names of the stage (see
    /// <see cref="ISceneHost.IndexChanged"/>), or null.</param>
    public SceneStage(EditorWindow window, StarSystem source, EditorSession session, ICamera camera, GridRenderer grid,
        IDictionary<SystemObject, GameObject> objects, bool mainView, Action<SceneEffect>? indexChanged)
    {
        this.window = window;
        this.camera = camera;
        this.grid = grid;
        this.objects = objects;
        this.indexChanged = indexChanged;
        Data = session.GameData!;
        resources = session.Resources;
        var system = source.Clone();
        // The engine starsphere pass uses the near plane of the scene camera, which is too far at the whole-system view.
        // So the stage draws the starsphere in its own pass (DrawBackground).
        renderer = new SystemRenderer(camera, resources, window) { DrawStarsphere = false };
        try
        {
            world = new GameWorld(renderer, null, resources, null, true) { DefaultObjectType = EquipmentType.Editor };
            Data.LoadAllSystem(system);
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
            pass = new StagePass(camera, camera, false, null, null, false);
            sync = new SceneSync(window, session, Data.Items, resources, system, world, renderer, objects, IndexChanged,
                mainView);
        }
        catch
        {
            // A Debug build ends the process when a physics world is not disposed (OL-0021), so a failed load frees
            // what it made.
            objects.Clear();
            zoneRenderer?.Dispose();
            world?.Dispose();
            renderer.Dispose();
            throw;
        }
    }

    /// <summary>The clone of the system that the stage shows.</summary>
    public StarSystem System { get; }

    /// <summary>The system of the game data that the clone came from.</summary>
    public StarSystem Source => Index.Source;

    /// <summary>The game data that the system came from.</summary>
    public GameDataManager Data { get; }

    /// <summary>The shared data of the system: its zone roles, volumes, paths and lanes.</summary>
    public SystemIndex Index { get; private set; }

    /// <summary>Draws the lines of a panel into the line list of a draw whose <see cref="StagePass.PanelLines"/> is
    /// true, for example the paths of the open mission. Null draws none.</summary>
    public Action<LineRenderer>? PanelLines { get; set; }

    /// <summary>True while W has changes that the stage did not apply yet (see <see cref="SceneSync.IsBehind"/>).</summary>
    public bool IsBehind => sync.IsBehind;

    /// <summary>True when a section of the system waits for the reload of the game data (see
    /// <see cref="SceneSync.WaitsForReload"/>).</summary>
    public bool WaitsForReload => sync.WaitsForReload;

    /// <summary>
    /// Applies the unsaved edits of W that the loaded game data does not have. Call it once, after the stage is in
    /// place, because a change of the index can run <c>indexChanged</c> during the catch-up.
    /// </summary>
    public void CatchUp() => sync.CatchUp();

    /// <summary>Applies the changes of W since the last flush (see <see cref="SceneSync.Flush"/>).</summary>
    public void Flush() => sync.Flush();

    /// <summary>The section of W that an object, a zone, a light, a nebula or a field of the system shows, or
    /// null.</summary>
    public SectionRef? SectionOf(object item) => sync.SectionOf(item);

    /// <summary>The object, zone, light, nebula or field of the system that a section of W sets, or null. A section of the
    /// fields of the system gives the system.</summary>
    public object? ItemOf(SectionRef section) => sync.ItemOf(section);

    /// <summary>Why the stage does not show the W state of a section. Empty when it shows it.</summary>
    public IReadOnlyList<Diagnostic> Problems(SectionRef section) => sync.Problems(section);

    /// <summary>True when the item is part of the system.</summary>
    public bool Contains(object item) => item switch
    {
        SystemObject obj => objects.ContainsKey(obj),
        Zone zone => System.Zones.Contains(zone),
        LightSource light => System.LightSources.Contains(light),
        Nebula or AsteroidField => Index.VolumeOf(item) != null,
        _ => false,
    };

    public void Update(double elapsed) => world.Update(elapsed);

    /// <summary>
    /// Gives the renderer the view toggles of the user, and updates the world for the next draw with the camera of the
    /// renderer. The asteroid fields place their asteroids around that camera.
    /// </summary>
    public void RenderUpdate(double elapsed)
    {
        ApplyViewOptions();
        world.RenderUpdate(elapsed);
    }

    /// <summary>Draws the stage into a rectangle of the screen with the camera of the renderer. The renderer calls the
    /// hooks of the helpers during the draw.</summary>
    public void Draw(int x, int y, int width, int height, StagePass draw)
    {
        var context = window.RenderContext;
        var clearColor = context.ClearColor;
        pass = draw;
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
    /// Renders the system into a new texture from a camera of the agent (section 7.7 of the MCP1 design). The camera of
    /// the renderer does not move. The image has the view toggles of the user, except the Zones toggle, which the call
    /// gives. It shows no selection, no hover and no lines of a panel. The stage first applies the changes of W, so the
    /// image shows W. Call it on the UI thread and never in the draw-list callback of the Scene, because the renderer
    /// shares the command buffer of the window.
    /// </summary>
    /// <returns>The texture. The caller owns it. Its rows have the order of a render target, the bottom row
    /// first.</returns>
    public Texture2D RenderShot(ShotCamera shot, int width, int height, bool zones)
    {
        sync.Flush();
        var context = window.RenderContext;
        var restoreTarget = context.RenderTarget;
        var clearColor = context.ClearColor;
        var kept = pass;
        var lens = ShotLens.Of(shot, width, height);
        var target = new RenderTarget2D(context, width, height);
        var drawn = false;
        pass = new StagePass(lens, new ShotLens(shot, width, height, StarspherePass.SkyRange), zones, null, null, false);
        renderer.Camera = lens;
        context.RenderTarget = target;
        context.PushViewport(0, 0, width, height);
        context.PushScissor(new Rectangle(0, 0, width, height), false);
        try
        {
            // The asteroid fields place their asteroids around the camera of the update, so the image updates the world
            // with its own camera. The next draw of the panel updates the world again with the camera of the user.
            RenderUpdate(0);
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
            context.PopScissor();
            context.ClearColor = clearColor;
            context.DepthEnabled = false;
            context.BlendMode = BlendMode.Normal;
            context.Cull = false;
            context.RenderTarget = restoreTarget;
            renderer.Camera = camera;
            pass = kept;
            target.Dispose(keepTexture: drawn);
        }

        return target.Texture;
    }

    /// <summary>The items that an image of the agent can show, with their bounding spheres. They are the objects of
    /// the system, and the zones when the zone volumes show.</summary>
    public IEnumerable<(object Item, Vector3 Center, float Radius)> ShotItems(bool zones)
    {
        foreach (var obj in objects.Keys)
        {
            yield return (obj, obj.Position, Radius(obj));
        }

        if (zones)
        {
            foreach (var zone in System.Zones)
            {
                yield return (zone, zone.Position, ZoneRadius(zone));
            }
        }
    }

    /// <summary>The bounding sphere of an item of the system for a frame of an image of the agent. Null for an item
    /// that the stage does not show. A light is a point, so it gets a small sphere.</summary>
    public (Vector3 Center, float Radius)? SphereOf(object item) => item switch
    {
        SystemObject obj when objects.ContainsKey(obj) => (obj.Position, Radius(obj)),
        Zone zone when Contains(zone) => (zone.Position, ZoneRadius(zone)),
        LightSource light when Contains(light) => (light.Light.Position, LightRadius),
        Nebula or AsteroidField when Contains(item) && SceneItems.ZoneOf(item) is { } area =>
            (area.Position, ZoneRadius(area)),
        _ => null,
    };

    /// <summary>The radius of the sphere that holds the volume of a zone, as the stage draws the volume. A sphere and an
    /// ellipsoid have the size as their radii. A box has the size as its full extent. A cylinder and a ring have (outer
    /// radius, height).</summary>
    internal static float ZoneRadius(Zone zone) => zone.Shape switch
    {
        ShapeKind.Sphere => zone.Size.X,
        ShapeKind.Ellipsoid => Math.Max(zone.Size.X, Math.Max(zone.Size.Y, zone.Size.Z)),
        ShapeKind.Box => (zone.Size / 2).Length(),
        _ => new Vector2(zone.Size.X, zone.Size.Y / 2).Length(),
    };

    /// <summary>The radius of the model of an object, as the picker uses it.</summary>
    public float Radius(SystemObject obj) =>
        ScenePicker.Radius(objects.TryGetValue(obj, out var go) &&
                           go.RenderComponent is ModelRenderer { Model: { } model } ? model : null);

    /// <summary>Stops the sync with W and frees the world and the renderer. The map of the world objects becomes
    /// empty.</summary>
    public void Dispose()
    {
        sync.Dispose();
        objects.Clear();
        world.Dispose();
        // With fork commit OL-0022, the renderer also frees the meshes of the asteroid fields.
        renderer.Dispose();
        zoneRenderer.Dispose();
    }

    /// <summary>
    /// A change of W changed what the index or the names of the stage read (see <see cref="ISceneHost.IndexChanged"/>).
    /// A change of a zone role or of a lane builds the index again.
    /// </summary>
    private void IndexChanged(SceneEffect effects)
    {
        if ((effects & (SceneEffect.Roles | SceneEffect.Lanes)) != 0)
        {
            Index = new SystemIndex(System, Index.Source);
        }

        indexChanged?.Invoke(effects);
    }

    /// <summary>Gives the renderer the view toggles of the user and the background and the ambient light of the
    /// system.</summary>
    private void ApplyViewOptions()
    {
        renderer.DrawNebulae = window.Settings.ShowNebulae;
        renderer.DrawFog = window.Settings.ShowFog;
        renderer.BackgroundOverride = System.BackgroundColor;
        renderer.SystemLighting.Ambient = new Color4(System.AmbientColor, 1);
    }

    /// <summary>
    /// Draws the starsphere, then the grid. The renderer calls this after the opaque objects and before the transparent
    /// objects, at the place of its own starsphere pass. So planets hide the grid and the starsphere, and nebulae tint
    /// them.
    /// </summary>
    private void DrawBackground()
    {
        StarspherePass.Draw(renderer, pass.Sky, window.Settings.ShowStarsphere);
        if (window.Settings.ShowGrid)
        {
            grid.Draw(window.RenderContext, pass.Camera, GridColor, AxisXColor, AxisZColor);
        }
    }

    /// <summary>
    /// Draws the editor helpers in the 3D scene: the zone volumes, the patrol paths, the trade lanes, the range of the
    /// selected light, the lines of the panel and the selection wireframe. The renderer calls this after the transparent
    /// objects. It clears its line list at the start of each frame, so the helper lines must go into the list here, in
    /// the hook.
    /// </summary>
    private void DrawHelpers()
    {
        DrawZones();
        DrawLines();
        if (pass.PanelLines)
        {
            PanelLines?.Invoke(renderer.DebugRenderer);
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

    /// <summary>The zone that an item of the system highlights, and the kind that gives its color. A zone highlights
    /// itself in the color of its role. A nebula or a field highlights its zone in the color of the item. Null for another
    /// item.</summary>
    private (Zone Zone, ObjectKind Kind)? Highlight(object? item) =>
        item is Zone or Nebula or AsteroidField && Contains(item) && SceneItems.ZoneOf(item) is { } zone &&
        Index.KindOf(item) is { } kind
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
                zoneRenderer.DrawSphere(zone.Position, size.X, zone.RotationMatrix, color, inside);
                break;
            case ShapeKind.Ellipsoid:
                zoneRenderer.DrawEllipsoid(zone.Position, size, zone.RotationMatrix, color, inside);
                break;
            case ShapeKind.Box:
                zoneRenderer.DrawCube(zone.Position, size, zone.RotationMatrix, color, inside);
                break;
            case ShapeKind.Cylinder:
                zoneRenderer.DrawCylinder(zone.Position, size.X, size.Y, zone.RotationMatrix, color, inside);
                break;
            case ShapeKind.Ring:
                // A ring zone stores its size as (outer radius, height, inner radius).
                zoneRenderer.DrawRing(zone.Position, size.Z, size.X, size.Y, zone.RotationMatrix, color, inside);
                break;
        }
    }

    /// <summary>
    /// Adds the lines of the frame: the patrol paths when the Zones toggle is on, the selected or hovered patrol path
    /// always, the trade lanes, and the range circles of the selected light.
    /// </summary>
    private void DrawLines()
    {
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
        if (Highlight(pass.Selected) is var (zone, _) && !(pass.Selected is Zone && Index.PatrolPaths.Contains(zone)))
        {
            foreach (var (a, b) in ZoneOutline.Lines(zone))
            {
                renderer.DebugRenderer.DrawLine(a, b, SelectionColor);
            }

            return;
        }

        if (pass.Selected is not SystemObject selected || !objects.TryGetValue(selected, out var obj) ||
            obj.RenderComponent is not ModelRenderer { Model: { } model })
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
            if (part.Wireframe is { } wire && resources.FindMesh(wire.MeshCRC)?.VertexResource is { } vertices)
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
}
