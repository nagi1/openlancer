using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.Schema;
using LibreLancer.Data.Schema.GCS;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Studio.Commands;

/// <summary>A mission of the storyline (section 4.2 of the cutscene categories design).</summary>
/// <param name="Nickname">The nickname of the story mission, for example <c>Story_07a</c>.</param>
/// <param name="File">The install path of the mission file, in normal form.</param>
/// <param name="Folder">The install path of the folder of the mission file, in normal form.</param>
internal sealed record StoryMissionFact(string Nickname, string File, string Folder);

/// <summary>A base of the game data.</summary>
/// <param name="Name">The name of the base, or its nickname when it has no name.</param>
/// <param name="System">The nickname of the system of the base.</param>
/// <param name="SystemName">The name of the system, or its nickname when it has no name.</param>
internal sealed record BaseFact(string Name, string System, string SystemName);

/// <summary>How <c>genericscripts.ini</c> lists a bar conversation.</summary>
/// <param name="Gender">The gender of the first key that lists the script.</param>
/// <param name="Posture">The posture of the first key that lists the script.</param>
/// <param name="Segments">Each segment that lists the script, once, in the order of the file.</param>
internal sealed record ConversationFact(FLGender Gender, Posture Posture, IReadOnlyList<string> Segments);

/// <summary>
/// The facts of the loaded game data that put each cutscene script in a category and a group (section 4.2 of the
/// cutscene categories design). <see cref="CutsceneCategories.Build"/> reads only these facts and the paths, so a test
/// can give synthetic facts.
/// </summary>
/// <param name="References">The INI lines that name each script, and the rooms that play it.</param>
/// <param name="Missions">The missions of the storyline, in story order.</param>
/// <param name="Bases">The bases, by nickname, without case.</param>
/// <param name="Conversations">The bar conversations of <c>genericscripts.ini</c>, by install path.</param>
/// <param name="Intro">The install paths of the scripts of the intro scenes of the main menu.</param>
/// <param name="Unsaved">The install paths of the new cutscenes of the session that the game data does not have
/// yet.</param>
/// <param name="ModName">Gives the name of a mod from its id, or the id.</param>
internal sealed record CutsceneFacts(
    ThnReferences References,
    IReadOnlyList<StoryMissionFact> Missions,
    IReadOnlyDictionary<string, BaseFact> Bases,
    IReadOnlyDictionary<string, ConversationFact> Conversations,
    IReadOnlySet<string> Intro,
    IReadOnlySet<string> Unsaved,
    Func<string, string> ModName)
{
    /// <summary>The facts of the loaded game data.</summary>
    /// <param name="items">The loaded game data.</param>
    /// <param name="references">The references of the INI files of the same data.</param>
    /// <param name="text">Gives the text of a string resource, or an empty string or null when it has no text.</param>
    /// <param name="modName">Gives the name of a mod from its id, or the id.</param>
    /// <param name="unsaved">The install paths of the new cutscenes of the session that the data does not have.</param>
    public static CutsceneFacts From(GameItemDb items, ThnReferences references, Func<int, string> text,
        Func<string, string> modName, IEnumerable<string> unsaved)
    {
        string NameOf(int ids, string nickname) => text(ids) is { } name && !string.IsNullOrWhiteSpace(name)
            ? name
            : nickname;

        var missions = new List<StoryMissionFact>();
        foreach (var mission in items.Ini.Storyline.Missions)
        {
            if (ThnReferences.PathOf(mission.File) is { } file)
            {
                missions.Add(new StoryMissionFact(mission.Nickname, file, VfsPath.Parent(file)));
            }
        }

        // A base without a system has no region, so it counts as a base that is not in the data.
        var bases = new Dictionary<string, BaseFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Bases)
        {
            if (item.System is not { Length: > 0 } system)
            {
                continue;
            }

            var systemName = items.Systems.Get(system) is { } found ? NameOf(found.IdsName, found.Nickname) : system;
            bases[item.Nickname] = new BaseFact(NameOf(item.IdsName, item.Nickname), system, systemName);
        }

        var conversations = new Dictionary<string, (FLGender Gender, Posture Posture, List<string> Segments)>(
            VfsPath.Comparer);
        foreach (var (key, scripts) in items.Ini.GenericScripts.Scripts)
        {
            foreach (var path in scripts.Select(ThnReferences.PathOf).OfType<string>())
            {
                if (!conversations.TryGetValue(path, out var conversation))
                {
                    conversations[path] = conversation = (key.Gender, key.Posture, []);
                }

                if (!conversation.Segments.Contains(key.Segment, StringComparer.OrdinalIgnoreCase))
                {
                    conversation.Segments.Add(key.Segment);
                }
            }
        }

        var intro = items.IntroScenes.SelectMany(s => s.Scripts)
            .Select(s => s.DataPath == null ? null : VfsPath.Normalize(s.DataPath))
            .OfType<string>()
            .ToHashSet(VfsPath.Comparer);
        return new CutsceneFacts(references, missions, bases,
            conversations.ToDictionary(c => c.Key, c => new ConversationFact(c.Value.Gender, c.Value.Posture,
                c.Value.Segments), VfsPath.Comparer),
            intro, unsaved.Select(p => VfsPath.Normalize(p) ?? p).ToHashSet(VfsPath.Comparer), modName);
    }
}
