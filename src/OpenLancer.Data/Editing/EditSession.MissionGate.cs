using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    /// <summary>The lower mods merged on the game of each checked target, by the id of the target.</summary>
    private readonly Dictionary<string, TargetBase> targetBases = new(StringComparer.Ordinal);

    /// <summary>The targets whose note <see cref="TakeTargetNotes"/> gave.</summary>
    private readonly HashSet<string> notedTargets = new(StringComparer.Ordinal);

    /// <summary>
    /// Gives the linked game of a retail target by the id of the target (see <see cref="ModTargets"/>), or null when no
    /// game is linked for it. The save gate merges the mission patches of the active mod on each such game (section
    /// 6.12 of the M6b design). The editor gives the games of its game links. Without it, the gate checks no target.
    /// </summary>
    public Func<string, GameBase?>? TargetGame { get; set; }

    /// <summary>The Console note of a retail target that the save gate cannot check.</summary>
    public static string NoTargetGameNote(ExportTarget target) =>
        $"The editor cannot check {target.Name}, because no game is linked for it.";

    /// <summary>
    /// The notes of the retail targets of the active mod that the save gate cannot check, because no game is linked
    /// for them (section 6.12 of the M6b design). There is a note only when the active mod changes a mission file, and
    /// each target gives its note once in a session. The editor writes each note as an info line in the Console.
    /// </summary>
    public IReadOnlyList<string> TakeTargetNotes()
    {
        if (ActiveMod is not { } active || !HasRetailTarget(active.Manifest) || !MissionPatchPaths(active).Any())
            return [];
        return RetailTargets(active.Manifest)
            .Where(t => TargetGame?.Invoke(t.Id) == null && notedTargets.Add(t.Id))
            .Select(NoTargetGameNote).ToList();
    }

    /// <summary>
    /// The checks of the mission patches of the active mod on the game of each retail target (section 6.12 of the M6b
    /// design). The check merges the lower mods of the profile on the game first, as Play in the game does. Then it
    /// merges each mission patch of W:
    /// <list type="bullet">
    /// <item>A section that the patch changes and the game does not have: T01, an error (decision D415).</item>
    /// <item>A section whose hash differs from <c>ol.base</c>: T02, a warning (decision D429).</item>
    /// <item>Another merge error, for example of <c>ol.before</c>: T01 with the merge message.</item>
    /// </list>
    /// </summary>
    private List<Diagnostic> MissionTargetProblems(Mod active)
    {
        var problems = new List<Diagnostic>();
        if (!HasRetailTarget(active.Manifest))
            return problems;
        var patches = MissionPatches(active);
        if (patches.Count == 0)
            return problems;
        foreach (var target in RetailTargets(active.Manifest))
        {
            if (TargetBaseOf(target) is not { } lower)
                continue;
            foreach (var (path, sections) in patches)
                problems.AddRange(lower.Check(path, sections, active.Id, target));
        }
        return problems;
    }

    private static IEnumerable<ExportTarget> RetailTargets(ModManifest manifest) =>
        manifest.Targets.Select(ExportTarget.Find).OfType<ExportTarget>().Where(t => t.IsRetail);

    /// <summary>The mission files that the active mod changes with a patch: the scripts and their ship build
    /// files.</summary>
    private IEnumerable<string> MissionPatchPaths(Mod active) =>
        touched.Concat(active.Patches.Keys).Select(p => VfsPath.Normalize(p) ?? p).Distinct(VfsPath.Comparer)
            .Where(p => !active.Files.ContainsKey(p) && IsGuardedMissionFile(p));

    /// <summary>The sections of the patch of W of each mission file that the active mod changes with a patch.</summary>
    private List<(string Path, IReadOnlyList<Section> Sections)> MissionPatches(Mod active)
    {
        var result = new List<(string, IReadOnlyList<Section>)>();
        foreach (var path in MissionPatchPaths(active))
        {
            if (OpenDocument(path) is not { IsWholeFile: false } document)
                continue;
            // A patch that the writer cannot write has its own errors in the save.
            var patch = document.Write();
            if (patch is { Succeeded: true, Sections.Count: > 0 })
                result.Add((path, IniDocument.Parse(path, patch.ToBytes()).Sections));
        }
        return result;
    }

    /// <summary>The lower mods merged on the game of a target, or null when no game is linked for it. The merge runs
    /// once for each game in a session.</summary>
    private TargetBase? TargetBaseOf(ExportTarget target)
    {
        if (TargetGame?.Invoke(target.Id) is not { } game)
            return null;
        if (targetBases.TryGetValue(target.Id, out var known) && known.Game == game)
            return known;
        // The profile base gives the lower view of the session.
        var view = game.Layout == GameLayout.Standard && SameFolder(game.Root, Profile.BaseFolder)
            ? lowerView
            : ModOverlay.Build(game, lower, stringWriter, thn, OverlayOptions.Default with { KeepDocuments = true });
        return targetBases[target.Id] = new TargetBase(game, view);
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The lower mods of the profile merged on the game of a target. It keeps each file that a check
    /// read.</summary>
    private sealed class TargetBase(GameBase game, ModOverlay view)
    {
        private readonly Dictionary<string, IniDocument> files = new(VfsPath.Comparer);

        public GameBase Game => game;

        /// <summary>The problems of the merge of a patch on the file of the target.</summary>
        public IEnumerable<Diagnostic> Check(string path, IReadOnlyList<Section> patch, string modId, ExportTarget target)
        {
            if (!files.TryGetValue(path, out var file))
                files[path] = file = view.Document(path) ?? IniDocument.Parse(path, []);
            // The merge puts new sections into the list and never changes a section in place, so the kept file stays.
            var document = file.WithSections(file.Sections);
            var diagnostics = new List<Diagnostic>();
            var context = new PatchContext(modId, path, path, null, new PatchLedger(), diagnostics, keepPlaceholders: true);
            IniPatcher.Apply(document, patch, context);

            var missing = context.NoTargets.Select(n => n.Error).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var (section, _) in context.NoTargets)
                yield return Problem(DiagnosticSeverity.Error, "T01", path, section, target,
                    $"{section} is not in {path} of {target.Name}. The merge of this patch fails on {target.Name}. Remove " +
                    $"the edit, or remove {target.Name} from the targets of the mod.");
            foreach (var error in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && !missing.Contains(d)))
                yield return Problem(DiagnosticSeverity.Error, "T01", path, null, target,
                    $"The merge of this patch fails on {target.Name}: {error.Message}");
            foreach (var section in context.ChangedBases)
                yield return Problem(DiagnosticSeverity.Warning, "T02", path, section, target,
                    $"{section} of {path} on {target.Name} differs from the base of your edit. The changes of " +
                    $"{target.Name} in this section go away. Compare the two, then keep your lines or use the base of " +
                    $"{target.Name}.");
        }

        private static Diagnostic Problem(DiagnosticSeverity severity, string rule, string path, SectionIdentity? section,
            ExportTarget target, string message) =>
            new(severity, path, message)
            {
                Target = section == null ? null : new SectionRef(path, section),
                Rule = rule,
                Subject = $"{rule}|{target.Id}|{path.ToLowerInvariant()}|{section}",
            };
    }
}
