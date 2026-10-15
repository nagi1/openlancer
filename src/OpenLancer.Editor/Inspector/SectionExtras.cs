using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ImGuiNET;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The sections at the end of an Inspector page that edits sections of the edit view: "More keys" with the keys of the
/// schema that no row above shows, "Other keys" with the keys that the schema does not know, and "INI text" with the
/// lines of the sections. So the page can edit each key of its sections. A page of more than one section gets a
/// subheading for each section.
/// </summary>
internal sealed class SectionExtras(EditorWindow window, PropertyGrid grid)
{
    private const string MoreKeysHeading = "Keys that no row above shows";
    private const string OtherKeysHeading = "Keys that the engine does not read";

    /// <param name="sections">The sections of the page. A section that the edit view does not have shows nothing.</param>
    /// <param name="key">The first part of the settings keys of the sections, "Type".</param>
    /// <param name="moreKeys">True to show "More keys". A page that shows each key of its schema has no such
    /// section.</param>
    public void Draw(IReadOnlyList<SectionRef> sections, string key, bool moreKeys)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        var edits = session.Edits;
        var lists = sections.Select(s => (Section: s, Keys: SchemaPage.Layout(edits, s, grid.OpenEdit))).ToList();
        if (moreKeys)
        {
            // The rows above drew in this frame, so the list has the keys that they did not show.
            Keys(key + ".More", EditorIcons.Plus, "More keys", MoreKeysHeading, false, sections.Count > 1,
                sections.Select(s => (s, NotShown(edits, s))));
        }

        // A section rarely has such keys, so the list is open until the user closes it.
        Keys(key + ".Other", EditorIcons.Th, "Other keys", OtherKeysHeading, true, sections.Count > 1,
            lists.Select(l => (l.Section, l.Keys.Unknown)));
        IniText(sections, key + ".Ini");
    }

    /// <summary>The keys of "More keys" of a section in this frame (see <see cref="MoreKeys"/>).</summary>
    private IReadOnlyList<string> NotShown(EditSession edits, SectionRef section) =>
        PropertyGrid.LayoutView(edits, section, grid.OpenEdit) is { } layout
            ? MoreKeys(edits.Schema(section), grid.Shown(section), layout)
            : [];

    /// <summary>
    /// The keys of "More keys": the values of the schema that no bound row showed, without the key entry. Two names of
    /// one field set one value, for example <c>ambient_color</c> and <c>ambient</c> of an <c>[Object]</c>. So for
    /// each value, the list has the names that have lines, or else the first name. A line of the other name would set
    /// the value a second time, and a row of the value edits only one of the two lines.
    /// </summary>
    /// <param name="shown">The keys that bound rows showed in this frame.</param>
    /// <param name="layout">The section that the page lays out its rows from, so the list stays while a field edit is
    /// open (see <see cref="PropertyGrid.LayoutView"/>).</param>
    internal static IReadOnlyList<string> MoreKeys(IReadOnlyList<KeySchema> schema, IReadOnlySet<string> shown,
        SectionView layout) =>
        schema.Where(k => !k.IsKeyEntry)
            // A key without a field is its own value, for example a key that an entry handler reads.
            .GroupBy(k => (k.Field, Name: k.Field == null ? k.Name.ToLowerInvariant() : null))
            .Where(names => !names.Any(k => shown.Contains(k.Name)))
            .SelectMany(names => names.Where(k => layout.Lines(k.Name).Count > 0).DefaultIfEmpty(names.First()))
            .Select(k => k.Name)
            .ToList();

    /// <summary>A section with a row for each key of each section, or nothing when no section has a key.</summary>
    /// <param name="heading">The heading at the top of the section, which tells what the keys are.</param>
    /// <param name="defaultOpen">The open state until the user opens or closes the section.</param>
    /// <param name="sectionHeadings">True to give the keys of each section a subheading with the section.</param>
    private void Keys(string key, char icon, string title, string heading, bool defaultOpen, bool sectionHeadings,
        IEnumerable<(SectionRef Section, IReadOnlyList<string> Keys)> lists)
    {
        var shown = lists.Where(l => l.Keys.Count > 0).ToList();
        if (shown.Count == 0 || !grid.BeginSection(key, icon, title, defaultOpen))
        {
            return;
        }

        grid.SubHeading(heading);
        foreach (var (section, keys) in shown)
        {
            using (grid.Bind(section))
            {
                if (sectionHeadings)
                {
                    grid.SubHeading(section.Id.ToString());
                }

                foreach (var name in keys)
                {
                    grid.Text(name, null, name);
                }
            }
        }

        grid.EndSection();
    }

    /// <summary>The sections as INI text, to read and to copy.</summary>
    private void IniText(IReadOnlyList<SectionRef> sections, string key)
    {
        var views = sections.Select(s => window.Session!.Edits.Get(s)).OfType<SectionView>().ToList();
        if (views.Count == 0 || !grid.BeginSection(key, EditorIcons.Th, "INI text", defaultOpen: false))
        {
            return;
        }

        var text = new StringBuilder();
        foreach (var view in views)
        {
            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            text.Append($"[{view.Type}]");
            foreach (var entry in view.Entries)
            {
                text.Append('\n').Append(FieldEdit.IniText(entry.Name, [entry]));
            }
        }

        var ini = text.ToString();
        grid.Row("INI", () =>
        {
            var lines = Math.Min(ini.Count(c => c == '\n') + 1, 16);
            var height = lines * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y;
            ImGui.InputTextMultiline("##ini", ref ini, (uint)Encoding.UTF8.GetByteCount(ini) + 1,
                new System.Numerics.Vector2(-1, height), ImGuiInputTextFlags.ReadOnly);
        });
        grid.EndSection();
    }
}
