using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// Prefab mode in the draft (section 5.7 of the M6b design): the template of an asset of the active mod as a script in
/// the role form, which the trigger, line and link commands change. After each command, the draft writes the template
/// back into the asset and compiles each linked instance of the asset in the active mod (propagation, decision D675).
/// </summary>
internal sealed partial class MissionDraft
{
    /// <summary>A template in the draft: its prefab, its role form, and the template and the state of its last
    /// write.</summary>
    private sealed class TemplateDraft(PrefabRef prefab, string name, EditDocument document, PrefabTemplate written)
    {
        public PrefabRef Prefab { get; } = prefab;

        public string Name { get; set; } = name;

        public EditDocument Document { get; } = document;

        public PrefabTemplate Written { get; set; } = written;

        public object WrittenState { get; set; } = document.State;

        /// <summary>The roles that a command renamed since the last write: the old role and the new role. The records
        /// of the instances follow, so a member keeps its section and its nickname.</summary>
        public Dictionary<string, string> Renames { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, TemplateDraft> templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The role form of the template of a script <c>prefab:mod:&lt;id&gt;</c>, to change it. A built-in prefab
    /// and a prefab of a lower mod refuse, because they cannot change (decision D672).</summary>
    /// <exception cref="EditException"><c>PrefabNotFound</c>, <c>BuiltInPrefab</c>, <c>LowerModPrefab</c> or
    /// <c>PrefabProblems</c>.</exception>
    private EditDocument Template(string path)
    {
        var prefab = PrefabRef.TryParse(path) ?? throw Refuse(MissionErrors.PrefabNotFoundError(path));
        if (templates.TryGetValue(prefab.Script, out var known))
            return known.Document;
        var asset = ActivePrefab(prefab);
        var template = TemplateOf(asset);
        var document = EditDocument.NewWhole(prefab.Script);
        document.Restore(document.State with
        {
            Working = ImmutableList.CreateRange(PrefabMode.RoleForm(template)),
            Exists = true,
        });
        templates[prefab.Script] = new TemplateDraft(prefab, template.Name, document, template);
        return document;
    }

    /// <summary>
    /// Writes each template that a command changed back into its asset, then compiles each linked instance of the asset
    /// in the active mod (section 5.7 of the M6b design). A port whose role is gone goes with it. A template with a
    /// problem, and a compile that fails, refuse the whole command. The plan command calls it after each command.
    /// </summary>
    public void FinishTemplates()
    {
        foreach (var draft in templates.Values)
        {
            if (ReferenceEquals(draft.Document.State, draft.WrittenState))
                continue;
            DropPortsOfGoneRoles(draft.Document);
            var text = PrefabMode.TemplateText(draft.Document.Working, draft.Written);
            var template = PrefabTemplate.Read(text, out var problems);
            if (template == null || problems.Count > 0)
                throw Refuse(MissionErrors.PrefabProblemsError(draft.Name, problems.Count, problems.FirstOrDefault()?.ToString() ?? "")
                    with
                    {
                        Message = $"The edit gives prefab '{draft.Name}' a problem, so the editor did not apply it. " +
                                  (problems.FirstOrDefault()?.ToString() ?? ""),
                    });
            if (!template.Id.Equals(draft.Prefab.Id, StringComparison.Ordinal))
                throw Refuse(new EditError(EditErrorKind.Refused, EditPlan.PlanRefused,
                    $"The id of prefab '{draft.Name}' is the name of its file. Use Rename to change it."));
            SetPrefabText(draft.Prefab.Id, template.Write());
            var (instances, scripts) = Propagate(draft.Prefab, template, roles: draft.Renames);
            draft.Renames.Clear();
            PropagationNote(template.Name, instances, scripts);
            draft.Written = template;
            draft.Name = template.Name;
            draft.WrittenState = draft.Document.State;
        }
    }

    /// <summary>Removes each <c>[Port]</c> section of a template whose role no section has, for example after a delete of
    /// the section. The inverse puts the port back.</summary>
    private void DropPortsOfGoneRoles(EditDocument template)
    {
        var roles = template.Working.Where(s => !PrefabMode.IsHeader(s.Name)).Select(PrefabMode.RoleOf).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        for (var i = template.Working.Count - 1; i >= 0; i--)
        {
            var section = template.Working[i];
            if (!MissionIndex.Is(section.Name, "Port") || HeaderValue(section, "role") is not { } role || roles.Contains(role))
                continue;
            var removed = Remove(template, i);
            Inverses.Add(new RestoreSection(new ScriptRef(template.Path), i, RawSection(removed)));
        }
    }

    /// <summary>The ports of a template that name a role get the new name of the role. The inverse gives the old name
    /// back.</summary>
    public void RenameRole(EditDocument template, string old, string name)
    {
        if (templates.Values.FirstOrDefault(t => ReferenceEquals(t.Document, template)) is { } draft)
        {
            // A rename back of a role gives the old role again.
            var first = draft.Renames.FirstOrDefault(r => r.Value == old).Key ?? old;
            draft.Renames.Remove(first);
            if (first != name)
                draft.Renames[first] = name;
        }
        for (var i = 0; i < template.Working.Count; i++)
        {
            var section = template.Working[i];
            if (!MissionIndex.Is(section.Name, "Port"))
                continue;
            var row = section.ToList().FindIndex(e => MissionIndex.Is(e.Name, "role") && e.Count > 0 &&
                                                      MissionIndex.Text(e[0]).Equals(old, StringComparison.Ordinal));
            if (row < 0)
                continue;
            var entries = section.ToList();
            entries[row] = PrefabMode.Entry(section, "role", [name]);
            var oldLine = section[row];
            SetLines(template, i, entries, inverse: false);
            Inverses.Add(new SetLine(RowOf(template, i, row), RawLine(oldLine)));
        }
    }

    /// <summary>The value text of the first line of a key of a header section, or null.</summary>
    private static string? HeaderValue(Section section, string key) =>
        section.FirstOrDefault(e => MissionIndex.Is(e.Name, key)) is { Count: > 0 } line
            ? string.Join(", ", MissionValues.TextsOf(line))
            : null;

    /// <summary>The template document of a prefab and the index of its <c>[Field]</c> section with an id.</summary>
    /// <exception cref="EditException"><c>FieldNotFound</c>.</exception>
    public (EditDocument Template, int Index) FieldSection(PrefabRef prefab, string id)
    {
        var template = Template(prefab.Script);
        for (var i = 0; i < template.Working.Count; i++)
        {
            if (MissionIndex.Is(template.Working[i].Name, "Field") && HeaderValue(template.Working[i], "id") == id.Trim())
                return (template, i);
        }
        throw Refuse(MissionErrors.FieldNotFoundError(templates[prefab.Script].Name, id, FieldIds(template)));
    }

    /// <summary>The ids of the fields of a template document.</summary>
    public static IEnumerable<string> FieldIds(EditDocument template) =>
        template.Working.Where(s => MissionIndex.Is(s.Name, "Field")).Select(s => HeaderValue(s, "id")).OfType<string>();

    /// <summary>The field of a <c>[Field]</c> section of a template document, as the template reads it.</summary>
    public PrefabFieldDef FieldOf(EditDocument template, int index)
    {
        var text = PrefabMode.TemplateText([template.Working[0], template.Working[index]], templates.Values
            .First(t => ReferenceEquals(t.Document, template)).Written with { Sections = [], Fields = [], Ports = [] });
        var read = PrefabTemplate.Read(text, out _);
        return read?.Fields.FirstOrDefault() ?? throw Refuse(MissionErrors.FieldNotFoundError("", "", []));
    }

    /// <summary>True when a document is the role form of a template.</summary>
    public static bool IsTemplateDocument(EditDocument document) => IsTemplate(document);

    /// <summary>The number of lines of the template sections that use a field: a slot in a value, or a line of the
    /// field.</summary>
    public static int SlotUses(EditDocument template, string field) =>
        template.Working.Where(s => !PrefabMode.IsHeader(s.Name)).SelectMany(s => s)
            .Count(e => PrefabSlot.KeyField(e.Name) == field ||
                        MissionValues.TextsOf(e).Any(t => PrefabSlot.Of(t).Any(slot => slot.Field == field)));
}
