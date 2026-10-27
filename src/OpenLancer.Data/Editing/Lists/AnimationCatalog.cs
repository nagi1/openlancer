using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.IO;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>A clip and the animation archive that contains it.</summary>
public sealed record AnimationClip(string Name, string Archive);

/// <summary>The animation names in the active data view.</summary>
public static class AnimationCatalog
{
    /// <summary>Reads the animations that the game loads, in load order. A later file replaces a duplicate name.</summary>
    public static IReadOnlyList<AnimationClip> Read(FileSystem files, IEnumerable<string> archives)
    {
        var clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in archives)
        {
            using var stream = files.Open(path);
            foreach (var name in AnimationNames.Scripts(stream, path))
                clips[name] = new AnimationClip(name, path);
        }
        return clips.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Returns true when a clip matches the chosen character, part and search text.</summary>
    public static bool Matches(AnimationClip clip, string character, string part, string search)
    {
        var archive = Path.GetFileNameWithoutExtension(clip.Archive.Replace('\\', '/')).ToLowerInvariant();
        var female = archive.Contains("female") || archive.Contains("juni");
        var male = !female && (archive.Contains("male") || archive.Contains("trent"));
        var fitsCharacter = character switch
        {
            "Juni" => !male && !archive.Contains("trent"),
            "Trent" => !female && !archive.Contains("juni"),
            "Orillion" or "King" => !female && !archive.Contains("trent"),
            "Female" => !male && !archive.Contains("trent"),
            "Male" => !female && !archive.Contains("juni"),
            _ => true,
        };
        var kind = Part(clip);
        return fitsCharacter && (part == "All" || part == kind) &&
               (search.Length == 0 || clip.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                archive.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                Label(clip.Name).Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gives the body part named by the archive.</summary>
    public static string Part(AnimationClip clip)
    {
        var archive = Path.GetFileNameWithoutExtension(clip.Archive.Replace('\\', '/'));
        if (archive.StartsWith("body", StringComparison.OrdinalIgnoreCase)) return "Body";
        if (archive.StartsWith("facial", StringComparison.OrdinalIgnoreCase)) return "Head";
        if (archive.StartsWith("hand", StringComparison.OrdinalIgnoreCase)) return "Hands";
        return "Other";
    }

    /// <summary>Gives a short label from the words in a clip name.</summary>
    public static string Label(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("WALK") || n.Contains("WLKG")) return "Walk";
        if (n.Contains("SHRG")) return "Shrug";
        if (n.Contains("NOD")) return "Nod";
        if (n.Contains("SHAKHEAD")) return "Shake head";
        if (n.Contains("LSTN")) return "Listen";
        if (n.Contains("WALLA") || n.Contains("TALK")) return "Talk";
        if (n.Contains("GEST")) return "Gesture";
        if (n.Contains("SIT")) return "Sit";
        if (n.Contains("TURN")) return "Turn";
        if (n.Contains("IDLE")) return "Idle";
        return "Animation";
    }
}
