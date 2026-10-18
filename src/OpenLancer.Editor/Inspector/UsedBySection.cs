using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The read-only "Used by" section of the page of an object, a zone, a nebula or a field (decision D246 of the M3
/// design): one row for each line that names the item, from the reverse index of the lists. No list names a light, so the
/// light page has no section. It reads W, so it shows the unsaved edits. A nebula or a field shows the lines that name
/// its zone, also the hole lines of the nebula and field files. The follow button of a row selects the scene item of the
/// line, or its section. The section does not show when no line names the item.
/// </summary>
internal sealed class UsedBySection(EditorWindow window, PropertyGrid grid)
{
    /// <summary>The most rows that the section draws.</summary>
    private const int MaxRows = 200;

    /// <param name="section">The section of the item in W.</param>
    /// <param name="pageKey">The key of the page, for the open state of the section, for example "Zone".</param>
    public void Draw(SectionRef section, string pageKey)
    {
        if (window.Session is not { } session || References(session.Edits, section) is not { Count: > 0 } references)
        {
            return;
        }

        using (grid.Bind(null))
        {
            if (!grid.BeginSection($"{pageKey}.UsedBy", EditorIcons.Link, $"Used by ({references.Count})"))
            {
                return;
            }

            foreach (var reference in references.Take(MaxRows))
            {
                Row(reference);
            }

            if (references.Count > MaxRows)
            {
                grid.Text("More", $"{references.Count - MaxRows} more lines");
            }

            grid.EndSection();
        }
    }

    /// <summary>The lines that name the item of a section, without the lines of the section itself.</summary>
    internal static IReadOnlyList<ListReference> References(EditSession session, SectionRef section)
    {
        var list = section.Id.Type.ToLowerInvariant() switch
        {
            "object" => "object_of_system",
            "zone" or "nebula" or "asteroids" => "zone_of_system",
            _ => null,
        };
        if (list == null || section.Id.Key is not { } key || !session.IsSystemFile(section.Path))
        {
            return [];
        }

        return session.Lists.ReferencesTo(list, key, section.Path)
            .Where(r => !r.Section.Equals(section))
            .ToList();
    }

    private void Row(ListReference reference)
    {
        var item = window.Scene.ItemOf(reference.Section);
        object target = item is null or StarSystem ? reference.Section : item;
        var kind = item is null or StarSystem
            ? ObjectKinds.OfSection(reference.Section.Id.Type)
            : window.Scene.KindOf(item) ?? ObjectKinds.Unknown;
        var lines = window.Session!.Edits.Get(reference.Section)?.Lines(reference.Key).Count ?? 0;
        var label = lines > 1 ? $"{reference.Key} ({reference.Line + 1})" : reference.Key;
        var nickname = reference.Section.Id.Key ?? $"[{reference.Section.Id.Type}]";
        var name = item is null or StarSystem
            ? Path.GetFileName(reference.Section.Path)
            : window.Session.ItemName(item);
        grid.Reference(label, kind, nickname, name, target);
    }
}
