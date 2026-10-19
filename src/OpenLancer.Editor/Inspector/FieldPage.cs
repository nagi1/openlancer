using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a nebula or an asteroid field (section 10.1 of the M22 design). As in a Unity GameObject, the
/// item is its <c>[Nebula]</c> or <c>[Asteroids]</c> section, and its zone is its Transform. The page has these
/// sections:
/// <list type="bullet">
/// <item>The item section: the zone (a key entry, so read-only, with a button that selects the zone) and the file with
/// its list picker.</item>
/// <item>Area: the transform, the shape and the size of the zone, bound to the section of the zone.</item>
/// <item>Holes: the exclusion zones of the item, as the game data loaded them.</item>
/// <item>File: the sections of the nebula or asteroid file. The editor does not know their schema yet, so their rows
/// are read-only (decision D123). When a later milestone gives the file a schema, the same rows edit.</item>
/// </list>
/// The panel adds the loaded values and the raw INI blocks of the item section and of the zone section.
/// </summary>
internal sealed class FieldPage(EditorWindow window, PropertyGrid grid)
{
    private const string FileNote = "The Scene shows another file when you open the system again after the save.";

    private readonly SectionExtras extras = new(window, grid);
    private readonly UsedBySection usedBy = new(window, grid);

    // The sections of the last file, for one version of the edit view.
    private (EditorSession? Session, int Version, string? File) fileVersion;
    private IReadOnlyList<SectionRef> fileSections = [];

    /// <summary>Forgets the sections of the last file. Call it when the profile closes.</summary>
    public void Reset()
    {
        fileVersion = default;
        fileSections = [];
    }

    /// <param name="item">The <see cref="Nebula"/> or the <see cref="AsteroidField"/>.</param>
    /// <param name="section">The <c>[Nebula]</c> or <c>[Asteroids]</c> section of the item in W, or null.</param>
    public void Draw(object item, SectionRef? section)
    {
        var zone = SceneItems.ZoneOf(item);
        var zoneSection = zone == null ? null : window.SectionOf(zone);
        var kind = window.Scene.KindOf(item) ?? ObjectKinds.Unknown;
        Item(item, kind, zone, section);
        if (zone != null)
        {
            Area(item, zone, zoneSection);
        }

        Holes(item);
        File(item, section);
        if (section != null)
        {
            // The lines that name the zone of the item (decision D246 of the M3 design).
            usedBy.Draw(section, item.GetType().Name);
            extras.Draw([section], item.GetType().Name, moreKeys: true);
        }
    }

    /// <summary>
    /// The file that the item reads, or null when it has none. It is the last <c>file</c> line of its section in W, so
    /// the page follows a pick before the save. Without such a line, it is the file that the system loaded with.
    /// </summary>
    internal static string? FileValue(EditSession? edits, SectionRef? section, object item)
    {
        if (section != null && edits?.Get(section) is { } view && view.Lines("file") is { Count: > 0 } lines &&
            lines[^1].Values is [var value, ..] && value.ToString()!.Trim() is { Length: > 0 } text)
        {
            return text;
        }

        return SceneItems.FileOf(item) is { Length: > 0 } loaded ? loaded : null;
    }

    /// <summary>The sections of W of the file that a <c>file</c> value names, in file order. The value is a path below
    /// the data folder, as the engine reads it (Schema/Universe/Nebula.cs:38-45). Empty when the edit view has no such
    /// file.</summary>
    internal static IReadOnlyList<SectionRef> FileSections(EditSession edits, string? file) =>
        file != null && ThnReferences.PathOf(file) is { } path ? edits.Sections(path) : [];

    /// <summary>
    /// The notes of the Area section. A shared zone changes the other item too. The game draws no cloud outside a box,
    /// cylinder or ring nebula. A field draws its asteroids only near the camera.
    /// </summary>
    /// <param name="name">The display name of an item.</param>
    internal static IReadOnlyList<string> AreaNotes(object item, SystemIndex? index, Func<object, string> name)
    {
        var notes = new List<string>();
        if (SceneItems.ZoneOf(item) is not { } zone)
        {
            return notes;
        }

        foreach (var use in index?.UsesOf(zone) ?? [])
        {
            if (!ReferenceEquals(use.Item, item) && use.Kind is ZoneUseKind.AsteroidField or ZoneUseKind.Nebula)
            {
                var other = use.Kind == ZoneUseKind.AsteroidField ? "asteroid field" : "nebula";
                notes.Add($"The {other} {name(use.Item)} uses this zone too. A change of the area changes both.");
            }
        }

        switch (item)
        {
            case Nebula when zone.Shape is not (ShapeKind.Sphere or ShapeKind.Ellipsoid):
                notes.Add("The game draws no cloud outside a box, cylinder or ring nebula.");
                break;
            case AsteroidField field:
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Asteroids draw only within {field.FillDist:0.###} of the camera."));
                break;
        }

        return notes;
    }

    /// <summary>The section of the item: the zone, the file, and the number of sections that name the same
    /// file.</summary>
    private void Item(object item, ObjectKind kind, Zone? zone, SectionRef? section)
    {
        if (!grid.BeginSection("Field.Item", kind.Icon, kind.Label))
        {
            return;
        }

        using (grid.Bind(section))
        {
            grid.Reference("Zone", zone == null ? ObjectKinds.Of(ZoneRole.Other) : window.Scene.KindOf(zone)!,
                zone?.Nickname, zone == null ? null : Name(zone), zone, "zone");
            grid.SetNextNote(FileNote);
            grid.Text("File", SceneItems.FileOf(item), "file");
        }

        if (UsedBy(section, FileValue(window.Session?.Edits, section, item)) is > 1 and var count)
        {
            grid.Text("Used by", $"{count} sections name this file");
        }

        grid.EndSection();
    }

    /// <summary>The number of lines that name the file in the list of the <c>file</c> rule, or 0.</summary>
    private int UsedBy(SectionRef? section, string? file)
    {
        if (section == null || file == null || window.Session is not { } session ||
            session.Edits.Lists.Rules(section, "file") is not [{ Lists: [var list, ..] }, ..])
        {
            return 0;
        }

        return session.Edits.Lists.CountReferences(list, file);
    }

    /// <summary>The zone of the item as its area: the rows of the zone page, bound to the section of the zone.</summary>
    private void Area(object item, Zone zone, SectionRef? zoneSection)
    {
        if (!grid.BeginSection("Field.Area", EditorIcons.VectorSquare, "Area"))
        {
            return;
        }

        using (grid.Bind(zoneSection))
        {
            ZonePage.TransformRows(grid, zone);
            ZonePage.ShapeRows(grid, zone);
        }

        foreach (var note in AreaNotes(item, window.Scene.Index, Name))
        {
            grid.Row("Note", () => ImGui.TextWrapped(note));
        }

        if (grid.Link("Zone", zone.Nickname, "Select the zone"))
        {
            window.Selection = zone;
        }

        grid.EndSection();
    }

    /// <summary>The exclusion zones of the item, as the game data loaded them, with the values of their lines.</summary>
    private void Holes(object item)
    {
        var holes = item switch
        {
            Nebula nebula => (nebula.ExclusionZones ?? []).Select(e => (e.Zone, NebulaHole(e))).ToList(),
            AsteroidField field => field.ExclusionZones.Select(e => (e.Zone, FieldHole(e))).ToList(),
            _ => [],
        };
        if (holes.Count == 0 || !grid.BeginSection("Field.Holes", EditorIcons.Ban, "Holes", false))
        {
            return;
        }

        foreach (var (zone, detail) in holes)
        {
            grid.Reference("Hole", zone == null ? ObjectKinds.Of(ZoneRole.Exclusion) : window.Scene.KindOf(zone)!,
                zone?.Nickname, detail, zone, "exclusion");
        }

        grid.EndSection();
    }

    private static string NebulaHole(NebulaExclusionZone hole)
    {
        var shell = hole.ShellPath is { Length: > 0 } path ? Path.GetFileName(path.Replace('\\', '/')) : "none";
        return string.Create(CultureInfo.InvariantCulture,
            $"fog far {hole.FogFar:0.###}, shell {shell}, shell scalar {hole.ShellScalar:0.###}, " +
            $"max alpha {hole.ShellMaxAlpha:0.###}");
    }

    private static string FieldHole(AsteroidExclusionZone hole) =>
        $"billboards {(hole.ExcludeBillboards ? "excluded" : "kept")}, " +
        $"dynamic asteroids {(hole.ExcludeDynamicAsteroids ? "excluded" : "kept")}";

    /// <summary>A subheading for each section of the file of the item, and a row for each key of its lines.</summary>
    private void File(object item, SectionRef? section)
    {
        if (window.Session is not { } session || FileValue(session.Edits, section, item) is not { } file)
        {
            return;
        }

        var version = (session, session.Version, file);
        if (fileVersion != version)
        {
            fileSections = FileSections(session.Edits, file);
            fileVersion = version;
        }

        var title = $"File: {Path.GetFileName(file.Replace('\\', '/'))}";
        if (fileSections.Count == 0 || !grid.BeginSection("Field.File", EditorIcons.FolderOpen, title, false))
        {
            return;
        }

        foreach (var fileSection in fileSections)
        {
            if (session.Edits.Get(fileSection) is not { } view)
            {
                continue;
            }

            using (grid.Bind(fileSection))
            {
                grid.SubHeading(fileSection.Id.ToString());
                foreach (var key in view.Entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    grid.Text(key, null, key);
                }
            }
        }

        grid.EndSection();
    }

    /// <summary>The name of an item of the open system: its name, or else its file name or nickname.</summary>
    private string Name(object item) =>
        window.Session?.ItemName(item) is { Length: > 0 } name ? name : SceneItems.FallbackName(item) ?? "";
}
