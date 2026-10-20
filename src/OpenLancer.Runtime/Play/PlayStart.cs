using System.Numerics;

namespace OpenLancer.Runtime.Play;

/// <summary>Where the player starts a play: in space (Play from camera), docked at a base (Play at base), or at the start
/// of a mission (a mission play from a start save).</summary>
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

    /// <summary>A start of a mission play (section 5.6 of the M6c design): the start save gives the place and the
    /// mission part, and the test layer holds the setup triggers of the start.</summary>
    /// <param name="Save">The full path of the start save.</param>
    /// <param name="TestLayer">The full path of the folder of the test layer, or null. The play loads it after the mods
    /// of the play.</param>
    public sealed record FromSave(string Save, string? TestLayer) : PlayStart;
}
