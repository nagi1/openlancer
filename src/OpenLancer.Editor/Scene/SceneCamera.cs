using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Render.Cameras;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The scene view camera, with Unity-style controls:
/// hold the right mouse button to fly (mouse look, WASD, Q and E, the wheel sets the speed),
/// Alt and the left mouse button to orbit, the middle mouse button to pan, and the wheel to zoom.
/// </summary>
internal sealed class SceneCamera
{
    private const float MouseTurnRate = 0.005f;

    // Straight up or down, so the top and bottom views of the axis widget are exact. The up vector of the camera turns
    // with the pitch, so the view stays valid at this limit.
    private const float MaxPitch = MathF.PI / 2;
    private const float MinDistance = 10;

    private Vector3 position;
    private Vector3 target;
    private float yaw;
    private float pitch;

    /// <summary>The fly speed, in world units per second.</summary>
    public float Speed { get; private set; } = 1000;

    public Vector3 Position => position;

    private Matrix4x4 Rotation => Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationY(yaw);
    private Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Rotation);
    private float Distance => Math.Max(MinDistance, Vector3.Distance(position, target));

    /// <summary>Looks at a sphere from above and to the front, so that all of it is in view.</summary>
    public void Frame(Vector3 center, float radius)
    {
        yaw = 0;
        pitch = -0.6f;
        target = center;
        position = center - Forward * Math.Max(radius * 1.6f, MinDistance);
        Speed = Math.Clamp(radius / 6, 100, 100_000);
    }

    /// <summary>Moves the camera to look at a point from a distance, and keeps the direction.</summary>
    public void Focus(Vector3 point, float distance)
    {
        target = point;
        position = point - Forward * Math.Max(distance, MinDistance);
    }

    /// <summary>
    /// Turns the camera to look in a direction at the orbit point, from the same distance. A view straight down or up
    /// has -Z at the top of the screen.
    /// </summary>
    public void LookAlong(Vector3 direction)
    {
        var distance = Distance;
        direction = Vector3.Normalize(direction);
        pitch = MathF.Asin(Math.Clamp(direction.Y, -1, 1));
        // Straight down or up, the yaw does not change the direction. Yaw 0 then puts -Z at the top of the screen.
        yaw = MathF.Abs(direction.Y) > 0.9999f ? 0 : MathF.Atan2(-direction.X, -direction.Z);
        position = target - Forward * distance;
    }

    /// <summary>Reads the mouse and the keyboard. Call this right after the scene view's invisible button.</summary>
    public void HandleInput(Keyboard keyboard, bool hovered, bool active, double elapsed)
    {
        var io = ImGui.GetIO();
        var delta = io.MouseDelta;

        if (active && ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            Fly(keyboard, delta, io.MouseWheel, io.KeyShift, (float)elapsed);
        }
        else if (active && io.KeyAlt && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var distance = Distance;
            Turn(delta);
            position = target - Forward * distance;
        }
        else if (active && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            var scale = Distance * 0.0015f;
            var move = (Vector3.Transform(Vector3.UnitY, Rotation) * delta.Y -
                        Vector3.Transform(Vector3.UnitX, Rotation) * delta.X) * scale;
            position += move;
            target += move;
        }
        else if (hovered && io.MouseWheel != 0)
        {
            var distance = Distance * MathF.Pow(0.85f, io.MouseWheel);
            position = target - Forward * Math.Max(distance, MinDistance);
        }
    }

    public void Apply(LookAtCamera camera, int width, int height)
    {
        // A near plane that follows the distance keeps depth precision over the size of a whole system.
        camera.ZRange = new Vector2(Math.Clamp(Distance * 0.001f, 1, 1000), 10_000_000);
        camera.Update(width, height, position, position + Forward, Rotation);
    }

    private void Fly(Keyboard keyboard, Vector2 delta, float wheel, bool boost, float elapsed)
    {
        var distance = Distance;
        Turn(delta);
        if (wheel != 0)
        {
            Speed = Math.Clamp(Speed * MathF.Pow(1.25f, wheel), 10, 1_000_000);
        }

        var move = Vector3.Zero;
        if (keyboard.IsKeyDown(Keys.W)) move -= Vector3.UnitZ;
        if (keyboard.IsKeyDown(Keys.S)) move += Vector3.UnitZ;
        if (keyboard.IsKeyDown(Keys.A)) move -= Vector3.UnitX;
        if (keyboard.IsKeyDown(Keys.D)) move += Vector3.UnitX;
        if (keyboard.IsKeyDown(Keys.E)) move += Vector3.UnitY;
        if (keyboard.IsKeyDown(Keys.Q)) move -= Vector3.UnitY;
        if (move != Vector3.Zero)
        {
            position += Vector3.Transform(Vector3.Normalize(move), Rotation) * Speed * (boost ? 4 : 1) * elapsed;
        }

        // The orbit point stays in front of the camera at the same distance.
        target = position + Forward * distance;
    }

    private void Turn(Vector2 delta)
    {
        yaw -= delta.X * MouseTurnRate;
        pitch = Math.Clamp(pitch - delta.Y * MouseTurnRate, -MaxPitch, MaxPitch);
    }
}
