using System.IO;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The nickname and the zone of a scene item, for the readers that show any item. These are the Inspector header, the
/// labels, Copy nickname, Copy position, the status bar and the Hierarchy. A nebula and an asteroid field have no nickname
/// of their own. They show the nickname of their zone, which is the key of their section (decision D151 of the M22 design).
/// </summary>
internal static class SceneItems
{
    /// <summary>The nickname of an item with a nickname, or the zone nickname of a nebula or a field. Null for another
    /// item and for a nebula or a field without a zone.</summary>
    public static string? Nickname(object? item) => item switch
    {
        NicknameItem named => named.Nickname,
        _ => ZoneOf(item)?.Nickname,
    };

    /// <summary>The zone of a nebula, a field or a zone, or null for another item.</summary>
    public static Zone? ZoneOf(object? item) => item switch
    {
        Zone zone => zone,
        Nebula nebula => nebula.Zone,
        AsteroidField field => field.Zone,
        _ => null,
    };

    /// <summary>The file of a nebula or a field, or null for another item.</summary>
    public static string? FileOf(object? item) => item switch
    {
        Nebula nebula => nebula.SourceFile,
        AsteroidField field => field.SourceFile,
        _ => null,
    };

    /// <summary>
    /// The text that names an item without a name, or null when the item has neither a file nor a nickname. A nebula or
    /// a field gives its file name without the extension, for example <c>Li01_Badlands_Asteroids</c>. Another item gives
    /// its nickname.
    /// </summary>
    public static string? FallbackName(object? item) =>
        FileOf(item) is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file.Replace('\\', '/')) : Nickname(item);
}
