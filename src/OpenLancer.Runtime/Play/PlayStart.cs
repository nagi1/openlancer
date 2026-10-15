using System.Numerics;

namespace OpenLancer.Runtime.Play;

/// <summary>Where the player starts a play: in space (Play from camera) or docked at a base (Play at base).</summary>
public abstract record PlayStart
{
    private PlayStart()
    {
    }

    /// <summary>A start in space at a point, with a heading.</summary>
    /// <param name="System">The nickname of the system.</param>
    /// <param name="Orientation">The orientation of the ship. The ship flies along its -Z axis.</param>
    public sealed record InSpace(string System, Vector3 Position, Quaternion Orientation) : PlayStart;

    /// <summary>A start docked at a base.</summary>
    /// <param name="Base">The nickname of the base.</param>
    public sealed record AtBase(string Base) : PlayStart;
}
