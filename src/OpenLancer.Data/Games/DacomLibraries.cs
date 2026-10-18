using System;
using System.Collections.Generic;
using System.IO;

namespace OpenLancer.Data.Games;

/// <summary>
/// Reads the <c>[Libraries]</c> section of <c>EXE\dacom.ini</c>: the DLLs that the game loads at start. The lines have no
/// '=', so the INI parser cannot read them. Mods add their DLLs here, for example JFLP and the plugins of HD Edition.
/// </summary>
internal static class DacomLibraries
{
    /// <summary>The 14 lines of the retail game, as names without ".dll" in lowercase.</summary>
    public static readonly IReadOnlySet<string> Retail = new HashSet<string>(StringComparer.Ordinal)
    {
        "readfile", "x86math", "engbase", "system", "rp8", "soundstreamer", "soundmanager", "deformable2", "thorn",
        "shading", "rendcomp", "alchemy", "ximage", "flmaterials",
    };

    /// <summary>The name of JFLP, without ".dll".</summary>
    public const string Jflp = "jflp";

    /// <summary>
    /// The DLL names of the active lines, in file order, as the file writes them but without a leading ".\". A line that
    /// starts with ';' is off, and a line that starts with '@' includes another file. The first word of a line is the
    /// name: arguments follow it, and a ';' or a tab starts a comment.
    /// </summary>
    public static IReadOnlyList<string> Read(TextReader reader)
    {
        var names = new List<string>();
        var inSection = false;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.StartsWith("[Libraries]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection || line.Length == 0 || line[0] is ';' or '@')
                continue;
            var end = line.IndexOfAny([';', '\t']);
            if (end >= 0)
                line = line[..end];
            var word = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : "";
            if (word.StartsWith(@".\", StringComparison.Ordinal) || word.StartsWith("./", StringComparison.Ordinal))
                word = word[2..];
            if (word.Length > 0)
                names.Add(word);
        }
        return names;
    }

    /// <summary>The name to compare: lowercase, without ".dll".</summary>
    public static string Key(string name)
    {
        var key = name.ToLowerInvariant();
        return key.EndsWith(".dll", StringComparison.Ordinal) ? key[..^4] : key;
    }
}
