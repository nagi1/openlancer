using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The rows of the mission lines that name an item of the world (section 10.18 of the M6a design). Each line has one
/// row, "{slot}: {trigger nickname} ({plain name})". A click opens the mission in the Mission tab and selects the line.
/// The Behaviors section of a system object and the Used by sections of a base and of a zone draw them.
/// </summary>
internal sealed class MissionUsesRows(EditorWindow window, PropertyGrid grid)
{
    /// <summary>The most rows that a page draws.</summary>
    private const int MaxRows = 100;

    /// <summary>The mission lines that name an item by its nickname.</summary>
    public IReadOnlyList<MissionObjectUse> UsesOf(string? nickname) =>
        nickname == null ? [] : window.Missions.UsesOf(nickname);

    /// <summary>Draws the rows in the open section of the grid.</summary>
    public void Rows(IReadOnlyList<MissionObjectUse> uses)
    {
        foreach (var use in uses.Take(MaxRows))
        {
            if (grid.Link("Mission", use.Text, "Open the mission at this line in the Mission tab."))
            {
                window.Missions.GoTo(use.Line.Section.Ref, use.Line.Entry);
            }
        }

        if (uses.Count > MaxRows)
        {
            grid.Text("More", $"{(uses.Count - MaxRows).ToString(CultureInfo.InvariantCulture)} more mission lines");
        }
    }

    /// <summary>Draws a section "Used by" with the rows, for a page that has no such section, for example a base. The
    /// section does not show when no line names the item.</summary>
    public void Section(string? nickname, string pageKey)
    {
        var uses = UsesOf(nickname);
        if (uses.Count == 0 || !grid.BeginSection($"{pageKey}.Missions", EditorIcons.Route,
                $"Used by ({uses.Count.ToString(CultureInfo.InvariantCulture)})"))
        {
            return;
        }

        Rows(uses);
        grid.EndSection();
    }
}
