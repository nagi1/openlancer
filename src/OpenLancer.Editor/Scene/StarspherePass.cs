using System.Numerics;
using LibreLancer;
using LibreLancer.Render;
using LibreLancer.Render.Materials;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// Draws the starsphere of the open system with a sky camera. The scene camera frames a whole system with a near plane
/// of up to 1,000 units, and the starsphere models are smaller than that. So the engine starsphere pass culls and clips
/// them. The scene view turns that pass off (<see cref="SystemRenderer.DrawStarsphere"/>) and calls this pass from the
/// opaque hook, which the engine calls at the same place in the frame.
/// </summary>
/// <remarks>
/// The pass follows the starsphere block of <c>SystemRenderer.Draw</c> (SystemRenderer.cs:408-469 at the pinned
/// commit), with public members only. A submodule update that changes that block must change this pass too. The pass
/// does not cull parts. The engine culls only to save fill rate, and a starsphere has few parts.
/// </remarks>
internal static class StarspherePass
{
    /// <summary>What the pass draws in one frame.</summary>
    /// <param name="Stars">The starsphere models draw.</param>
    /// <param name="FogTint">The fog of the nebula that holds the camera tints the view after the stars.</param>
    internal readonly record struct SkyPlan(bool Stars, bool FogTint);

    /// <summary>
    /// The rule of the engine. A nebula that holds the camera counts only when the nebulae draw. The stars draw when
    /// the Starsphere toggle is on and the camera is not fully in the fog of that nebula. In full fog, the renderer
    /// clears the view with the fog color. The fog tint draws after the stars when a nebula counts.
    /// </summary>
    /// <param name="showStarsphere">The Starsphere toggle of the view.</param>
    /// <param name="showNebulae">The nebulae draw (<see cref="SystemRenderer.DrawNebulae"/>).</param>
    /// <param name="inNebula">The zone of a nebula holds the camera.</param>
    /// <param name="fullyFogged">The camera is fully in the fog of that nebula
    /// (<see cref="NebulaRenderer.FogTransitioned"/>).</param>
    internal static SkyPlan Plan(bool showStarsphere, bool showNebulae, bool inNebula, bool fullyFogged)
    {
        var nebula = showNebulae && inNebula;
        var stars = showStarsphere && !(nebula && fullyFogged);
        return new SkyPlan(stars, stars && nebula);
    }

    /// <summary>
    /// Draws the starsphere models at the camera position behind all objects (the depth range 1 to 1), then the fog
    /// tint. After the pass, the scene camera and the full depth range apply again, for the grid and the transparent
    /// pass.
    /// </summary>
    /// <param name="renderer">The renderer of the open system. Its camera is the scene camera.</param>
    /// <param name="sky">The sky camera (see <see cref="SceneCamera.Apply"/>).</param>
    /// <param name="showStarsphere">The Starsphere toggle of the view.</param>
    public static void Draw(SystemRenderer renderer, ICamera sky, bool showStarsphere)
    {
        var nebula = renderer.ObjectInNebula(sky.Position);
        // The engine reads the fog only when the nebulae draw (SystemRenderer.CheckNebulae). The fog of a cylinder or
        // ring zone logs an error (Zone.ScaledDistance), so the pass does not read it when the nebulae do not draw.
        var fullyFogged = nebula != null && renderer.DrawNebulae && nebula.FogTransitioned();
        var plan = Plan(showStarsphere, renderer.DrawNebulae, nebula != null, fullyFogged);
        if (!plan.Stars)
        {
            return;
        }

        var context = renderer.RenderContext;
        context.DepthRange = new Vector2(1, 1);
        context.SetCamera(sky);
        var models = renderer.StarSphereModels;
        for (var i = 0; i < models.Length; i++)
        {
            // Only a cutscene gives each model a world and a lighting (Cutscene.cs). Without them, a model is at the
            // camera position and has no lights.
            var world = Matrix4x4.CreateTranslation(sky.Position);
            if (renderer.StarSphereWorlds is { } worlds && i < worlds.Length)
            {
                world = worlds[i] * world;
            }

            var lighting = renderer.StarSphereLightings is { } lightings && i < lightings.Length
                ? lightings[i]
                : Lighting.Empty;
            models[i].DrawImmediate(context, renderer.ResourceManager, world, ref lighting, BasicMaterial.ForceAlpha);
        }

        if (plan.FogTint)
        {
            nebula!.RenderFogTransition(context);
        }

        context.SetCamera(renderer.Camera);
        context.DepthRange = new Vector2(0, 1);
    }
}
