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
/// the Console gets each problem.
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
            Start(mod);
        ImGui.SameLine();
        if (Button("Cancel"))
            Close();
    }

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

    private void Start(Mod exported)
    {
        error = null;
        var chosen = ExportTarget.All.Where(t => targets.Contains(t.Id)).ToList();
        window.Settings.SetExport(exported.Folder, new ExportChoices([.. chosen.Select(t => t.Id)], dropIn, flmm, folder));
        window.Settings.Save();
        var reports = bases;
        var request = new ExportRequest
        {
            Mod = exported,
            Targets = chosen,
            BaseOf = target => BaseOf(target, reports),
            OutputFolder = folder,
            Forms = (dropIn ? ExportForms.DropIn : 0) | (flmm ? ExportForms.Flmm : 0),
            StringWriter = DllStringResourceWriter.Instance,
            Thn = ThornEvaluator.Instance,
        };
        running = Task.Run(() => ModExport.Run(request));
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
