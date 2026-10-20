using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The prefab instances and the prefab assets in the draft (section 5 of the M6b design).</summary>
internal sealed partial class MissionDraft
{
    /// <summary>The text of each editor file of the active mod before the first change of the batch, by its path
    /// relative to the mod folder. The commit check reads the records and the prefabs before the batch from it.</summary>
    public Dictionary<string, string?> ModBefore { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The depth of the steps of a prefab command that change the members of an instance: a compile, a place
    /// or a remove of an instance. While it is above 0, the guard of the members lets the edits through, and a delete
    /// of a member is not a removed-member override.</summary>
    public int InstanceOps { get; set; }

    /// <summary>The sections that the body guards of the running inverses name, by their canonical reference (section 4.8
    /// of the M6b design). A guard proves that its section is as the edit left it, so an inverse can change the lines of
    /// such a member back. A member without a guard stays guarded.</summary>
    public HashSet<string> GuardedSections { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An editor file of the active mod, to change it. The draft keeps its text before the batch.</summary>
    /// <exception cref="InvalidOperationException">The session is read-only, or it has no active mod.</exception>
    public ModFileDocument ModFile(string relativePath)
    {
        var document = Scope.ModFile(relativePath);
        ModBefore.TryAdd(relativePath, document.Text);
        return document;
    }

    /// <summary>The text of an editor file of the active mod before the batch: the text that the draft kept, or W now
    /// for a file that the batch did not change.</summary>
    public string? ModTextBefore(string relativePath)
    {
        if (ModBefore.TryGetValue(relativePath, out var text))
            return text;
        try
        {
            return Session.ModFileText(relativePath);
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Prefabs ----

    /// <summary>The prefab of a reference now, or a refusal when no mod has it.</summary>
    /// <exception cref="EditException"><c>PrefabNotFound</c>.</exception>
    public PrefabAsset Prefab(PrefabRef prefab) =>
        Editor.Prefab(prefab) ?? throw Refuse(MissionErrors.PrefabNotFoundError(prefab.ToString(),
            ItemRefs.Hint(prefab.Id, Editor.Prefabs().Select(p => p.Ref.Id))));

    /// <summary>The template of a prefab, or a refusal when its file has a problem.</summary>
    /// <exception cref="EditException"><c>PrefabProblems</c>.</exception>
    public PrefabTemplate TemplateOf(PrefabAsset asset) =>
        asset.Template ?? throw Refuse(MissionErrors.PrefabProblemsError(asset.Name, asset.Problems.Count,
            asset.Problems.FirstOrDefault()?.ToString() ?? ""));

    /// <summary>An asset of the active mod, to change it. A built-in prefab and an asset of a lower mod refuse.</summary>
    /// <exception cref="EditException"><c>PrefabNotFound</c>, <c>BuiltInPrefab</c> or <c>LowerModPrefab</c>.</exception>
    public PrefabAsset ActivePrefab(PrefabRef prefab)
    {
        var asset = Prefab(prefab);
        if (prefab.Builtin)
            throw Refuse(MissionErrors.BuiltInPrefabError(asset.Name));
        if (!asset.Active)
            throw Refuse(MissionErrors.LowerModPrefabError(asset.Name, asset.ModId ?? ""));
        return asset;
    }

    /// <summary>Writes the text of an asset of the active mod, or deletes it with null.</summary>
    public void SetPrefabText(string id, string? text)
    {
        try
        {
            ModFile(PrefabAsset.PathOf(id)).SetText(text);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.IO.IOException or
                                      UnauthorizedAccessException)
        {
            throw Refuse(new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, e.Message));
        }
    }

    // ---- Instances ----

    /// <summary>The script and the record of a linked instance.</summary>
    /// <exception cref="EditException"><c>InstanceNotFound</c>.</exception>
    public (EditDocument Script, InstanceRecord Record) Linked(InstanceRef instance)
    {
        var document = Script(instance.Script);
        var graph = Graph(document);
        return graph.Instance(instance.Id) is { } record
            ? (document, record)
            : throw Refuse(MissionErrors.InstanceNotFoundError(document.Path, instance.Id,
                ItemRefs.Hint(instance.Id, graph.Instances.Select(i => i.Id))) with { Target = instance.ToString() });
    }

    /// <summary>Puts a record into the graph file of a script in place of the record with its id, or at an index. Null
    /// removes the record with the id.</summary>
    public void SetRecord(EditDocument script, string id, InstanceRecord? record, int? index = null)
    {
        var graph = Graph(script);
        var records = graph.Instances.ToList();
        var at = records.FindIndex(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (record == null)
        {
            if (at >= 0)
                records.RemoveAt(at);
        }
        else if (at >= 0)
            records[at] = record;
        else
            records.Insert(Math.Clamp(index ?? records.Count, 0, records.Count), record);
        SetGraph(script, graph with { Instances = records });
    }

    /// <summary>The index of a member section in a script: a section of a type with a nickname, or without a type a
    /// trigger first, then another section. -1 when the script has none.</summary>
    public static int MemberIndex(EditDocument script, string? type, string nickname)
    {
        if (type == null || MissionIndex.Is(type, "Trigger"))
        {
            var trigger = TriggerIndex(script, nickname);
            if (trigger >= 0 || type != null)
                return trigger;
        }
        if (type != null)
            return IndexOfKey(script, type, nickname);
        for (var i = 0; i < script.Working.Count; i++)
        {
            if (!MissionIndex.Is(script.Working[i].Name, "Trigger") &&
                string.Equals(SectionIdentity.KeyOf(script.Working[i])?.Trim(), nickname.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>The record and the role of the linked instance whose member is the section at an index, or null. A
    /// removed member is not a member.</summary>
    public (InstanceRecord Record, string Role)? MemberAt(EditDocument script, int index)
    {
        if (IsTemplate(script))
            return null;
        var graph = Graph(script);
        if (graph.Instances.Count == 0)
            return null;
        var section = script.Working[index];
        var key = MissionIndex.Is(section.Name, "Trigger") ? TriggerRules.Nickname(section) : SectionIdentity.KeyOf(section);
        if (key == null)
            return null;
        foreach (var record in graph.Instances)
        {
            foreach (var member in record.Members.Where(m => !record.Removed.Contains(m.Role)))
            {
                if (!string.Equals(member.Nickname, key.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                var type = Editor.Prefab(record.Prefab)?.Template?.Section(member.Role)?.Type;
                if (type == null || MissionIndex.Is(type, section.Name))
                    return (record, member.Role);
            }
        }
        return null;
    }

    /// <summary>Refuses a change of the lines of a member of a linked instance (decision D676): the user changes a field
    /// of the instance, opens the prefab, or unpacks the instance. The steps of a prefab command pass, and so does an
    /// inverse whose body guard names the section.</summary>
    /// <exception cref="EditException"><c>PrefabMember</c>.</exception>
    public void RefuseMember(EditDocument script, int index)
    {
        if (InstanceOps > 0 || MemberAt(script, index) is not var (record, _) ||
            GuardedSections.Count > 0 && GuardedSections.Contains(RefOf(script, index)))
            return;
        throw Refuse(MissionErrors.PrefabMemberError(Capital(Owner(script, index)), record.Id) with
        {
            Target = RefOf(script, index),
        });
    }

    /// <summary>The delete of a member of a linked instance: the record, the role, and the template when the delete
    /// compiles the instance.</summary>
    /// <param name="Template">The template of the instance, or null when the instance does not compile: its prefab is
    /// missing (rule P02), it has a hand edit (rule P01), its prefab changed after its last compile (rule P03), or its
    /// compile fails. A delete does not bring the changes of the prefab.</param>
    internal sealed record MemberDelete(InstanceRecord Record, string Role, PrefabTemplate? Template);

    /// <summary>The delete of the section at an index when the section is a member of a linked instance, or null. The
    /// steps of a prefab command do not make a removed-member override.</summary>
    internal MemberDelete? MemberDeleteAt(EditDocument script, int index)
    {
        if (InstanceOps > 0 || MemberAt(script, index) is not var (record, role))
            return null;
        var template = Editor.Prefab(record.Prefab)?.Template;
        if (template == null || HandEdited(script, record) || record.PrefabHash != null && record.PrefabHash != template.ContentHash)
            return new MemberDelete(record, role, null);
        try
        {
            PrefabCompiler.Compile(template, LinkedInstance.InputOf(record with { Removed = [.. record.Removed, role] }, template));
            return new MemberDelete(record, role, template);
        }
        catch (EditException)
        {
            return new MemberDelete(record, role, null);
        }
    }

    /// <summary>
    /// The lines that a delete of the section at an index handles itself: each line that names the section, except the
    /// lines of the other members of its linked instance when the delete compiles the instance. The compile leaves out
    /// such lines (decision D676 c), so they do not become "Missing" lines.
    /// </summary>
    public List<(int Section, int Row)> OutsideLines(EditDocument script, int index, IEnumerable<(int Section, int Row)> lines)
    {
        if (MemberDeleteAt(script, index) is not { Template: { } template } delete)
            return lines.ToList();
        var members = delete.Record.Members.Where(m => !delete.Record.Removed.Contains(m.Role))
            .Select(m => MemberIndex(script, template.Section(m.Role)?.Type, m.Nickname)).ToHashSet();
        return lines.Where(l => !members.Contains(l.Section)).ToList();
    }

    /// <summary>
    /// Records the delete of a member of a linked instance as a removed-member override (decision D676 c, the rule of
    /// Unity 2022.2 and later): the record gets the role. The instance compiles again, so each line of another member
    /// that names the removed member goes, as Unity sets a reference to a removed child to None. The prefab does not
    /// change. The inverse gives the record and the lines back.
    /// </summary>
    private void MemberRemoved(EditDocument script, MemberDelete delete)
    {
        var updated = delete.Record with { Removed = [.. delete.Record.Removed, delete.Role] };
        if (delete.Template is { } template)
        {
            Compile(script, updated, template);
            return;
        }
        // An instance that does not compile keeps its lines and its hash. It shows P01 or P02.
        Inverses.AddRange(RunInner(new RestoreInstanceRecord(new ScriptRef(script.Path), updated)));
        Changes.Add(new InstanceRef(script.Path, delete.Record.Id).ToString());
    }

    /// <summary>The hash of the lines of a compile (<c>lines_hash</c>).</summary>
    /// <exception cref="EditException">A line does not read back as its values.</exception>
    public static string LinesHashOf(IReadOnlyList<CompiledSection> compiled) =>
        PrefabCompiler.LinesHash(PrefabCompiler.Sections(compiled));

    /// <summary>
    /// Compiles a linked instance into W (section 5.4 of the M6b design): each member gets the lines of the compile in
    /// its place, a new member goes to the end of the script, and a member whose role the prefab does not have any more
    /// goes. Then the record gets the members, the hash of the lines and the hash of the prefab. The inverses of the
    /// steps go to the command that runs now.
    /// </summary>
    /// <remarks>A compile changes only the sections of the instance: the present members of its record in W. A made name
    /// that another section has refuses (section 4.9 of the M6b design). So does the name of a removed member when a
    /// section of the user took it after the delete.</remarks>
    /// <param name="adopt">True when the members of the record are sections that become the instance now ("Save as
    /// prefab").</param>
    /// <returns>The new record.</returns>
    /// <exception cref="EditException">The compile refuses. The message names the instance, the field and the
    /// reason.</exception>
    public InstanceRecord Compile(EditDocument script, InstanceRecord record, PrefabTemplate template, bool adopt = false)
    {
        InstanceOps++;
        try
        {
            IReadOnlyList<CompiledSection> compiled;
            try
            {
                compiled = PrefabCompiler.Compile(template, LinkedInstance.InputOf(record, template));
            }
            catch (EditException e)
            {
                throw Refuse(e.Error with { Message = $"Prefab instance '{record.Id}' in {script.Path}: {e.Error.Message}" });
            }
            var path = new ScriptRef(script.Path);
            var owner = adopt ? record : Graph(script).Instance(record.Id);
            var owned = owner?.Members.Where(m => !owner.Removed.Contains(m.Role)).Select(m => m.Nickname)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var section in compiled)
            {
                var at = MemberIndex(script, section.Type, section.Nickname);
                if (at >= 0 && owned.Contains(section.Nickname))
                    Inverses.AddRange(RunInner(new ReplaceSectionBody(ItemOf(script, at), section.Entries)));
                else
                {
                    if (NameProblem(script, -1, section.Type, section.Nickname) is { } taken)
                        throw Refuse(taken with
                        {
                            Message = $"Prefab instance '{record.Id}' gives section '{section.Role}' the name " +
                                      $"'{section.Nickname}'. Script {script.Path} has a section with this name that is " +
                                      "not a part of the instance. Rename or delete that section, then try again.",
                        });
                    Inverses.AddRange(RunInner(new RestoreSection(path, int.MaxValue, new RawSection(section.Type, section.Entries))));
                }
            }
            // A member whose role the prefab does not have any more goes. A removed member stays removed.
            foreach (var member in record.Members.Where(m => template.Section(m.Role) == null && !record.Removed.Contains(m.Role)))
            {
                var at = MemberIndex(script, null, member.Nickname);
                if (at < 0)
                    continue;
                Inverses.AddRange(MissionIndex.Is(script.Working[at].Name, "Trigger")
                    ? RunInner(new RemoveTrigger(new TriggerRef(script.Path, member.Nickname)))
                    : RunInner(new RemoveSection(ItemOf(script, at))));
            }
            var names = record.Members.GroupBy(m => m.Role).ToDictionary(g => g.Key, g => g.First().Nickname);
            var updated = record with
            {
                Members = template.Sections
                    .Select(s => new InstanceMember(s.Role, compiled.FirstOrDefault(c => c.Role == s.Role)?.Nickname ??
                                                            names.GetValueOrDefault(s.Role) ?? ""))
                    .Where(m => m.Nickname.Length > 0).ToList(),
                Removed = record.Removed.Where(r => template.Section(r) != null).ToList(),
                // A link of a port that the prefab does not have any more goes.
                Ports = record.Ports.Where(p => template.Port(p.Port) != null).ToList(),
                LinesHash = LinesHashOf(compiled),
                PrefabHash = template.ContentHash,
            };
            if (!updated.Equals(Graph(script).Instance(record.Id)))
                Inverses.AddRange(RunInner(new RestoreInstanceRecord(path, updated)));
            Changes.Add(new InstanceRef(script.Path, record.Id).ToString());
            return updated;
        }
        finally
        {
            InstanceOps--;
        }
    }

    /// <summary>
    /// Compiles each linked instance of a prefab in the scripts of the active mod (propagation, section 5.7 of the M6b
    /// design, decision D675). An instance with a hand edit (P01) does not compile: the result lists it, and the notes
    /// name it. The steps go into the inverse of the command that runs now.
    /// </summary>
    /// <param name="except">The id and the script of an instance that the command compiled itself, or null.</param>
    /// <param name="roles">The roles of the template that an edit renamed: the old role and the new role, or null. The
    /// records follow, so each member keeps its section.</param>
    /// <returns>The number of instances that compiled and the number of their scripts.</returns>
    public (int Instances, int Scripts) Propagate(PrefabRef prefab, PrefabTemplate template, (string Script, string Id)? except = null,
        IReadOnlyDictionary<string, string>? roles = null)
    {
        var instances = 0;
        var scripts = new HashSet<string>(OpenLancer.Data.IO.VfsPath.Comparer);
        foreach (var (path, record) in Editor.InstancesOf(prefab).ToList())
        {
            if (except is var (script, id) && OpenLancer.Data.IO.VfsPath.Comparer.Equals(script, path) &&
                record.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                continue;
            var document = Script(path);
            if (HandEdited(document, record))
            {
                Notes.Add($"Prefab instance '{record.Id}' in {path} did not change, because its lines differ from the " +
                          "prefab. Unpack it or compile it again.");
                Warnings.Add(new Problem(DiagnosticSeverity.Warning, PrefabLinks.HandEditMessage(record.Id))
                {
                    Ref = new InstanceRef(path, record.Id).ToString(),
                    Rule = "P01",
                });
                continue;
            }
            string Role(string role) => roles?.GetValueOrDefault(role) ?? role;
            var renamed = roles is { Count: > 0 }
                ? record with
                {
                    Members = record.Members.Select(m => m with { Role = Role(m.Role) }).ToList(),
                    Removed = record.Removed.Select(Role).ToList(),
                    Ports = record.Ports,
                }
                : record;
            Compile(document, renamed, template);
            instances++;
            scripts.Add(path);
            PropagatedScripts.Add(path);
        }
        return (instances, scripts.Count);
    }

    /// <summary>True when the lines of the members of a record in W differ from its last compile (rule P01).</summary>
    public bool HandEdited(EditDocument script, InstanceRecord record)
    {
        if (record.LinesHash == null)
            return false;
        var template = Editor.Prefab(record.Prefab)?.Template;
        var sections = new List<LibreLancer.Data.Ini.Section>();
        foreach (var member in record.Members.Where(m => !record.Removed.Contains(m.Role)))
        {
            var at = MemberIndex(script, template?.Section(member.Role)?.Type, member.Nickname);
            if (at < 0)
                return true;
            sections.Add(script.Working[at]);
        }
        return PrefabCompiler.LinesHash(sections) != record.LinesHash;
    }

    /// <summary>
    /// The records of a script after a rename of a trigger or an item that the script names (section 4.9 of the M6b
    /// design): each value, override and port of a record that names the old name names the new name, by the reference
    /// kinds of the item. Then the hash of the compiled lines follows. So the rename changes the records as it changes
    /// the lines of the members, and makes no hand edit (rule P01).
    /// </summary>
    /// <param name="kinds">The value kinds of the references to the item (see <see cref="MissionLinks.KindsOf"/>).</param>
    public void RenameInRecords(EditDocument script, IReadOnlyCollection<MissionValueKind> kinds, string old, string name)
    {
        foreach (var record in Graph(script).Instances.ToList())
        {
            var template = Editor.Prefab(record.Prefab)?.Template;
            IReadOnlyList<InstanceValue> Renamed(IReadOnlyList<InstanceValue> values) =>
                template == null ? values
                    : values.Select(v => template.Field(v.Field) is { } field
                        ? v with { Rows = v.Rows.Select(r => RenameRow(field, r, kinds, old, name)).ToList() }
                        : v).ToList();
            var updated = record with
            {
                Values = Renamed(record.Values),
                Overrides = Renamed(record.Overrides),
                Ports = kinds.Contains(MissionValueKind.Trigger)
                    ? record.Ports.Select(p => p.Trigger.Equals(old, StringComparison.OrdinalIgnoreCase) ? p with { Trigger = name } : p)
                        .ToList()
                    : record.Ports,
            };
            if (updated.Equals(record))
                continue;
            if (template != null)
            {
                try
                {
                    updated = updated with { LinesHash = LinesHashOf(PrefabCompiler.Compile(template, LinkedInstance.InputOf(updated, template))) };
                }
                catch (EditException)
                {
                    // The old hash stays, so the instance shows P01.
                }
            }
            Inverses.AddRange(RunInner(new RestoreInstanceRecord(new ScriptRef(script.Path), updated)));
            Changes.Add(new InstanceRef(script.Path, record.Id).ToString());
        }
    }

    /// <summary>A row of a field in its text form, with each value of the reference kinds that names the old name in place
    /// of the new name. A condition and a line find their values by the schema of their key.</summary>
    private static string RenameRow(PrefabFieldDef field, string text, IReadOnlyCollection<MissionValueKind> kinds, string old,
        string name)
    {
        var row = PrefabRow.Parse(field.Kind, text);
        var texts = row.Texts.ToList();
        var places = new List<int>();
        switch (field.Kind)
        {
            case PrefabFieldKind.Value when kinds.Contains(field.ValueKind):
                places.AddRange(Enumerable.Range(0, texts.Count));
                break;
            case PrefabFieldKind.Triggers when kinds.Contains(MissionValueKind.Trigger):
                places.AddRange(Enumerable.Range(0, texts.Count));
                break;
            case PrefabFieldKind.List:
                var at = 0;
                foreach (var column in field.Columns)
                {
                    if (kinds.Contains(column.Kind))
                        places.AddRange(Enumerable.Range(at, column.Width));
                    at += column.Width;
                }
                break;
            case PrefabFieldKind.Condition or PrefabFieldKind.Lines when row.Key is { Length: > 0 } key:
                var section = new LibreLancer.Data.Ini.Section(MissionSchema.Retail.Directive(key) != null ? "ObjList" : "Trigger");
                try
                {
                    section.Add(MissionValues.Entry(section, key, texts, null, binary: false));
                }
                catch (EditException)
                {
                    // A line that does not read keeps its text. The compile then refuses with the reason.
                    break;
                }
                places.AddRange(MissionLinks.Naming(MissionSchema.Retail, [section], kinds, old).Select(p => p.Value));
                break;
        }
        var changed = false;
        foreach (var place in places.Where(p => p < texts.Count && texts[p].Equals(old, StringComparison.OrdinalIgnoreCase)))
        {
            texts[place] = name;
            changed = true;
        }
        return changed ? (row with { Texts = texts }).Format() : text;
    }

    /// <summary>True for the template of a prefab in prefab mode, the script <c>prefab:mod:&lt;id&gt;</c>, and false for
    /// a mission script (section 5.7 of the M6b design).</summary>
    private static bool IsTemplate(EditDocument document) => MissionRefs.IsPrefab(document.Path);
}
