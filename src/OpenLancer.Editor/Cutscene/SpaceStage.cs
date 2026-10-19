using System;
using System.Collections.Generic;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.World;
using OpenLancer.Editor.Scene;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The star system behind a space script of the Cutscene tab (decisions D640, D646, D647 and D653 of the space context
/// design): a world of its own with the render path of the Scene view. The players of the preview make the objects of
/// their scripts in this world, and the stage draws it. The stage never shares a world or a renderer with the Scene
/// view, and it never changes the system.
/// </summary>
/// <remarks>
/// The stage outlives each player that uses its world. So the workspace always disposes the clock of the preview
/// before it disposes the stage. A Debug build ends the process when a physics world is not disposed (OL-0021).
/// </remarks>
internal sealed class SpaceStage : IDisposable
{
    // The objects of the system, which always show and which the stage updates.
    private readonly HashSet<GameObject> systemObjects;

    // The objects of the player of the last draw, as a set.
    private (IReadOnlyCollection<GameObject>? List, HashSet<GameObject>? Set) shown;

    // The sky camera of the current draw, for the starsphere pass.
    private ICamera? sky;

    private SpaceStage(string system, SystemRenderer renderer, GameWorld world)
    {
        System = system;
        Renderer = renderer;
        World = world;
        systemObjects = new HashSet<GameObject>(world.Objects);
        renderer.OpaqueHook = DrawSky;
    }

    /// <summary>The nickname of the system.</summary>
    public string System { get; }

    /// <summary>The world of the system. The players make the objects of their scripts in it.</summary>
    public GameWorld World { get; }

    public SystemRenderer Renderer { get; }

    /// <summary>
    /// Loads a system of the loaded game data as <c>SceneView.Load</c> does, without the clone, because the stage
    /// changes nothing in the system. The starsphere draws with a sky camera, as in the Scene view. The objects use the
    /// equipment type of the editor, so the stations look as they look in the Scene view.
    /// </summary>
    public static SpaceStage Load(StarSystem system, GameDataManager data, GameResourceManager resources, Game game)
    {
        var renderer = new SystemRenderer(new LensCamera(), resources, game) { DrawStarsphere = false };
        GameWorld? world = null;
        try
        {
            // The world needs its physics, because the projectile renderer of LoadSystem reads the projectile manager.
            world = new GameWorld(renderer, null, resources, null, true) { DefaultObjectType = EquipmentType.Editor };
            // The world sets a debug draw of the physics, which the stage does not show.
            renderer.PhysicsHook = null;
            data.LoadAllSystem(system);
            world.LoadSystem(system, resources, null, false);
            renderer.LoadLights(system);
            renderer.LoadStarspheres(system);
            renderer.LoadZones(system.AsteroidFields, system.Nebulae);
            renderer.BackgroundOverride = system.BackgroundColor;
            renderer.SystemLighting.Ambient = new Color4(system.AmbientColor, 1);
            return new SpaceStage(system.Nickname, renderer, world);
        }
        catch
        {
            world?.Dispose();
            renderer.Dispose();
            throw;
        }
    }

    /// <summary>Advances the objects of the system only (decision D647). Each player steps the objects of its own
    /// script.</summary>
    public void Update(double seconds)
    {
        foreach (var obj in systemObjects)
        {
            obj.Update(seconds, World);
        }
    }

    /// <summary>
    /// Shows the objects of the system always. Each other object of the world shows only when it belongs to the shown
    /// player and the script objects show (rule S-7 of the cutscene fixes plan). The flags of the system objects do not
    /// change.
    /// </summary>
    /// <param name="objects">The objects of the world.</param>
    /// <param name="system">The objects of the system.</param>
    /// <param name="shown">The objects of the player that the view draws, or null.</param>
    /// <param name="scriptObjects">The toggle "Script objects".</param>
    internal static void ApplyVisibility(IReadOnlyList<GameObject> objects, ISet<GameObject> system,
        ISet<GameObject>? shown, bool scriptObjects)
    {
        foreach (var obj in objects)
        {
            if (system.Contains(obj))
            {
                continue;
            }

            if (scriptObjects && shown != null && shown.Contains(obj))
            {
                obj.Flags &= ~GameObjectFlags.Hidden;
            }
            else
            {
                obj.Flags |= GameObjectFlags.Hidden;
            }
        }
    }

    /// <summary>Draws the system and the objects of one player through a camera, into the current viewport.</summary>
    /// <param name="objects">The objects of the player that the view draws, or null.</param>
    /// <param name="scriptObjects">The toggle "Script objects".</param>
    /// <param name="camera">The camera of the view.</param>
    /// <param name="skyCamera">The camera of the starsphere, with the pose of the view camera.</param>
    /// <param name="viewport">The viewport, in pixels.</param>
    /// <param name="seconds">The script time of this frame. The effects advance by it.</param>
    public void Draw(IReadOnlyCollection<GameObject>? objects, bool scriptObjects, ICamera camera, ICamera skyCamera,
        Rectangle viewport, double seconds)
    {
        if (!ReferenceEquals(shown.List, objects))
        {
            shown = (objects, objects == null ? null : new HashSet<GameObject>(objects));
        }

        ApplyVisibility(World.Objects, systemObjects, shown.Set, scriptObjects);
        sky = skyCamera;
        Renderer.Camera = camera;
        World.RenderUpdate(seconds);
        Renderer.Draw(viewport.Width, viewport.Height);
    }

    /// <summary>The starsphere at the place of the engine starsphere pass (see <see cref="StarspherePass"/>).</summary>
    private void DrawSky()
    {
        if (sky != null)
        {
            StarspherePass.Draw(Renderer, sky, true);
        }
    }

    /// <summary>Frees the physics world (OL-0021) and the meshes of the asteroid fields (OL-0022).</summary>
    public void Dispose()
    {
        Renderer.OpaqueHook = null;
        World.Dispose();
        Renderer.Dispose();
    }
}

/// <summary>
/// Holds at most one stage and its key: the nickname of the system and the load of the game data (decision D646). A
/// script of the same system and the same data uses the same stage. The factory makes the stage, so a test can give a
/// fake.
/// </summary>
internal sealed class StageCache<TStage> where TStage : class, IDisposable
{
    /// <summary>The stage, or null.</summary>
    public TStage? Stage { get; private set; }

    /// <summary>The system and the game data of the stage, or null.</summary>
    public (string System, object Data)? Key { get; private set; }

    /// <summary>True when the stage is the stage of a system of the data.</summary>
    public bool Holds(string system, object data) =>
        Stage != null && Key is { } key && key.System.Equals(system, StringComparison.OrdinalIgnoreCase) &&
        ReferenceEquals(key.Data, data);

    /// <summary>
    /// The stage of a system of the data. Another key disposes the old stage before the factory loads the new one. A
    /// factory that throws leaves no stage.
    /// </summary>
    public TStage Get(string system, object data, Func<TStage> load)
    {
        if (Holds(system, data))
        {
            return Stage!;
        }

        Reset();
        var stage = load();
        (Stage, Key) = (stage, (system, data));
        return stage;
    }

    /// <summary>Disposes the stage.</summary>
    public void Reset()
    {
        var stage = Stage;
        (Stage, Key) = (null, null);
        stage?.Dispose();
    }
}
