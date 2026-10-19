using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The origin rules of the console filter design: the key rule of a problem of a section (section 7.1), and the section
/// rule that names the mod of an engine line (section 3.5).
/// </summary>
public sealed partial class EditSession
{
    /// <summary>The text after a save gate error that becomes a warning because the base game has it (decision
    /// D508).</summary>
    public const string BaseGameSaveSuffix = "The base game has the same problem, so the save continues.";

    // The merged files of the edit view that the section rule parsed, for the edit view that it read them from.
    private (ModOverlay View, FileSystem Files, Dictionary<string, (IReadOnlyList<Section> Sections, SectionIdentity[] Ids)?> Parsed)?
        mergedFiles;

    /// <summary>
    /// True when a problem of a section is a base-game problem (the key rule, decision D508 and D512): the problem names
    /// a key, the key has the origin base (so the active mod does not set or remove it), no lower mod changes the
    /// section, and the same check of the base version of the section gives the same problem for the key. Only the
    /// field checks (<see cref="Check(SectionRef)"/>) and the list check of a missing required key use the rule. Each
    /// other problem reads other sections, so it is never a base-game problem by this rule.
    /// </summary>
    /// <remarks>
    /// The field checks read only the lines of the key and the schema. The check of a missing required key reads only
    /// the section. So the result in the base version equals the result in B. A mod that writes or removes the key gives
    /// the key the origin of the active mod, and a mod that adds the key that makes the key required changes the base
    /// result.
    /// </remarks>
    public bool IsBaseGameProblem(Diagnostic problem)
    {
        if (problem is not { Target: { } target, Key: not null } || !TryFind(target, out var document, out var index))
            return false;
        return IsBaseGameProblem(target, document, index, problem, null);
    }

    /// <param name="listProblem">The list problem of <paramref name="problem"/>, or null when the caller does not know
    /// it.</param>
    private bool IsBaseGameProblem(SectionRef target, EditDocument document, int index, Diagnostic problem,
        ListProblem? listProblem)
    {
        if (problem.Key is not { } key || Origin(target, key).Origin != ValueOrigin.Base)
            return false;
        var id = document.IdentityAt(index);
        if (document.BaseIdOf(id) is not { } baseId || document.BaseSection(id) is not { } lower ||
            lowerView.Ledger.ModsThatChangeSection(document.Path, baseId).Count > 0)
            return false;
        if (SchemaOf(target) is { } schema &&
            SectionChecks.Check(schema, lower, target, document.IsBinary).Any(p => SameProblem(p, problem)))
            return true;
        listProblem ??= Lists.Problems(target).FirstOrDefault(p => SameProblem(p.Diagnostic, problem));
        return listProblem is { Kind: ListProblemKind.RequiredMissing, Rule: { } rule } &&
               ListChecks.MissesRequired(lower, key, rule, Schema(target, key) is not { Multiline: false });
    }

    /// <summary>The same key, kind and message. The severity can differ: a check of a section that no mod changes gives
    /// a warning where a check of a changed section gives an error.</summary>
    private static bool SameProblem(Diagnostic a, Diagnostic b) =>
        string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase) && a.Message == b.Message;

    /// <summary>The warning that takes the place of a save gate error of the base game (decision D508).</summary>
    private static Diagnostic BaseGameWarning(Diagnostic error) => error with
    {
        Severity = DiagnosticSeverity.Warning,
        Message = $"{error.Message} {BaseGameSaveSuffix}",
        Origin = MessageOrigin.BaseGame,
    };

    /// <summary>
    /// The origin of a problem of a save (section 3.6 of the console filter design). A problem of the key rule has the
    /// origin base game. A problem of a section in a key that a lower mod changes has that mod (decision D518). Each
    /// other problem about a section or a cutscene has the active mod, because the save gate checks only what the
    /// active mod causes. A problem without a place is about the work of OpenLancer, for example a file that does not
    /// move into place.
    /// </summary>
    public MessageOrigin SaveProblemOrigin(Diagnostic problem)
    {
        if (problem.Origin is { } known)
            return known;
        if (ActiveMod is not { } active || problem.Target == null && problem.Cutscene == null)
            return MessageOrigin.OpenLancer;
        if (problem is { Target: { } target, Key: { } key } && TryFind(target, out _, out _) &&
            Origin(target, key) is (ValueOrigin.LowerMod, { } lowerMod))
            return MessageOrigin.Mod(lowerMod, ModEvidence.Section);
        return MessageOrigin.Mod(active.Id, ModEvidence.ActiveMod);
    }

    /// <summary>
    /// The origin of a problem of the merge at the open (section 3.6 of the console filter design): the mod whose folder
    /// holds the file of the problem, else OpenLancer.
    /// </summary>
    public MessageOrigin MergeProblemOrigin(Diagnostic problem)
    {
        foreach (var mod in EditView.Mods.Reverse())
        {
            if (Path.IsPathRooted(problem.Source) && PathRules.IsInside(problem.Source, mod.Folder))
                return MessageOrigin.Mod(mod.Id, ModEvidence.File);
        }
        return MessageOrigin.OpenLancer;
    }

    /// <summary>
    /// The section of the edit view that holds a line of a file, as the engine read the merged file, or null when the
    /// edit view has no such file or the line is before the first section.
    /// </summary>
    /// <param name="path">The path of the file as the engine wrote it, for example <c>EXE\..\data\x.ini</c>.</param>
    internal SectionRef? SectionAtLine(string path, int line)
    {
        if (VfsPath.Normalize(path) is not { } normal || Merged(normal) is not { } merged)
            return null;
        var found = -1;
        for (var i = 0; i < merged.Sections.Count && merged.Sections[i].Line <= line; i++)
            found = i;
        return found < 0 ? null : new SectionRef(normal, merged.Ids[found]);
    }

    /// <summary>The section of the item that a line of a component names: a <c>[Ship]</c> of the ship files or a
    /// <c>[Pilot]</c> of the pilot files. Null when the edit view has no such section.</summary>
    internal SectionRef? SubjectSection(EngineLineSubject kind, string nickname) => kind switch
    {
        EngineLineSubject.Ship => Find(ItemFileKind.Ships, nickname),
        _ => Lists.List("pilot")?.Find(nickname)?.Section,
    };

    /// <summary>True when a mod of the edit view changes the section: it changes its lines, adds, deletes or renames
    /// it, or replaces its file.</summary>
    internal bool ModChanges(SectionRef section) => EditView.Ledger.ModsThatChangeSection(section.Path, section.Id).Count > 0;

    /// <summary>
    /// The mod that causes an engine line, by the section rule (section 3.5 of the console filter design): the last mod
    /// that changes the section of the line, else the last mod that changes its file, else the only mod of the profile
    /// that changes game files, else "a mod".
    /// </summary>
    /// <param name="section">The section of the line, or null.</param>
    /// <param name="path">The path of the file of the line as the engine wrote it, or null.</param>
    internal MessageOrigin ModOfLine(SectionRef? section, string? path)
    {
        var ledger = EditView.Ledger;
        if (section != null && ledger.ModsThatChangeSection(section.Path, section.Id) is [.., var last])
            return MessageOrigin.Mod(last, ModEvidence.Section);
        var file = section?.Path ?? (path == null ? null : VfsPath.Normalize(path));
        if (file != null && ledger.ModsThatChangeFile(file) is [.., var owner])
            return MessageOrigin.Mod(owner, ModEvidence.File);
        return EditView.Mods.Where(m => m.Files.Count > 0 || m.Patches.Count > 0).ToList() is [var only]
            ? MessageOrigin.Mod(only.Id, ModEvidence.OnlyMod)
            : MessageOrigin.SomeMod;
    }

    /// <summary>The sections of a merged file of the edit view with their identities, read at the first use for each
    /// edit view. Null when the edit view has no such INI file.</summary>
    private (IReadOnlyList<Section> Sections, SectionIdentity[] Ids)? Merged(string path)
    {
        if (mergedFiles is not { } cache || !ReferenceEquals(cache.View, EditView))
        {
            cache = (EditView, EditView.CreateFileSystem(), new(VfsPath.Comparer));
            mergedFiles = cache;
        }

        if (cache.Parsed.TryGetValue(path, out var known))
            return known;
        (IReadOnlyList<Section>, SectionIdentity[])? result = null;
        try
        {
            if (path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && cache.Files.FileExists(path))
            {
                var sections = IniDocument.Parse(path, cache.Files.ReadAllBytes(path)).Sections;
                result = (sections, SectionIdentity.Identify(sections));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The section rule then finds no section, and it names the mod of the file.
        }
        cache.Parsed[path] = result;
        return result;
    }
}
