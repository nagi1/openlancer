using System;
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.Render.Cameras;
using LibreLancer.Thn;
using LibreLancer.Utf.Anm;
using LibreLancer.World;
using OpenLancer.Editor.Assets;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Inspector;

/// <summary>What a drag on the clip preview does.</summary>
internal enum PreviewDrag
{
    None,
    Pan,
    Orbit,
    Zoom,
}

/// <summary>
/// The clip preview of the animation picker: it plays a clip in a loop on a copy of a character of the scene, in its own
/// world with the lights of the asset thumbnails. The controls follow the Unity animation preview: a drag with the left or
/// the middle button pans, a drag with the right button or Alt and the left button orbits, and the wheel or Alt and the
/// right button zooms. F puts back the view.
/// </summary>
internal sealed class ThnAnimationPreview(EditorWindow window) : IDisposable
{
    /// <summary>The controls, for the hint of the pane.</summary>
    public const string Controls =
        "Drag or middle drag: pan. Right drag or Alt+drag: orbit. Wheel or Alt+right drag: zoom. F: reset the view.";

    /// <summary>The size of the render target in pixels.</summary>
    private const int Size = 512;

    /// <summary>The vertical field of view of the camera in degrees, as <see cref="LookAtCamera"/> has it.</summary>
    private const float FieldOfView = 50;

    /// <summary>The angle of an orbit for each pixel of a drag, in radians.</summary>
    private const float OrbitSpeed = 0.01f;

    /// <summary>The wheel steps of a zoom for each pixel of a drag.</summary>
    private const float DragZoomSpeed = 0.02f;

    /// <summary>The objects of the clip that the preview plays.</summary>
    /// <param name="Duration">The length of one loop in seconds.</param>
    /// <param name="Character">The copy of the character. Its object is the actor in the world.</param>
    private sealed record Scene(GameWorld World, ThnSceneObject Character, DfmSkeletonManager Skeleton, float Duration);

    private readonly LookAtCamera camera = new();
    private EditorSession? owner;
    private SystemRenderer? renderer;
    private RenderTarget2D? target;
    private ImTextureRef? image;
    private Scene? scene;
    private (CharacterModels Models, string Clip, string? Idle)? current;
    private string? problem;
    private double playTime;
    private long lastFrame;
    private Vector3 cameraCenter;
    private float cameraDistance;
    private float zoom = 1;
    private float yaw;
    private float pitch;
    private Vector3 pan;

    /// <summary>Plays a clip on a copy of a character and draws the view and its controls.</summary>
    /// <param name="models">The models of the character of the scene, or null when the scene has no such
    /// character.</param>
    /// <param name="clip">The name of the clip.</param>
    /// <param name="idle">The name of the idle that plays below the clip, or null (see
    /// <see cref="AnimationClips.IdleFor"/>).</param>
    public void Draw(CharacterModels? models, string clip, string? idle)
    {
        if (models == null)
        {
            ImGui.TextWrapped("The cutscene preview does not have this character now. Open the cutscene preview and " +
                              "wait until the scene loads.");
            return;
        }

        try
        {
            if (current is not { } key || key.Models != models || key.Clip != clip || key.Idle != idle)
            {
                Start(models, clip, idle);
            }

            if (problem != null || scene == null)
            {
                ImGui.TextWrapped(problem ?? "");
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = lastFrame == 0 ? 0 : Math.Min(0.1, Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds);
            lastFrame = now;
            playTime += elapsed;
            if (playTime >= scene.Duration)
            {
                // The clip starts again with a new copy of the character.
                Start(models, clip, idle);
                lastFrame = now;
            }

            var shown = scene!;
            Advance(shown.Skeleton, shown.Character, elapsed);
            var rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0);
            var center = cameraCenter + shown.Character.Translate + pan;
            var offset = Vector3.Transform(new Vector3(0, cameraDistance * 0.05f, -cameraDistance), rotation) * zoom;
            camera.Update(Size, Size, center + offset, center, Matrix4x4.CreateFromQuaternion(rotation));
            Render(shown.World, elapsed);
            if (ImGui.Button("Reset view"))
            {
                ResetView();
            }

            ImGui.SetItemTooltip("Puts back the camera of the clip preview. F does the same when the pointer is on the " +
                                 "clip preview.");
            var available = ImGui.GetContentRegionAvail();
            var side = Math.Max(64, Math.Min(available.X, available.Y));
            var origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##clip-preview", new Vector2(side),
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            var io = ImGui.GetIO();
            if (ImGui.IsItemHovered())
            {
                if (io.MouseWheel != 0)
                {
                    zoom = Zoom(zoom, io.MouseWheel);
                }

                if (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F, false))
                {
                    ResetView();
                }
            }

            if (ImGui.IsItemActive() && Dragged() is { } button)
            {
                switch (DragOf(button, io.KeyAlt))
                {
                    case PreviewDrag.Pan:
                        pan = Pan(pan, io.MouseDelta, rotation, cameraDistance * zoom, side);
                        break;
                    case PreviewDrag.Orbit:
                        (yaw, pitch) = Orbit(yaw, pitch, io.MouseDelta);
                        break;
                    case PreviewDrag.Zoom:
                        zoom = Zoom(zoom, DragZoom(io.MouseDelta));
                        break;
                }
            }

            ImGui.GetWindowDrawList().AddImage(image!.Value, origin, origin + new Vector2(side),
                new Vector2(0, 1), new Vector2(1, 0));
        }
        catch (Exception e)
        {
            problem = $"The editor cannot preview this clip: {e.Message}";
            FLLog.Warning("Clip preview", e.ToString());
            ImGui.TextWrapped(problem);
        }
    }

    /// <summary>
    /// What a drag with a mouse button does, as in the Unity animation preview: the left or the middle button pans, the
    /// right button or Alt and the left button orbits, and Alt and the right button zooms.
    /// </summary>
    internal static PreviewDrag DragOf(ImGuiMouseButton button, bool alt) => button switch
    {
        ImGuiMouseButton.Left => alt ? PreviewDrag.Orbit : PreviewDrag.Pan,
        ImGuiMouseButton.Right => alt ? PreviewDrag.Zoom : PreviewDrag.Orbit,
        ImGuiMouseButton.Middle => PreviewDrag.Pan,
        _ => PreviewDrag.None,
    };

    /// <summary>The yaw and the pitch after an orbit drag, in radians. The pitch stays from -90 to 90 degrees, as in the
    /// Unity animation preview.</summary>
    internal static (float Yaw, float Pitch) Orbit(float yaw, float pitch, Vector2 delta) =>
        (yaw - delta.X * OrbitSpeed, Math.Clamp(pitch - delta.Y * OrbitSpeed, -MathF.PI / 2, MathF.PI / 2));

    /// <summary>The pan after a drag: the point under the pointer moves with the pointer, in the plane of the
    /// view.</summary>
    /// <param name="rotation">The rotation of the camera.</param>
    /// <param name="distance">The distance of the camera from the center.</param>
    /// <param name="side">The size of the view in pixels.</param>
    internal static Vector3 Pan(Vector3 current, Vector2 delta, Quaternion rotation, float distance, float side)
    {
        var amount = 2 * distance * MathF.Tan(FieldOfView / 2 * MathF.PI / 180) / Math.Max(1, side);
        var right = Vector3.Transform(-Vector3.UnitX, rotation);
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        return current + (-delta.X * right + delta.Y * up) * amount;
    }

    /// <summary>The zoom after wheel steps: a step up moves the camera closer.</summary>
    internal static float Zoom(float current, float wheel) => Math.Clamp(current * MathF.Pow(0.85f, wheel), 0.35f, 4f);

    /// <summary>The wheel steps of a zoom drag: a drag to the right or up moves the camera closer, as in Unity.</summary>
    internal static float DragZoom(Vector2 delta) => (delta.X - delta.Y) * DragZoomSpeed;

    /// <summary>Advances the clip and moves the character by the root motion of the clip.</summary>
    internal static void Advance(DfmSkeletonManager skeleton, ThnSceneObject character, double elapsed)
    {
        skeleton.UpdateScripts(elapsed);
        character.Update();
    }

    /// <summary>The length of a clip: the longest channel of its joint maps and its object maps, and at least
    /// 0.1 s.</summary>
    internal static float ClipDuration(Script script)
    {
        var duration = 0f;
        foreach (var joint in script.JointMaps)
        {
            duration = Math.Max(duration, joint.Channel.Duration);
        }

        foreach (var obj in script.ObjectMaps)
        {
            duration = Math.Max(duration, obj.Channel.Duration);
        }

        return Math.Max(0.1f, duration);
    }

    /// <summary>Frees the render target and the renderer, and forgets the character and the session.</summary>
    public void Dispose()
    {
        scene?.World.Dispose();
        scene = null;
        renderer?.Dispose();
        renderer = null;
        current = null;
        owner = null;
        problem = null;
        if (target != null)
        {
            ImGuiHelper.DeregisterTexture(target.Texture);
            target.Dispose();
            target = null;
            image = null;
        }
    }

    /// <summary>The button of the drag that is active now, or null.</summary>
    private static ImGuiMouseButton? Dragged()
    {
        foreach (var button in new[] { ImGuiMouseButton.Left, ImGuiMouseButton.Right, ImGuiMouseButton.Middle })
        {
            if (ImGui.IsMouseDragging(button))
            {
                return button;
            }
        }

        return null;
    }

    private void ResetView()
    {
        zoom = 1;
        yaw = 0;
        pitch = 0;
        pan = Vector3.Zero;
    }

    /// <summary>Makes a new copy of the character and starts the clip on it. The script comes from the animation table of
    /// the engine, so it is the script that the game plays.</summary>
    private void Start(CharacterModels models, string clip, string? idle)
    {
        var session = window.Session ?? throw new InvalidOperationException("No game data is open.");
        if (!ReferenceEquals(owner, session))
        {
            // The renderer holds the resources of the session.
            Dispose();
            owner = session;
        }

        current = (models, clip, idle);
        problem = null;
        lastFrame = 0;
        playTime = 0;
        scene?.World.Dispose();
        scene = null;
        var scripts = session.GameData!.GetCharacterAnimations().Scripts;
        if (!scripts.TryGetValue(clip, out var script))
        {
            problem = ThnAnimationPicker.UnknownClip;
            return;
        }

        var skeleton = new DfmSkeletonManager(models.Body, models.Head, models.LeftHand, models.RightHand);
        if (idle != null && scripts.TryGetValue(idle, out var idleScript))
        {
            skeleton.StartScript(idleScript, 0, 1, 0, true);
        }

        var duration = ClipDuration(script);
        skeleton.StartScript(script, 0, 1, duration);
        renderer ??= new SystemRenderer(camera, session.Resources, window)
        {
            NullColor = ThumbnailRenderer.NullColor,
            SystemLighting = ThumbnailRenderer.Lighting,
        };
        target ??= new RenderTarget2D(window.RenderContext, Size, Size);
        image ??= ImGuiHelper.RegisterTexture(target.Texture);
        var world = new GameWorld(renderer, null, session.Resources, null, false);
        var actor = new GameObject
        {
            RenderComponent = new CharacterRenderer(skeleton)
            {
                Accessory = models.Accessory,
                AccessoryModel = models.AccessoryModel,
            },
        };
        actor.SetTransform(Transform3D.Identity);
        world.AddObject(actor);
        var character = new ThnSceneObject { Name = "Clip preview", Object = actor, Rotate = Quaternion.Identity };
        scene = new Scene(world, character, skeleton, duration);
        var bounds = skeleton.Bounds;
        cameraCenter = (bounds.Min + bounds.Max) * 0.5f;
        cameraDistance = Math.Max(0.5f, Vector3.Distance(bounds.Min, bounds.Max) * 0.5f) * ThumbnailRenderer.CameraDistance;
    }

    private void Render(GameWorld world, double elapsed)
    {
        var context = window.RenderContext;
        var previous = context.RenderTarget;
        context.RenderTarget = target;
        context.PushViewport(0, 0, Size, Size);
        try
        {
            context.Cull = true;
            world.RenderUpdate(elapsed);
            renderer!.Draw(Size, Size);
        }
        finally
        {
            context.PopViewport();
            context.DepthEnabled = false;
            context.BlendMode = BlendMode.Normal;
            context.Cull = false;
            context.RenderTarget = previous;
        }
    }
}
