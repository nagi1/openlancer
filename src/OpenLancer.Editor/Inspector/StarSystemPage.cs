using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a star system: identity, its place in the universe, the environment and music, the counts of
/// its contents, and the advanced values. A count shows its items in the Hierarchy.
/// </summary>
internal sealed class StarSystemPage(EditorWindow window, PropertyGrid grid)
{
    // The "t:" words of the Hierarchy search that the Contents section counts, in the order of the section.
    private static readonly (string Label, string Word)[] ContentTypes =
    [
        ("Suns", "sun"), ("Planets", "planet"), ("Stations", "station"), ("Docking rings", "ring"),
        ("Gates", "gate"), ("Jump holes", "hole"), ("Trade lane rings", "lane"), ("Satellites", "satellite"),
        ("Weapons platforms", "platform"), ("Zones", "zone"), ("Asteroid fields", "field"), ("Nebulae", "nebula"),
        ("Lights", "light"),
    ];

    // The counts of the last system. They are counted again only for a different system.
    private StarSystem? countedSystem;
    private Dictionary<string, int> counts = [];

    /// <summary>Forgets the counted system. Call it when the profile closes.</summary>
    public void Reset()
    {
        countedSystem = null;
        counts = [];
    }

    public void Draw(StarSystem selected)
    {
        // The page always shows the loaded system, not the copy that the Scene shows.
        var system = window.Session?.GameData?.Items.Systems.Get(selected.Nickname) ?? selected;
        Identity(system);
        Universe(system);
        Environment(system);
        Contents(system);
        Advanced(system);
    }

    private void Identity(StarSystem system)
    {
        if (!grid.BeginSection("StarSystem.Identity", EditorIcons.Tag, "Identity"))
        {
            return;
        }

        grid.Text("Nickname", system.Nickname, "nickname");
        grid.Ids("Name", system.IdsName, "strid_name");
        grid.Info("Info", system.IdsInfo, "ids_info");
        grid.SetNextNote("The system file, from the [System] section of universe.ini.");
        grid.Text("File", system.SourceFile, "file");
        grid.Text("Message id prefix", system.MsgIdPrefix, "msg_id_prefix");
        grid.EndSection();
    }

    private void Universe(StarSystem system)
    {
        if (!grid.BeginSection("StarSystem.Universe", EditorIcons.Globe, "Universe"))
        {
            return;
        }

        grid.Text("Position", string.Create(CultureInfo.InvariantCulture,
            $"{system.UniversePosition.X:0.###}, {system.UniversePosition.Y:0.###}"), "pos");
        grid.Flags("Visit", system.Visit, "visit");
        var faction = system.LocalFaction;
        grid.Text("Local faction", faction == null ? null
            : window.Session?.Name(faction.IdsName) is { Length: > 0 } name ? $"{faction.Nickname} ({name})"
            : faction.Nickname, "local_faction");
        grid.Number("Navmap scale", system.NavMapScale, iniKey: "NavMapScale");
        grid.EndSection();
    }

    private void Environment(StarSystem system)
    {
        if (!grid.BeginSection("StarSystem.Environment", EditorIcons.Cloud, "Environment and music"))
        {
            return;
        }

        grid.Color("Background color", system.BackgroundColor, "space_color");
        grid.Color("Ambient color", system.AmbientColor.ToColor4(), "ambient_color");
        grid.Text("Spacedust", system.Spacedust, "spacedust");
        grid.Number("Spacedust particles", system.SpacedustMaxParticles, iniKey: "spacedust_maxparticles");
        grid.Number("Far clip", system.FarClip, iniKey: "space_farclip");
        grid.Text("Space music", system.MusicSpace, "space");
        grid.Text("Danger music", system.MusicDanger, "danger");
        grid.Text("Battle music", system.MusicBattle, "battle");
        grid.EndSection();
    }

    /// <summary>
    /// The number of items of each type. A click on a count shows the items in the Hierarchy. It opens the system
    /// first when the system is not open.
    /// </summary>
    private void Contents(StarSystem system)
    {
        if (!grid.BeginSection("StarSystem.Contents", EditorIcons.List, "Contents"))
        {
            return;
        }

        if (!ReferenceEquals(system, countedSystem))
        {
            counts = HierarchyModel.TypeCounts(system);
            countedSystem = system;
        }

        var isOpen = ReferenceEquals(window.Scene.Index?.Source, system);
        foreach (var (label, word) in ContentTypes)
        {
            var count = counts.GetValueOrDefault(word);
            if (count == 0)
            {
                continue;
            }

            var tooltip = isOpen
                ? $"Show them in the Hierarchy (t:{word})."
                : $"Open the system and show them in the Hierarchy (t:{word}).";
            if (grid.Link(label, count.ToString("N0", CultureInfo.InvariantCulture), tooltip))
            {
                if (!isOpen)
                {
                    window.OpenSystem(system);
                    window.Selection = system;
                }

                window.ShowInHierarchy($"t:{word}");
            }
        }

        grid.EndSection();
    }

    private void Advanced(StarSystem system)
    {
        if (!grid.BeginSection("StarSystem.Advanced", EditorIcons.Cog, "Advanced", false))
        {
            return;
        }

        grid.Text("Texture panels", Join(system.TexturePanelsFiles), "file");
        grid.Text("Encounter parameters", Join(system.EncounterParameters.Select(e => e.Nickname)),
            "EncounterParameters");
        grid.Number("Resource files", system.ResourceFiles.Count);
        grid.Number("Preloads", system.Preloads?.Length ?? 0);
        grid.EndSection();
    }

    private static string? Join(IEnumerable<string> values) =>
        values.ToArray() is { Length: > 0 } array ? string.Join(", ", array) : null;
}
