using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Export;

/// <summary>
/// Mod &gt; Export mod (milestone M11): the targets with their bases, the package forms and the output folder, as the
/// Build Settings window of Unity shows them. The export runs on a worker thread. The result replaces the buttons, and
/// the Console gets each problem. Export and play installs the export of the first checked retail target into the play
/// copy of its base. Then it starts the game, as Build And Run of Unity does (decision D326).
/// </summary>
internal sealed class ExportDialog(EditorWindow window) : ModalDialog
{
    /// <summary>The source of the Console lines of the export.</summary>
    public const string Source = "Export";

    private Mod? mod;
    private IReadOnlyList<GameReport> bases = [];
    private readonly HashSet<string> targets = new(StringComparer.Ordinal);
    private bool dropIn = true;
    private bool flmm = true;
    private string folder = "";
    private Task<ExportResult>? running;
    private ExportResult? result;
    private string? error;

    // The retail target whose export the game plays after the export, or null for Export alone.
    private ExportTarget? playTarget;

    protected override string Title => mod == null
        ? "Export mod###export"
        : $"Export mod: {mod.Manifest.Name} ({mod.Id}), version {mod.Manifest.Version}###export";

    /// <summary>Shows the dialog for a mod.</summary>
    /// <param name="profilePath">The profile file, for the default output folder.</param>
    /// <param name="reports">The checks of the linked games.</param>
    public void Show(Mod exported, string profilePath, IReadOnlyList<GameReport> reports)
    {
        mod = exported;
        bases = reports;
        result = null;
        error = null;
        running = null;
        playTarget = null;
        var saved = window.Settings.ExportOf(exported.Folder);
        targets.Clear();
        foreach (var target in ExportTarget.All.Where(t => Available(t) && t.Refusal(exported.Manifest) == null))
        {
            if (saved == null || saved.Targets.Contains(target.Id))
                targets.Add(target.Id);
        }
        (dropIn, flmm) = saved == null ? (true, true) : (saved.DropIn, saved.Flmm);
        folder = saved?.Folder is { Length: > 0 } path ? path : ModExport.DefaultFolder(profilePath, exported.Id);
        Open();
    }

    /// <summary>The check of the linked game that is the base of a retail target, or null.</summary>
    internal static GameReport? BaseOf(ExportTarget target, IReadOnlyList<GameReport> reports) =>
        target.IsRetail ? reports.FirstOrDefault(r => target.BaseRefusal(r) == null) : null;

    private bool Available(ExportTarget target) => !target.IsRetail || BaseOf(target, bases) != null;

    protected override void Body()
    {
        if (mod == null)
        {
            Close();
            return;
        }
        if (running != null)
        {
            if (!running.IsCompleted)
            {
                ImGui.Text("Exporting...");
                return;
            }
            Finish(running);
            running = null;
        }
        if (result != null)
        {
            Result(result);
            return;
        }

        Targets(mod);
        ImGui.Spacing();
        ImGui.Text("Packages");
        ImGui.SameLine();
        ImGui.Checkbox("Drop-in zip for Nexus", ref dropIn);
        ImGui.SameLine();
        ImGui.Checkbox(FlmmPackage.Checked ? "FLMM package" : "FLMM package (preview)", ref flmm);
        if (!FlmmPackage.Checked)
            ImGui.SetItemTooltip("OpenLancer did not check this package with FLMM yet. Check it with FLMM before you " +
                                 "publish it.");
        ImGui.Spacing();
        ImGui.Text($"Output folder {folder}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Change..."))
            FolderActions.Choose(window, path => folder = path);
        Problem(error);
        ImGui.Spacing();

        var retail = targets.Any(id => ExportTarget.Find(id)!.IsRetail);
        var ready = targets.Count > 0 && (!retail || dropIn || flmm);
        if (Button("Export", primary: true, enabled: ready))
            Start(mod, play: null);
        // Export and play needs a checked retail target whose base has a game program.
        if (PlayTargetOf() is { } play)
        {
            ImGui.SameLine();
            if (Button("Export and play", enabled: ready && !window.GamePlay.Busy))
                Start(mod, play);
            ImGui.SetItemTooltip(window.GamePlay.Busy
                ? "A game or the test server of a play copy runs. Stop it first."
                : $"Export, then install the export for {play.Name} into the play copy of {BaseOf(play, bases)!.Root} " +
                  "and start the game. The play copy then holds this mod alone.");
        }
        ImGui.SameLine();
        if (Button("Cancel"))
            Close();
    }

    /// <summary>The first checked retail target whose base has a game program, or null.</summary>
    private ExportTarget? PlayTargetOf() =>
        ExportTarget.All.FirstOrDefault(t => t.IsRetail && targets.Contains(t.Id) && BaseOf(t, bases) is { ExePath: not null });

    /// <summary>The check box of each target, with its base or the reason why it has none.</summary>
    private void Targets(Mod exported)
    {
        if (!ImGui.BeginTable("##targets", 2, ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn("Targets");
        ImGui.TableSetupColumn("Base");
        ImGui.TableHeadersRow();
        foreach (var target in ExportTarget.All)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var reason = target.Refusal(exported.Manifest) ?? target.BaseRefusal(BaseOf(target, bases));
            var on = targets.Contains(target.Id) && reason == null;
            ImGui.BeginDisabled(reason != null);
            if (ImGui.Checkbox(target.Name, ref on))
            {
                if (on)
                    targets.Add(target.Id);
                else
                    targets.Remove(target.Id);
            }
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            if (reason != null)
                ImGui.TextColored(EditorTheme.Warning, reason);
            else
                ImGui.Text(target.IsRetail ? BaseText(BaseOf(target, bases)!) : "the mod folder");
        }
        ImGui.EndTable();
    }

    private static string BaseText(GameReport report) =>
        report.Kind == GameKind.Vanilla ? $"{report.Root} ({report.Name})" : report.Root!;

    private void Start(Mod exported, ExportTarget? play)
    {
        error = null;
        playTarget = play;
        var chosen = ExportTarget.All.Where(t => targets.Contains(t.Id)).ToList();
        window.Settings.SetExport(exported.Folder, new ExportChoices([.. chosen.Select(t => t.Id)], dropIn, flmm, folder));
        window.Settings.Save();
        var request = RequestOf(exported, chosen, bases, folder, (dropIn ? ExportForms.DropIn : 0) | (flmm ? ExportForms.Flmm : 0),
            out var publishProblems);
        foreach (var problem in publishProblems)
            window.Log.Add(ConsoleSeverity.Warning, Source, $"{exported.Folder}: {problem}");
        running = Task.Run(() => ModExport.Run(request));
    }

    /// <summary>The request of an export. The texts of the packages come from the publish.json of the mod (milestone
    /// M11c).</summary>
    /// <param name="publishProblems">The problems of the read of publish.json. The texts leave out a field with a
    /// problem, so the Console shows them.</param>
    internal static ExportRequest RequestOf(Mod exported, IReadOnlyList<ExportTarget> chosen, IReadOnlyList<GameReport> reports,
        string folder, ExportForms forms, out IReadOnlyList<string> publishProblems)
    {
        var publish = PublishSettings.Load(exported.Folder);
        publishProblems = publish.Problems;
        return new ExportRequest
        {
            Mod = exported,
            Targets = chosen,
            BaseOf = target => BaseOf(target, reports),
            OutputFolder = folder,
            Forms = forms,
            Publish = publish.Settings,
            StringWriter = DllStringResourceWriter.Instance,
            Thn = ThornEvaluator.Instance,
        };
    }

    /// <summary>Takes the result of the export, and writes its problems and its summary to the Console.</summary>
    private void Finish(Task<ExportResult> task)
    {
        if (task.Exception?.GetBaseException() is { } failure)
        {
            error = ErrorText.Of(failure);
            window.Log.Add(ConsoleSeverity.Error, Source, error);
            return;
        }
        result = task.Result;
        foreach (var problem in result.Problems)
            window.Log.Add(problem);
        var written = result.Packages.Count(p => p.Path != null);
        window.Log.Add(result.Succeeded ? ConsoleSeverity.Info : ConsoleSeverity.Error, Source, result.Succeeded
            ? $"The export wrote {written} packages to {result.OutputFolder}."
            : ModExport.Stopped);
        if (playTarget is { } play && mod != null)
        {
            if (result.Builds.TryGetValue(play, out var files) && result.Packages.Where(p => p.Target == play).All(p => p.Path != null) &&
                BaseOf(play, bases) is { } game)
                window.GamePlay.PlayExport(game, files, mod.Id);
            else
                window.Log.Add(ConsoleSeverity.Error, Source, $"Play did not start. The export for {play.Name} did not complete.");
        }
    }

    /// <summary>The result in place of the buttons: a line for each package, the number of warnings and Open
    /// folder.</summary>
    private void Result(ExportResult done)
    {
        foreach (var package in done.Packages)
        {
            if (package.Path != null)
                ImGui.Text($"{EditorIcons.CheckCircle}  {package.FileName}");
            else
                ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.TimesCircle}  {package.FileName}: refused. See the Console.");
        }
        var warnings = done.Problems.Count(p => p.Severity == DiagnosticSeverity.Warning);
        var errors = done.Problems.Count(p => p.Severity == DiagnosticSeverity.Error);
        ImGui.Text($"{errors} errors, {warnings} warnings. The Console shows each problem.");
        ImGui.Spacing();
        if (Button("Open folder", enabled: Directory.Exists(done.OutputFolder)))
            FolderActions.Open(window, done.OutputFolder);
        ImGui.SameLine();
        if (Button("Close", primary: true))
            Close();
    }
}
