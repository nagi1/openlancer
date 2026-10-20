using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

public sealed partial class MissionEditor
{
    // The assets of the lower mods by the folder of the mod and the id. A lower mod does not change while the session
    // is open, so the editor reads each file once.
    private readonly Dictionary<(string Folder, string Id), PrefabAsset?> lowerAssets = new();

    // The last linked analysis of each script, with its inputs (see Linked).
    private readonly Dictionary<string, (MissionAnalysis Source, GraphFile Graph, string?[] Texts, MissionAnalysis Linked)> links =
        new(VfsPath.Comparer);

    /// <summary>
    /// The prefab of a reference (section 5.2 of the M6b design): a built-in prefab, or the asset of a mod. A <c>mod:</c>
    /// prefab is the asset of the active mod first, then of the lower mods in reverse load order. Null when no mod has
    /// it.
    /// </summary>
    public PrefabAsset? Prefab(PrefabRef prefab) => Prefab(prefab, null);

    /// <param name="modText">Gives the text of an editor file of the active mod, or null for the text of W now.</param>
    internal PrefabAsset? Prefab(PrefabRef prefab, Func<string, string?>? modText)
    {
        if (prefab.Builtin)
            return BuiltinPrefabs.Text(prefab.Id) is { } builtin ? Builtin(prefab.Id, builtin) : null;
        if (!TemplateReader.IsId(prefab.Id))
            return null;
        if (session.ActiveMod is { } active && (modText ?? ModText)(PrefabAsset.PathOf(prefab.Id)) is { } own)
            return new PrefabAsset(PrefabRef.ModOf(prefab.Id), own, active.Id, Active: true);
        foreach (var mod in session.LowerMods.Reverse())
        {
            if (LowerAsset(mod, prefab.Id) is { } lower)
                return lower;
        }
        return null;
    }

    /// <summary>The prefab of the text of an instance record, or null.</summary>
    internal PrefabAsset? Prefab(string recordText, Func<string, string?>? modText = null) =>
        PrefabRef.TryParse(recordText) is { } prefab ? Prefab(prefab, modText) : null;

    private readonly Dictionary<string, PrefabAsset> builtins = new(StringComparer.Ordinal);

    private PrefabAsset Builtin(string id, string text)
    {
        if (!builtins.TryGetValue(id, out var asset))
            builtins[id] = asset = new PrefabAsset(PrefabRef.BuiltinOf(id), text, null, Active: false);
        return asset;
    }

    /// <summary>
    /// The prefabs of the library and of the Assets panel (section 8.3 of the M6b design): the built-in prefabs, then the
    /// assets of the active mod, then the assets of the lower mods that the active mod does not hide. A lower mod later
    /// in the load order hides an asset with the same id of an earlier one.
    /// </summary>
    public IReadOnlyList<PrefabAsset> Prefabs()
    {
        var result = BuiltinPrefabs.Ids.Select(id => Builtin(id, BuiltinPrefabs.Text(id)!)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in session.ModFilesIn(PrefabAsset.Folder))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (seen.Add(id) && Prefab(PrefabRef.ModOf(id)) is { Active: true } asset)
                result.Add(asset);
        }
        foreach (var mod in session.LowerMods.Reverse())
        {
            foreach (var id in LowerIds(mod).Where(seen.Add))
            {
                if (LowerAsset(mod, id) is { } asset)
                    result.Add(asset);
            }
        }
        return result;
    }

    /// <summary>
    /// A free file name of a new prefab asset (section 5.9 of the M6b design): the wanted name with only lowercase
    /// letters, digits, '_' and '-', and a number after it when a prefab of the library has the name. "Make variant" and
    /// "Save as prefab" propose it.
    /// </summary>
    public string FreePrefabId(string wanted)
    {
        var stem = new string(wanted.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        if (stem.Length == 0)
            stem = "prefab";
        stem = stem.Length > 40 ? stem[..40] : stem;
        var taken = Prefabs().Select(p => p.Ref.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem))
            return stem;
        for (var n = 2;; n++)
        {
            var made = $"{stem}{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (!taken.Contains(made))
                return made;
        }
    }

    /// <summary>A free name of a new prefab in the library: the wanted name, and a number after it when a prefab of the
    /// library has the name. So two variants of one prefab do not show the same name (UI rule 9).</summary>
    public string FreePrefabName(string wanted)
    {
        var stem = wanted.Trim();
        var taken = Prefabs().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem))
            return stem;
        for (var n = 2;; n++)
        {
            var made = $"{stem} {n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (!taken.Contains(made))
                return made;
        }
    }

    /// <summary>The ids of the assets of a lower mod: the files of its folder <c>prefabs\missions</c>.</summary>
    private static IEnumerable<string> LowerIds(Mods.Mod mod)
    {
        var folder = Path.Combine(mod.Folder, "prefabs", "missions");
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.ini").Select(Path.GetFileNameWithoutExtension).OfType<string>()
                    .Where(TemplateReader.IsId).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The asset of a lower mod, or null. The editor reads the file once.</summary>
    private PrefabAsset? LowerAsset(Mods.Mod mod, string id)
    {
        if (lowerAssets.TryGetValue((mod.Folder, id), out var known))
            return known;
        PrefabAsset? asset = null;
        try
        {
            var file = Path.Combine(mod.Folder, "prefabs", "missions", id + ".ini");
            if (File.Exists(file))
                asset = new PrefabAsset(PrefabRef.ModOf(id), File.ReadAllText(file).Replace("\r\n", "\n"), mod.Id, Active: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            asset = null;
        }
        lowerAssets[(mod.Folder, id)] = asset;
        return asset;
    }

    /// <summary>The text of an editor file of the active mod in W, or null.</summary>
    private string? ModText(string relativePath)
    {
        try
        {
            return session.ModFileText(relativePath);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The analysis of a script with its linked prefab instances and the warnings P01 to P03 (section 5 of the M6b
    /// design): the records of the graph file of the active mod and their prefabs now. The same inputs give the same
    /// analysis, so a view that keys on the analysis builds again only after a change.
    /// </summary>
    public MissionAnalysis Linked(MissionAnalysis analysis) => Linked(analysis, null);

    /// <param name="modText">Gives the text of an editor file of the active mod, or null for W now. The commit check of
    /// a batch gives the texts before the batch.</param>
    internal MissionAnalysis Linked(MissionAnalysis analysis, Func<string, string?>? modText)
    {
        analysis = analysis.Matched ?? analysis;
        var path = analysis.Script.Path;
        if (modText != null)
            return PrefabLinks.Apply(analysis, GraphFile.Read(modText(GraphFile.PathOf(path)), out _),
                text => Prefab(text, modText), session.ActiveMod?.Id);
        var graph = Graph(path);
        if (graph.Instances.Count == 0)
            return analysis;
        var assets = graph.Instances.Select(i => Prefab(i.Prefab)).ToList();
        var texts = assets.Select(a => a?.Text).ToArray();
        if (links.TryGetValue(path, out var known) && ReferenceEquals(known.Source, analysis) &&
            ReferenceEquals(known.Graph, graph) && known.Texts.SequenceEqual(texts, ReferenceEqualityComparer.Instance))
            return known.Linked;
        var byRecord = graph.Instances.Select((i, n) => (i.Prefab, Asset: assets[n]))
            .GroupBy(p => p.Prefab).ToDictionary(g => g.Key, g => g.First().Asset);
        var linked = PrefabLinks.Apply(analysis, graph, text => byRecord.GetValueOrDefault(text), session.ActiveMod?.Id);
        links[path] = (analysis, graph, texts, linked);
        return linked;
    }

    // The analysis of the role form of each template text.
    private readonly Dictionary<string, (string Text, MissionAnalysis Analysis)> templateAnalyses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A prefab in prefab mode (section 8.6 of the M6b design): its template, the analysis of its template sections in
    /// the role form, the count of its linked instances in the active mod, and why it cannot change. Null when no mod
    /// has the prefab. The same text gives the same analysis, so a view that keys on it builds again only after a
    /// change.
    /// </summary>
    public PrefabModeModel? PrefabMode(PrefabRef prefab)
    {
        if (Prefab(prefab) is not { } asset)
            return null;
        var template = asset.Template;
        MissionAnalysis? analysis = null;
        if (template != null)
        {
            var key = prefab.Script;
            if (!templateAnalyses.TryGetValue(key, out var known) || !ReferenceEquals(known.Text, asset.Text))
            {
                known = (asset.Text, TemplateAnalysis(prefab, template, asset.ModId));
                templateAnalyses[key] = known;
            }
            analysis = known.Analysis;
        }
        var instances = prefab.Builtin ? [] : InstancesOf(prefab).ToList();
        var reason = prefab.Builtin ? PrefabModeModel.BuiltInBanner
            : !asset.Active ? PrefabModeModel.LowerModBanner(asset.ModId ?? "")
            : session.ReadOnlyReason;
        return new PrefabModeModel(asset, template, analysis, instances.Count,
            instances.Select(i => i.Script).Distinct(VfsPath.Comparer).Count(), reason);
    }

    /// <summary>The analysis of the template sections of a prefab in the role form. The problems of a line with a slot
    /// go, because the compile fills the slot.</summary>
    private MissionAnalysis TemplateAnalysis(PrefabRef prefab, PrefabTemplate template, string? mod)
    {
        var inputs = world.Inputs();
        var sections = OpenLancer.Data.Missions.Prefabs.PrefabMode.RoleForm(template)
            .Where(s => !OpenLancer.Data.Missions.Prefabs.PrefabMode.IsHeader(s.Name)).ToList();
        var document = MissionDocument.Of(prefab.Script, sections, false);
        var index = MissionIndex.Build(document, inputs.Schema, MissionWorld.Empty, inputs.Coverage);
        bool HasSlot(Diagnostic problem) =>
            problem.Target is { } target && document.Find(target) is { } section && problem.EntryIndex >= 0 &&
            problem.EntryIndex < section.Section.Count && section.Section[problem.EntryIndex] is var entry &&
            (PrefabSlot.KeyField(entry.Name) != null || MissionValues.TextsOf(entry).Any(t => PrefabSlot.Of(t).Count > 0));
        var problems = MissionChecks.Run(index, inputs.Options).Where(p => !HasSlot(p) && p.Rule is not ("G01" or "G02" or "G03"))
            .Select(p => p with { Origin = mod != null ? MessageOrigin.Mod(mod, ModEvidence.File) : MessageOrigin.OpenLancer })
            .ToList();
        var script = new MissionScriptInfo(prefab.Script, null, MissionScriptState.Changed, mod, false);
        return new MissionAnalysis(script, [], document, index, problems, null)
        {
            Prefabs = new MissionPrefabResult([], new Dictionary<MissionTrigger, MissionPrefabInstance>()),
            Acts = MissionActs.Propose(index),
            HasFileBytes = false,
        };
    }

    /// <summary>The linked instances of the scripts of the active mod whose records name a prefab, by script. The
    /// propagation of a prefab edit compiles them (section 5.7 of the M6b design).</summary>
    internal IEnumerable<(string Script, InstanceRecord Record)> InstancesOf(PrefabRef prefab)
    {
        foreach (var script in ScriptPaths())
        {
            foreach (var record in Graph(script).Instances)
            {
                if (PrefabRef.TryParse(record.Prefab) is { } named && named.Equals(prefab))
                    yield return (script, record);
            }
        }
    }
}

/// <summary>
/// A prefab in prefab mode (section 8.6 of the M6b design): its asset, its template, the analysis of its template
/// sections in the role form for the graph and the pages, its linked instances in the active mod, and why it cannot
/// change.
/// </summary>
/// <param name="Analysis">The analysis of the template sections, or null when the file of the prefab has a problem. Its
/// script is the reference of the prefab, for example <c>prefab:mod:ol_gate</c>.</param>
/// <param name="Instances">The number of linked instances of the prefab in the scripts of the active mod.</param>
/// <param name="Scripts">The number of scripts with such an instance.</param>
/// <param name="ReadOnlyReason">Why the prefab cannot change, or null: a built-in prefab or a prefab of a lower
/// mod.</param>
public sealed record PrefabModeModel(PrefabAsset Asset, PrefabTemplate? Template, MissionAnalysis? Analysis, int Instances,
    int Scripts, string? ReadOnlyReason)
{
    /// <summary>The banner of a built-in prefab (section 8.11 of the M6b design).</summary>
    public const string BuiltInBanner = "This is a built-in prefab. It cannot change. Make a variant to change it.";

    /// <summary>The banner of a prefab of a lower mod.</summary>
    public static string LowerModBanner(string mod) => $"This prefab belongs to mod {mod}. It cannot change. Make a variant to change it.";

    /// <summary>The text of the bar of prefab mode.</summary>
    public string BarText(string? mod) =>
        $"Changes here change {MissionChecks.Count(Instances, "instance", "instances")} in " +
        $"{MissionChecks.Count(Scripts, "mission", "missions")} of mod {mod}.";
}
