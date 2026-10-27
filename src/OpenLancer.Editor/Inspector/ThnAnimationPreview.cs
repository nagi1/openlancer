using System;
using System.Diagnostics;
using System.Numerics;
using System.Linq;
using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.Data.GameData;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.Render.Cameras;
using LibreLancer.Utf.Mat;
using LibreLancer.Utf.Anm;
using LibreLancer.Thn;
using LibreLancer.World;
using ImGuiNET;
using OpenLancer.Data.Editing.Lists;

namespace OpenLancer.Editor.Inspector;

/// <summary>Shows an animation on an independent copy of a scene character.</summary>
internal sealed class ThnAnimationPreview(EditorWindow window) : IDisposable
{
    private const int Size = 512;
    private EditorSession? owner;
    private SystemRenderer? renderer;
    private GameWorld? world;
    private GameObject? actor;
    private ThnSceneObject? character;
    private DfmSkeletonManager? skeleton;
    private AnmFile? archive;
    private string? archivePath;
    private float duration;
    private double playTime;
    private Vector3 cameraCenter;
    private float cameraDistance;
    private float zoom = 1;
    private float yaw;
    private float pitch;
    private Vector3 pan;
    private RenderTarget2D? target;
    private ImTextureRef? image;
    private readonly LookAtCamera camera = new();
    private (CharacterRenderer Source, AnimationClip Clip)? current;
    private long lastFrame;
    private string? problem;

    public void Draw(CharacterRenderer? source, AnimationClip clip)
    {
        if (source == null)
        {
            ImGui.TextWrapped("The scene character is not ready. Select a character target and wait for the scene to load.");
            return;
        }
        try
        {
            if (current is not { } key || !SameAppearance(key.Source, source) || key.Clip != clip)
                Start(source, clip);
            if (problem != null)
            {
                ImGui.TextWrapped(problem);
                return;
            }
            var now = Stopwatch.GetTimestamp();
            var elapsed = lastFrame == 0 ? 0 : Math.Min(0.1, Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds);
            lastFrame = now;
            playTime += elapsed;
            if (playTime >= duration)
            {
                Start(source, clip);
                lastFrame = now;
            }
            Advance(skeleton!, character!, elapsed);
            var center = cameraCenter + character!.Translate + pan;
            var rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0);
            var offset = Vector3.Transform(new Vector3(0, cameraDistance * 0.05f, -cameraDistance), rotation) * zoom;
            camera.Update(Size, Size, center + offset, center);
            Render(elapsed);
            if (ImGui.Button("Reset view"))
            {
                zoom = 1;
                yaw = pitch = 0;
                pan = Vector3.Zero;
            }
            var available = ImGui.GetContentRegionAvail();
            var side = Math.Max(64, Math.Min(350 * ImGuiHelper.Scale, Math.Min(available.X, available.Y)));
            var origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##animation-view", new Vector2(side, side),
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            var io = ImGui.GetIO();
            if (ImGui.IsItemHovered() && io.MouseWheel != 0)
                zoom = Zoom(zoom, io.MouseWheel);
            if (ImGui.IsItemActive())
            {
                var move = ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                           io.KeyShift && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
                if (move)
                    pan = Pan(pan, io.MouseDelta, rotation, cameraDistance * zoom, side);
                else if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    yaw -= io.MouseDelta.X * 0.01f;
                    pitch = Math.Clamp(pitch - io.MouseDelta.Y * 0.01f, -1.3f, 1.3f);
                }
            }
            ImGui.GetWindowDrawList().AddImage(image!.Value, origin, origin + new Vector2(side),
                new Vector2(0, 1), new Vector2(1, 0));
        }
        catch (Exception e)
        {
            problem = "Cannot preview this animation: " + e.Message;
            LibreLancer.FLLog.Warning("Animation preview", e.ToString());
            ImGui.TextWrapped(problem);
        }
    }

    private void Start(CharacterRenderer source, AnimationClip clip)
    {
        current = (source, clip);
        problem = null;
        lastFrame = 0;
        playTime = 0;
        var session = window.Session ?? throw new InvalidOperationException("No game data is open.");
        if (!ReferenceEquals(owner, session))
        {
            Dispose();
            owner = session;
            current = (source, clip);
        }
        world?.Dispose();
        world = null;
        var animation = session.GameData!.GetCharacterAnimations();
        if (!string.Equals(archivePath, clip.Archive, StringComparison.OrdinalIgnoreCase) || archive == null)
        {
            using var stream = session.GameData.Items.VFS.Open(clip.Archive);
            archive = new AnmFile(clip.Archive, stream);
            archive.Buffer.Commit();
            archivePath = clip.Archive;
        }
        if (!archive.Scripts.TryGetValue(clip.Name, out var script))
            throw new InvalidOperationException("The animation is not in the selected file.");
        var original = source.Skeleton;
        skeleton = new DfmSkeletonManager(original.Body, original.Head, original.LeftHand, original.RightHand);
        if (AnimationCatalog.Part(clip) is "Head" or "Hands")
        {
            var prefix = IdlePrefix(original.Body.Path, original.Head?.Path);
            var idle = animation.Scripts.Values.FirstOrDefault(x => x.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase));
            if (idle != null)
                skeleton.StartScript(idle, 0, 1, 0, true);
        }
        duration = ClipDuration(script);
        skeleton.StartScript(script, 0, 1, duration);
        renderer ??= CreateRenderer();
        target ??= new RenderTarget2D(window.RenderContext, Size, Size);
        if (image == null)
            image = ImGuiHelper.RegisterTexture(target.Texture);
        world = new GameWorld(renderer, null, session.Resources, null, false);
        actor = new GameObject { RenderComponent = new CharacterRenderer(skeleton)
        {
            Accessory = source.Accessory, AccessoryModel = source.AccessoryModel
        }};
        actor.SetTransform(Transform3D.Identity);
        world.AddObject(actor);
        character = new ThnSceneObject { Name = "Animation preview", Object = actor, Rotate = Quaternion.Identity };
        var bounds = skeleton.Bounds;
        var center = (bounds.Min + bounds.Max) * 0.5f;
        var radius = Math.Max(0.5f, Vector3.Distance(bounds.Min, bounds.Max) * 0.5f);
        cameraCenter = center;
        cameraDistance = radius * 2.35f;
    }

    internal static Vector3 Pan(Vector3 current, Vector2 delta, Quaternion rotation, float distance, float side)
    {
        var amount = 2 * distance * MathF.Tan(25 * MathF.PI / 180) / Math.Max(1, side);
        var right = Vector3.Transform(-Vector3.UnitX, rotation);
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        return current + (-delta.X * right + delta.Y * up) * amount;
    }

    internal static float Zoom(float current, float wheel) => Math.Clamp(current * MathF.Pow(0.85f, wheel), 0.35f, 4f);

    internal static void Advance(DfmSkeletonManager skeleton, ThnSceneObject character, double elapsed)
    {
        skeleton.UpdateScripts(elapsed);
        character.Update();
    }

    internal static float ClipDuration(Script script)
    {
        var duration = 0f;
        foreach (var joint in script.JointMaps)
            duration = Math.Max(duration, joint.Channel.Duration);
        foreach (var obj in script.ObjectMaps)
            duration = Math.Max(duration, obj.Channel.Duration);
        return Math.Max(0.1f, duration);
    }

    private static bool SameAppearance(CharacterRenderer first, CharacterRenderer second) =>
        ReferenceEquals(first.Skeleton.Body, second.Skeleton.Body) &&
        ReferenceEquals(first.Skeleton.Head, second.Skeleton.Head) &&
        ReferenceEquals(first.Skeleton.LeftHand, second.Skeleton.LeftHand) &&
        ReferenceEquals(first.Skeleton.RightHand, second.Skeleton.RightHand) &&
        ReferenceEquals(first.AccessoryModel, second.AccessoryModel);

    internal static string IdlePrefix(string body, string? head)
    {
        var appearance = body + " " + head;
        var female = appearance.Contains("female", StringComparison.OrdinalIgnoreCase) ||
                     appearance.Contains("juni", StringComparison.OrdinalIgnoreCase);
        return female ? "FMBODY_STND_IDLE" : "MLBODY_STND_IDLE";
    }

    private SystemRenderer CreateRenderer()
    {
        var lighting = new SystemLighting();
        foreach (var direction in new[] { new Vector3(0, -1, 0), new Vector3(0, 0, 1) })
            lighting.Lights.Add(new DynamicLight { Light = new RenderLight
                { Kind = LightKind.Directional, Direction = direction, Color = Color3f.White } });
        return new SystemRenderer(camera, window.Session!.Resources, window)
            { NullColor = new Color4(56, 57, 58, 255), SystemLighting = lighting };
    }

    private void Render(double elapsed)
    {
        var context = window.RenderContext;
        var previous = context.RenderTarget;
        context.RenderTarget = target;
        context.PushViewport(0, 0, Size, Size);
        try
        {
            context.Cull = true;
            world!.RenderUpdate(elapsed);
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

    public void Dispose()
    {
        world?.Dispose();
        renderer?.Dispose();
        current = null;
        owner = null;
        archive = null;
        archivePath = null;
        character = null;
        renderer = null;
        world = null;
        if (target != null)
        {
            ImGuiHelper.DeregisterTexture(target.Texture);
            target.Dispose();
            target = null;
            image = null;
        }
    }
}
