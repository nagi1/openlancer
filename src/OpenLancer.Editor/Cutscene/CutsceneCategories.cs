using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Schema;
using LibreLancer.Data.Schema.GCS;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>The categories of the cutscene list, in the order of the list (decision D601 of the cutscene categories
/// design).</summary>
internal enum CutsceneCategory
{
    Intro,
    MainStory,
    Bases,
    BarConversations,
    Characters,
    RandomMissions,
    Mods,
    Other,
}

/// <summary>One row: a script with its text for the combo and the tab.</summary>
/// <param name="Path">The install path, in normal form.</param>
/// <param name="Name">The file name without the extension.</param>
/// <param name="Detail">The dim text after the name, for example the base that plays the script, or empty.</param>
/// <param name="SearchText">The other text that the filter matches. It has the install path and the bases and systems
/// that play the script. It also has the words of the category and the group (decision D618).</param>
/// <param name="Used">True when an INI line of the game data names the script.</param>
internal sealed record CutsceneRow(string Path, string Name, string Detail, string SearchText, bool Used);

/// <summary>A group of one category. A category without groups has one group with an empty label.</summary>
/// <param name="Id">The id of the group, unique in its category.</param>
/// <param name="Detail">The dim text after the label, for example "game opening", or empty.</param>
/// <param name="Tooltip">The text that shows on hover, or empty.</param>
internal sealed record CutsceneGroup(string Id, string Label, string Detail, string Tooltip, IReadOnlyList<CutsceneRow> Rows);

/// <summary>A category with its groups, in the order of the list.</summary>
internal sealed record CutsceneCategoryNode(CutsceneCategory Category, IReadOnlyList<CutsceneGroup> Groups)
{
    /// <summary>The number of scripts in the category.</summary>
    public int Count => Groups.Sum(g => g.Rows.Count);
}

/// <summary>The place of a script: its category and the id of its group.</summary>
internal readonly record struct CutscenePlace(CutsceneCategory Category, string GroupId);

/// <summary>A choice of the group menu of the Cutscenes tab: a category, or one group of a category.</summary>
/// <param name="GroupId">The id of the group, or null for all groups of the category.</param>
internal readonly record struct CutsceneChoice(CutsceneCategory Category, string? GroupId)
{
    /// <summary>True when a script with this place shows for the choice.</summary>
    public bool Keeps(CutscenePlace? place) =>
        place is { } p && p.Category == Category && (GroupId == null || p.GroupId == GroupId);
}

/// <summary>
/// The categories and groups of the cutscene scripts (section 4 of the cutscene categories design). The toolbar list of
/// the Cutscene tab and the group menu of the Cutscenes tab show them. Each script has one place, so the counts add up
/// to the number of scripts. The places come from the paths and from the loaded game data, never from a list of retail
/// file names.
/// </summary>
internal sealed class CutsceneCategories
{
    /// <summary>The detail of a new cutscene that is not saved (decision D613).</summary>
    public const string NewDetail = "new";

    /// <summary>The detail of a script that no INI line names (decision D613).</summary>
    public const string UnusedDetail = "not used";

    /// <summary>The detail of a story script that the game plays only in space.</summary>
    public const string SpaceDetail = "in space";

    /// <summary>The detail of the group of the first story mission (decision D605).</summary>
    public const string OpeningDetail = "game opening";

    private const string DataFolder = "DATA";
    private const string StoryFolder = @"DATA\SCRIPTS\STORY";
    private const string IntroFolder = @"DATA\SCRIPTS\INTRO";
    private const string BasesFolder = @"DATA\SCRIPTS\BASES";
    private const string GcsFolder = @"DATA\SCRIPTS\GCS";
    private const string ExtrasFolder = @"DATA\SCRIPTS\EXTRAS";
    private const string VendorsFolder = @"DATA\SCRIPTS\VENDORS";
    private const string RandomMissionsFolder = @"DATA\RANDOMMISSIONS";
    private const string ModsFolder = @"DATA\MODS";

    // The ids of the groups that do not come from the data.
    private const string NoMissionGroup = "no-mission";
    private const string ManyRegionsGroup = "many-regions";
    private const string NoRoomGroup = "no-room";
    private const string UnlistedGroup = "unlisted";
    private const string GenericFidgetsGroup = "extras";
    private const string VendorFidgetsGroup = "vendors";
    private const string OutsideDataGroup = "outside";

    /// <summary>
    /// The region prefixes of the system nicknames and their names, in the order of the groups (decision D606). The
    /// data has no region names, so the names follow the naming convention of the retail data.
    /// </summary>
    private static readonly (string Prefix, string Name)[] Regions =
    [
        ("LI", "Liberty"), ("BR", "Bretonia"), ("KU", "Kusari"), ("RH", "Rheinland"),
        ("BW", "Border Worlds"), ("EW", "Edge Worlds"), ("IW", "Independent Worlds"),
        ("HI", "Hispania"), ("ST", "Story systems"),
    ];

    private readonly Dictionary<string, (CutscenePlace Place, CutsceneRow Row)> byPath;

    private CutsceneCategories(IReadOnlyList<CutsceneCategoryNode> categories, string? openingGroup)
    {
        Categories = categories;
        OpeningGroup = openingGroup;
        byPath = new Dictionary<string, (CutscenePlace, CutsceneRow)>(VfsPath.Comparer);
        foreach (var category in categories)
        {
            foreach (var group in category.Groups)
            {
                foreach (var row in group.Rows)
                {
                    byPath[row.Path] = (new CutscenePlace(category.Category, group.Id), row);
                }
            }
        }
    }

    /// <summary>The categories that have scripts, in the order of the list.</summary>
    public IReadOnlyList<CutsceneCategoryNode> Categories { get; }

    /// <summary>The id of the group of the first story mission in Main story, or null when it has no scripts. It has
    /// the detail "game opening" (decision D605).</summary>
    public string? OpeningGroup { get; }

    /// <summary>The number of scripts.</summary>
    public int Count => byPath.Count;

    /// <summary>
    /// The categories of the scripts. Each path gets one place. The new unsaved cutscenes of the facts are rows too
    /// (decision D615).
    /// </summary>
    /// <param name="paths">The install paths of the scripts of the game data.</param>
    public static CutsceneCategories Build(IEnumerable<string> paths, CutsceneFacts facts) =>
        new Builder(facts).Build(paths);

    /// <summary>The place of a script, or null when the list does not have it. Case does not matter.</summary>
    public CutscenePlace? PlaceOf(string? path) =>
        Find(path) is { } found ? found.Place : null;

    /// <summary>The row of a script, or null when the list does not have it. Case does not matter.</summary>
    public CutsceneRow? RowOf(string? path) => Find(path)?.Row;

    /// <summary>The label of a choice of the group menu: the label of the category or of the group. Null when the list
    /// does not have that category or group, for example after the undo of the last new cutscene of a mod.</summary>
    public string? LabelOf(CutsceneChoice choice)
    {
        if (Categories.FirstOrDefault(c => c.Category == choice.Category) is not { } category)
        {
            return null;
        }

        return choice.GroupId == null
            ? Label(category.Category)
            : category.Groups.FirstOrDefault(g => g.Id == choice.GroupId)?.Label;
    }

    /// <summary>The label of the header of a category, in sentence case.</summary>
    public static string Label(CutsceneCategory category) => category switch
    {
        CutsceneCategory.Intro => "Intro scenes",
        CutsceneCategory.MainStory => "Main story",
        CutsceneCategory.Bases => "Bases and rooms",
        CutsceneCategory.BarConversations => "Bar conversations",
        CutsceneCategory.Characters => "Characters",
        CutsceneCategory.RandomMissions => "Random missions",
        CutsceneCategory.Mods => "Mod cutscenes",
        _ => "Other",
    };

    /// <summary>The dim text after the label of a category: "main menu" for the intro scenes, else empty.</summary>
    public static string Detail(CutsceneCategory category) => category == CutsceneCategory.Intro ? "main menu" : "";

    /// <summary>What a category holds, for the tooltip of its header and of its info icon.</summary>
    public static string Tooltip(CutsceneCategory category) => category switch
    {
        CutsceneCategory.Intro =>
            "The scenes behind the main menu. When the main menu opens, the game picks one intro base at random and " +
            "plays the scene scripts of its start room. The game opening is in Main story, Mission 1.",
        CutsceneCategory.MainStory =>
            "The scripts of the story missions, with one group for each mission in story order. A group holds the " +
            "scripts of the mission folder and the story scenes that the mission names. The rows follow the order of " +
            "the mission file. Mission 1 starts with the game opening. The scenes behind the main menu are in Intro " +
            "scenes.",
        CutsceneCategory.Bases =>
            "The room scripts of the bases: room sets, ambient scenes, and landing and launch scenes. A group is the " +
            "region of the bases that play the script.",
        CutsceneCategory.BarConversations =>
            "The generic bar conversations. For a character in a bar, the game picks a script from genericscripts.ini " +
            "by segment, gender and posture.",
        CutsceneCategory.Characters =>
            "The fidget scripts: the short idle movements of generic characters, story characters and vendors.",
        CutsceneCategory.RandomMissions => "The scripts of the random missions.",
        CutsceneCategory.Mods =>
            "The new cutscenes of the mods in DATA\\MODS, also the new cutscenes that are not saved. There is one group " +
            "for each mod.",
        _ => "The scripts that are not in another category. There is one group for each folder.",
    };

    private (CutscenePlace Place, CutsceneRow Row)? Find(string? path) =>
        path != null && VfsPath.Normalize(path) is { } normal && byPath.TryGetValue(normal, out var found)
            ? found
            : null;

    /// <summary>True when a path is in a folder: it starts with the folder and a backslash. Case does not
    /// matter.</summary>
    private static bool In(string path, string folder) =>
        path.Length > folder.Length + 1 && path[folder.Length] == '\\' &&
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>The region of a system: the letters of its nickname before the first digit, in upper case.</summary>
    internal static string RegionOf(string system)
    {
        var end = 0;
        while (end < system.Length && !char.IsAsciiDigit(system[end]))
        {
            end++;
        }

        return (end == 0 ? system : system[..end]).ToUpperInvariant();
    }

    /// <summary>The place of a region in the order of the groups: the index in the table, or after the table for an
    /// unknown prefix.</summary>
    private static int RegionRank(string region) =>
        Array.FindIndex(Regions, r => r.Prefix == region) is >= 0 and var index ? index : Regions.Length;

    /// <summary>The name of a region: the name in the table, or "<PREFIX> systems" for an unknown prefix.</summary>
    private static string RegionName(string region) =>
        RegionRank(region) is var rank && rank < Regions.Length ? Regions[rank].Name : $"{region} systems";

    /// <summary>The order of a row in its group. The rows of Main story follow the play order (decision D604). The
    /// other rows sort by name only.</summary>
    private readonly record struct RowOrder(int Part, int Tier, int Position, int Kind, int Scan);

    /// <summary>A group before its rows are complete.</summary>
    private sealed class GroupDraft(string id, string label, int rank)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public int Rank { get; } = rank;
        public string Detail { get; set; } = "";
        public string Tooltip { get; set; } = "";
        public List<(string Path, string Detail, RowOrder Order)> Rows { get; } = [];
    }

    /// <summary>A story mission with the id and the label of its group, and the RTC files that its mission file
    /// adds.</summary>
    private sealed record Part(StoryMissionFact Fact, string GroupId, string Label, Dictionary<string, int> RtcLines);

    /// <summary>The build steps of section 4.5.</summary>
    private sealed class Builder
    {
        private readonly CutsceneFacts facts;
        private readonly ThnReferences references;
        private readonly Dictionary<ThnReference, int> scanOrder = new(ReferenceEqualityComparer.Instance);
        private readonly Part[] parts;
        private readonly Dictionary<string, int?> partOfPath = new(VfsPath.Comparer);
        private readonly Dictionary<(CutsceneCategory, string), GroupDraft> groups = [];

        public Builder(CutsceneFacts facts)
        {
            this.facts = facts;
            references = facts.References;
            for (var i = 0; i < references.All.Count; i++)
            {
                scanOrder[references.All[i]] = i;
            }

            parts = facts.Missions.Select(mission =>
            {
                var number = MissionNumber(mission.Nickname);
                var rtc = new Dictionary<string, int>(VfsPath.Comparer);
                foreach (var line in references.RtcLines)
                {
                    if (VfsPath.Comparer.Equals(line.File, mission.File) && ThnReferences.PathOf(line.Value) is { } file)
                    {
                        rtc.TryAdd(file, line.SectionIndex);
                    }
                }

                return number is { } n
                    ? new Part(mission, "mission:" + n.ToString(CultureInfo.InvariantCulture),
                        "Mission " + n.ToString(CultureInfo.InvariantCulture), rtc)
                    : new Part(mission, "mission:" + mission.Nickname.ToUpperInvariant(), mission.Nickname, rtc);
            }).ToArray();
        }

        public CutsceneCategories Build(IEnumerable<string> paths)
        {
            var all = new List<string>();
            var seen = new HashSet<string>(VfsPath.Comparer);
            foreach (var path in paths.Concat(facts.Unsaved).Select(p => VfsPath.Normalize(p) ?? p))
            {
                if (seen.Add(path))
                {
                    all.Add(path);
                }
            }

            foreach (var path in all)
            {
                Add(path);
            }

            var categories = new List<CutsceneCategoryNode>();
            foreach (var category in Enum.GetValues<CutsceneCategory>())
            {
                var drafts = groups.Where(g => g.Key.Item1 == category).Select(g => g.Value)
                    .OrderBy(g => g.Rank).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (drafts.Count == 0)
                {
                    continue;
                }

                categories.Add(new CutsceneCategoryNode(category, drafts.Select(d => Group(category, d)).ToList()));
            }

            var opening = parts.Length > 0 && groups.ContainsKey((CutsceneCategory.MainStory, parts[0].GroupId))
                ? parts[0].GroupId
                : null;
            return new CutsceneCategories(categories, opening);
        }

        /// <summary>Puts one script in its category and its group (steps 1 and 2).</summary>
        private void Add(string path)
        {
            var category = CategoryOf(path);
            var used = references.UsedBy(path).Count > 0;
            var (group, detail, order) = category switch
            {
                CutsceneCategory.MainStory => Story(path),
                CutsceneCategory.Bases => Region(path),
                CutsceneCategory.BarConversations => Conversation(path),
                CutsceneCategory.Characters => In(path, ExtrasFolder)
                    ? (Draft(category, GenericFidgetsGroup, "Generic fidgets", 0), "", default(RowOrder))
                    : (Draft(category, VendorFidgetsGroup, "Vendor fidgets", 1), "", default),
                CutsceneCategory.Mods => Mod(path),
                CutsceneCategory.Other => Folder(path),
                _ => (Draft(category, "", "", 0), "", default),
            };

            // Step 3: the first rule of the detail that applies.
            if (facts.Unsaved.Contains(path))
            {
                detail = NewDetail;
            }
            else if (!used)
            {
                detail = UnusedDetail;
            }

            group.Rows.Add((path, detail, order));
        }

        /// <summary>The category of a path (decision D602). The first rule that applies wins.</summary>
        private CutsceneCategory CategoryOf(string path)
        {
            if (In(path, ModsFolder) && path.IndexOf('\\', ModsFolder.Length + 1) > 0)
            {
                return CutsceneCategory.Mods;
            }

            if (In(path, StoryFolder) || PartOf(path) != null)
            {
                return CutsceneCategory.MainStory;
            }

            if (In(path, IntroFolder) || facts.Intro.Contains(path))
            {
                return CutsceneCategory.Intro;
            }

            if (In(path, BasesFolder))
            {
                return CutsceneCategory.Bases;
            }

            if (In(path, GcsFolder))
            {
                return CutsceneCategory.BarConversations;
            }

            if (In(path, ExtrasFolder) || In(path, VendorsFolder))
            {
                return CutsceneCategory.Characters;
            }

            return In(path, RandomMissionsFolder) ? CutsceneCategory.RandomMissions : CutsceneCategory.Other;
        }

        /// <summary>The index of the first story mission whose folder holds a path, or null.</summary>
        private int? PartOf(string path)
        {
            if (!partOfPath.TryGetValue(path, out var part))
            {
                var index = Array.FindIndex(parts, p => In(path, p.Fact.Folder));
                partOfPath[path] = part = index >= 0 ? index : null;
            }

            return part;
        }

        /// <summary>
        /// The mission group of a Main story script (decision D603). It is the first story mission whose folder holds an
        /// INI file that names the script. Else it is the mission of the folder of the script. A story scene that no
        /// such file names goes to "Not used by a mission".
        /// </summary>
        private (GroupDraft, string, RowOrder) Story(string path)
        {
            var uses = references.UsedBy(path);
            int? part = null;
            foreach (var use in uses)
            {
                if (PartOf(use.File) is { } index && (part == null || index < part))
                {
                    part = index;
                }
            }

            part ??= PartOf(path);
            var detail = references.Rooms(path).FirstOrDefault(r => r.Role is ThnRoomRole.Encounter or ThnRoomRole.Ambient)
                is { } room
                ? BaseName(room.Base)
                : references.OnlyInSpace(path) ? SpaceDetail : "";
            if (part is not { } p)
            {
                var none = Draft(CutsceneCategory.MainStory, NoMissionGroup, "Not used by a mission", int.MaxValue);
                none.Tooltip = "No INI file of a story mission folder names these scripts.";
                return (none, detail, default);
            }

            var mission = parts[p];
            var group = Draft(CutsceneCategory.MainStory, mission.GroupId, mission.Label,
                Array.FindIndex(parts, m => m.GroupId == mission.GroupId));
            group.Tooltip = "Story missions:\n" + string.Join("\n", parts.Where(m => m.GroupId == mission.GroupId)
                .Select(m => $"{m.Fact.Nickname}: {m.Fact.File}"));
            group.Detail = mission.GroupId == parts[0].GroupId ? OpeningDetail : "";
            return (group, detail, PlayOrder(p, uses));
        }

        /// <summary>
        /// The order of a script in the rows of one story mission (decision D604). First come the scripts that the
        /// mission file names and the scripts of the RTC files that it adds, by the section of the line. Then come the
        /// other scripts that a file of the folder names, in scan order. Then come the scripts without such a line, by
        /// name.
        /// </summary>
        private RowOrder PlayOrder(int part, IReadOnlyList<ThnReference> uses)
        {
            var mission = parts[part].Fact;
            (int Position, int Kind, int Scan)? best = null;
            foreach (var use in uses)
            {
                var scan = scanOrder[use];
                (int, int, int)? candidate = VfsPath.Comparer.Equals(use.File, mission.File) ? (use.SectionIndex, 0, scan)
                    : parts[part].RtcLines.TryGetValue(use.File, out var line) ? (line, 1, scan)
                    : null;
                if (candidate is { } c && (best == null || c.CompareTo(best.Value) < 0))
                {
                    best = c;
                }
            }

            if (best is { } b)
            {
                return new RowOrder(part, 0, b.Position, b.Kind, b.Scan);
            }

            var inFolder = uses.FirstOrDefault(u => In(u.File, mission.Folder));
            return inFolder != null ? new RowOrder(part, 1, 0, 0, scanOrder[inFolder]) : new RowOrder(part, 2, 0, 0, 0);
        }

        /// <summary>
        /// The region group of a room script (decisions D606 and D607): the region of the bases that play it, by the
        /// most bases. A tie goes to the region whose prefix starts the file name, else to the first region of the
        /// table. Three or more regions, or no room, give their own groups.
        /// </summary>
        private (GroupDraft, string, RowOrder) Region(string path)
        {
            var bases = Bases(path).Where(b => b.Fact != null).ToList();
            if (bases.Count == 0)
            {
                var none = Draft(CutsceneCategory.Bases, NoRoomGroup, "Not used by a room", int.MaxValue);
                none.Tooltip = "No room of the game data plays these scripts.";
                return (none, "", default);
            }

            var detail = bases.Count == 1 ? bases[0].Fact!.Name : $"{bases[0].Fact!.Name} +{bases.Count - 1}";
            var regions = bases.CountBy(b => RegionOf(b.Fact!.System)).ToList();
            if (regions.Count >= 3)
            {
                var many = Draft(CutsceneCategory.Bases, ManyRegionsGroup, "Three or more regions", int.MaxValue - 1);
                many.Tooltip = "Rooms in three or more regions play these scripts.";
                return (many, detail, default);
            }

            var most = regions.Max(r => r.Value);
            var tied = regions.Where(r => r.Value == most).Select(r => r.Key)
                .OrderBy(RegionRank).ThenBy(r => r, StringComparer.Ordinal).ToList();
            var name = System.IO.Path.GetFileName(path);
            var region = tied.FirstOrDefault(r => name.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ?? tied[0];
            var group = Draft(CutsceneCategory.Bases, "region:" + region, RegionName(region), RegionRank(region));
            if (group.Tooltip.Length == 0)
            {
                group.Tooltip = "Systems: " + string.Join(", ", facts.Bases.Values
                    .Where(b => RegionOf(b.System) == region)
                    .DistinctBy(b => b.System, StringComparer.OrdinalIgnoreCase)
                    .Select(b => b.SystemName)
                    .Order(StringComparer.OrdinalIgnoreCase));
            }

            return (group, detail, default);
        }

        /// <summary>The group of a bar conversation by the gender and the posture of <c>genericscripts.ini</c>
        /// (decision D608). The detail is the first segment, with "+N" for more segments.</summary>
        private (GroupDraft, string, RowOrder) Conversation(string path)
        {
            if (!facts.Conversations.TryGetValue(path, out var conversation))
            {
                var none = Draft(CutsceneCategory.BarConversations, UnlistedGroup, "Not in the conversation list",
                    int.MaxValue);
                none.Tooltip = "genericscripts.ini does not list these scripts.";
                return (none, "", default);
            }

            var (gender, genderRank) = conversation.Gender switch
            {
                FLGender.male => ("Male", 0),
                FLGender.female => ("Female", 1),
                var other => (other.ToString(), 2),
            };
            var posture = conversation.Posture switch
            {
                Posture.Stand => "standing",
                Posture.SitLow => "sitting",
                var other => other.ToString(),
            };
            var segments = conversation.Segments;
            var detail = segments.Count switch
            {
                0 => "",
                1 => segments[0],
                _ => $"{segments[0]} +{segments.Count - 1}",
            };
            var group = Draft(CutsceneCategory.BarConversations,
                $"{conversation.Gender}/{conversation.Posture}".ToLowerInvariant(), $"{gender}, {posture}",
                genderRank * 100 + (int)conversation.Posture);
            return (group, detail, default);
        }

        /// <summary>The group of a new cutscene of a mod: one group for each mod, with the name of the mod.</summary>
        private (GroupDraft, string, RowOrder) Mod(string path)
        {
            var start = ModsFolder.Length + 1;
            var id = path[start..path.IndexOf('\\', start)];
            return (Draft(CutsceneCategory.Mods, id.ToUpperInvariant(), facts.ModName(id), 0), "", default);
        }

        /// <summary>The group of a script of Other: the first folder below <c>DATA</c>, or "Outside DATA".</summary>
        private (GroupDraft, string, RowOrder) Folder(string path)
        {
            var names = path.Split('\\');
            if (names.Length < 2 || !names[0].Equals(DataFolder, StringComparison.OrdinalIgnoreCase))
            {
                return (Draft(CutsceneCategory.Other, OutsideDataGroup, "Outside DATA", 1), "", default);
            }

            // A script right in DATA has no folder below it, so its group is DATA.
            var folder = names.Length > 2 ? names[1] : names[0];
            return (Draft(CutsceneCategory.Other, "folder:" + folder.ToUpperInvariant(), folder, 0), "", default);
        }

        /// <summary>The bases that play a script, each once, in scan order, with their facts or null for a base that
        /// is not in the data.</summary>
        private List<(string Nickname, BaseFact? Fact)> Bases(string path) =>
            references.Rooms(path).Select(r => r.Base).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(b => (b, facts.Bases.GetValueOrDefault(b))).ToList();

        private string BaseName(string nickname) => facts.Bases.GetValueOrDefault(nickname)?.Name ?? nickname;

        /// <summary>The group of a category and an id. The first call makes it.</summary>
        private GroupDraft Draft(CutsceneCategory category, string id, string label, int rank)
        {
            if (!groups.TryGetValue((category, id), out var group))
            {
                groups[(category, id)] = group = new GroupDraft(id, label, rank);
            }

            return group;
        }

        /// <summary>The rows of a group in their order (step 4), with their search text.</summary>
        private CutsceneGroup Group(CutsceneCategory category, GroupDraft draft)
        {
            var rows = draft.Rows
                .OrderBy(r => r.Order.Part).ThenBy(r => r.Order.Tier).ThenBy(r => r.Order.Position)
                .ThenBy(r => r.Order.Kind).ThenBy(r => r.Order.Scan)
                .ThenBy(r => System.IO.Path.GetFileNameWithoutExtension(r.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .Select(r => new CutsceneRow(r.Path, System.IO.Path.GetFileNameWithoutExtension(r.Path), r.Detail,
                    SearchText(r.Path, category, draft), references.UsedBy(r.Path).Count > 0))
                .ToList();
            return new CutsceneGroup(draft.Id, draft.Label, draft.Detail, draft.Tooltip, rows);
        }

        /// <summary>The search text of a row. It has the install path, and the names, the nicknames and the systems of
        /// the bases that play the script. It also has the labels and the details of the category and the group. So
        /// "menu" finds the intro scenes, because their header shows "main menu".</summary>
        private string SearchText(string path, CutsceneCategory category, GroupDraft group)
        {
            var words = new List<string> { path };
            foreach (var (nickname, fact) in Bases(path))
            {
                words.Add(nickname);
                if (fact != null)
                {
                    words.Add(fact.Name);
                    words.Add(fact.SystemName);
                }
            }

            words.Add(Label(category));
            words.Add(Detail(category));
            words.Add(group.Label);
            words.Add(group.Detail);
            return string.Join("\n", words.Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>The number in a story mission nickname, for example 7 for <c>Story_07a</c>, or null.</summary>
        private static int? MissionNumber(string nickname)
        {
            var start = 0;
            while (start < nickname.Length && !char.IsAsciiDigit(nickname[start]))
            {
                start++;
            }

            var end = start;
            while (end < nickname.Length && char.IsAsciiDigit(nickname[end]))
            {
                end++;
            }

            return end > start && int.TryParse(nickname.AsSpan(start, end - start), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        }
    }
}
