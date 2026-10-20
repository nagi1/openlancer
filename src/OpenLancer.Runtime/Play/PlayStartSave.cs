using System;
using System.IO;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.Schema.Save;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// The start save of a mission play (section 5.5 of the M6c design): a plain-text save that the editor writes. The
/// OpenLancer game reads four parts of it: <c>[StoryInfo]</c>, <c>[MissionState]</c>, <c>[TriggerSave]</c> and the
/// place lines of <c>[Player]</c>. It keeps its multiplayer new character and the ship package of the play.
/// </summary>
/// <param name="Save">The save of the file. Its schema reads only the keys that it knows.</param>
/// <param name="Place">The place of the file: docked at a base, or in space at a point with the heading of
/// <c>rotate</c>. After the spawn, <see cref="PlaySpawn.Orient"/> turns the ship to this heading.</param>
public sealed record PlayStartSave(SaveGame Save, PlayStart Place)
{
    /// <summary>Reads a start save.</summary>
    /// <exception cref="InvalidDataException">The file is not a start save. The message gives the reason.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    public static PlayStartSave Read(string path)
    {
        SaveGame save;
        try
        {
            save = SaveGame.FromString(path, File.ReadAllText(path));
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"The start save {path} is not a save: {e.Message}", e);
        }

        var player = save.Player ?? throw new InvalidDataException($"The start save {path} has no [Player] section.");
        if (save.StoryInfo?.Mission is not { Length: > 0 })
            throw new InvalidDataException($"The start save {path} names no mission in [StoryInfo].");
        if (player.Base is { Length: > 0 } dock)
            return new PlayStartSave(save, new PlayStart.AtBase(dock));
        if (player.System is not { Length: > 0 } system)
            throw new InvalidDataException($"The start save {path} names no system and no base.");
        var turn = MathHelper.MatrixFromEulerDegrees(player.Rotate).ExtractRotation();
        return new PlayStartSave(save, new PlayStart.InSpace(system, player.Position, Quaternion.Normalize(turn)));
    }

    /// <summary>Reads a start save, or gives the reason why it cannot.</summary>
    public static PlayStartSave? TryRead(string path, out string? problem)
    {
        try
        {
            problem = null;
            return Read(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problem = e is InvalidDataException ? e.Message : $"The start save {path} cannot be read: {e.Message}";
            return null;
        }
    }
}
