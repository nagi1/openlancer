using System.IO;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// The folder of the files that a play writes (decision D41): <c>&lt;profile folder&gt;\&lt;profile name&gt;.play\</c>, next
/// to the deploy folder of the profile. It holds the game config <c>lancer.ini</c> and the save folder
/// <c>saves\</c>.
/// </summary>
public static class PlayFolder
{
    private const string Suffix = ".play";
    private const string ConfigName = "lancer.ini";
    private const string SavesName = "saves";

    /// <param name="profilePath">The path of the profile file.</param>
    public static string Of(string profilePath)
    {
        var full = Path.GetFullPath(profilePath);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + Suffix);
    }

    /// <summary>The game config of the play. The in-game options write it.</summary>
    public static string ConfigPath(string profilePath) => Path.Combine(Of(profilePath), ConfigName);

    /// <summary>The save folder of the game server: the saves of the player, <c>AutoSave.fl</c> and
    /// <c>scripts\</c>.</summary>
    public static string SavesPath(string profilePath) => Path.Combine(Of(profilePath), SavesName);

    /// <summary>
    /// Makes the play folder and its save folder when they do not exist. Call it before the game config and the
    /// server use them, because neither makes its folder, and a write error on the server thread ends the process.
    /// </summary>
    public static void Create(string profilePath) => Directory.CreateDirectory(SavesPath(profilePath));
}
