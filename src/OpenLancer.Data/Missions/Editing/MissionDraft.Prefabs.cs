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

    /// <summary>The depth of the inverses that run with a body guard (section 4.8 of the M6b design). The guard proves
    /// that each section is as the edit left it, so an inverse can change the lines of a member back.</summary>
    public int GuardedReverts { get; set; }

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
    /// of the instance, opens the prefab, or unpacks the instance. The steps of a prefab command and an inverse with a
    /// body guard pass.</summary>
    /// <exception cref="EditException"><c>PrefabMember</c>.</exception>
    public void RefuseMember(EditDocument script, int index)
    {
        if (InstanceOps > 0 || GuardedReverts > 0 || MemberAt(script, index) is not var (record, _))
            return;
        throw Refuse(MissionErrors.PrefabMemberError(Capital(Owner(script, index)), record.Id) with
        {
            Target = RefOf(script, index),
        });
    }

    /// <summary>
    /// Records the delete of a member of a linked instance as a removed-member override (decision D676 c, the rule of
    /// Unity 2022.2 and later): the record gets the role, and the hash of the compiled lines without the member. The
    /// prefab does not change. The inverse gives the record back.
    /// </summary>
    private void MemberRemoved(EditDocument script, InstanceRecord record, string role)
    {
        var updated = record with { Removed = [.. record.Removed, role] };
        if (Editor.Prefab(record.Prefab)?.Template is { } template)
        {
            try
            {
                updated = updated with { LinesHash = LinesHashOf(PrefabCompiler.Compile(template, LinkedInstance.InputOf(updated, template))) };
            }
            catch (EditException)
            {
                // A compile that fails keeps the old hash. The instance then shows P01, and a compile gives the reason.
            }
        }
        Inverses.AddRange(RunInner(new RestoreInstanceRecord(new ScriptRef(script.Path), updated)));
        Changes.Add(new InstanceRef(script.Path, record.Id).ToString());
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
    /// <returns>The new record.</returns>
    /// <exception cref="EditException">The compile refuses. The message names the instance, the field and the
    /// reason.</exception>
    public InstanceRecord Compile(EditDocument script, InstanceRecord record, PrefabTemplate template)
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
            foreach (var section in compiled)
            {
                var at = MemberIndex(script, section.Type, section.Nickname);
                if (at >= 0)
                    Inverses.AddRange(RunInner(new ReplaceSectionBody(ItemOf(script, at), section.Entries)));
                else
                {
                    if (NameProblem(script, -1, section.Type, section.Nickname) is { } taken)
                        throw Refuse(taken with
                        {
                            Message = $"Prefab instance '{record.Id}' gives section '{section.Role}' the name " +
                                      $"'{section.Nickname}'. {taken.Message}",
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
    /// The records of the scripts of a rename of a trigger: a port that names the old name names the new name, and the
    /// hash of the compiled lines follows. So the rename of a trigger that a port starts makes no hand edit (rule
    /// P01).
    /// </summary>
    public void RenameInRecords(EditDocument script, string old, string name)
    {
        foreach (var record in Graph(script).Instances.ToList())
        {
            if (!record.Ports.Any(p => p.Trigger.Equals(old, StringComparison.OrdinalIgnoreCase)))
                continue;
            var updated = record with
            {
                Ports = record.Ports.Select(p => p.Trigger.Equals(old, StringComparison.OrdinalIgnoreCase) ? p with { Trigger = name } : p)
                    .ToList(),
            };
            if (Editor.Prefab(record.Prefab)?.Template is { } template)
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
        }
    }

    /// <summary>True for the template of a prefab in prefab mode, the script <c>prefab:mod:&lt;id&gt;</c>, and false for
    /// a mission script (section 5.7 of the M6b design).</summary>
    private static bool IsTemplate(EditDocument document) => MissionRefs.IsPrefab(document.Path);
}
