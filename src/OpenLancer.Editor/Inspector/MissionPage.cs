using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a mission selection (section 10 of the M6a design): the header line with the info icon, the
/// read-only note, the completeness panel, the component sections, and the INI block in the raw INI editor. It draws
/// from the page model (<see cref="MissionPageModel"/>). In M6a-1, the Mission page has all its sections. The other
/// mission items show their problems and their INI block until M6a-2 gives each kind its page.
/// </summary>
internal sealed class MissionPage(EditorWindow window, PropertyGrid grid) : IDisposable
{
    // Sizes in unscaled points.
    private const float HeaderPaddingX = 10;
    private const float IniHeight = 220;

    private readonly RawIniEditor ini = new();
    private readonly Dictionary<SectionRef, RawText> iniTexts = [];
    private MissionRunResult? textsOf;

    public void Draw(MissionRef item)
    {
        var missions = window.Missions;
        if (missions.Result is not { } result)
        {
            PanelStates.Loading("Checking the mission scripts");
            return;
        }

        if (result.Of(item.Script) is not { } analysis)
        {
            PanelStates.Message("This mission script is not in the edit view.", "Select a mission in the Missions tab.");
            return;
        }

        var session = window.Session!;
        var model = item.Kind == MissionItemKind.Mission
            ? MissionPageModel.Mission(result, analysis, id => session.Name(id), CommFile, missions.ShowBaseGame,
                session.Edits.HidesOpenLancer)
            : MissionPageModel.Item(analysis, item, missions.ShowBaseGame);
        Header(model);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.BeginChild("##mission-page", Vector2.Zero);
        ImGui.PopStyleVar();
        if (visible)
        {
            var padding = MathF.Truncate(HeaderPaddingX * ImGuiHelper.Scale);
            ImGui.Indent(padding);
            ImGui.TextDisabled(MissionPageModel.ReadOnlyNote);
            if (model.Completeness is { } report)
            {
                CompletenessPanel.Draw("completeness", report, missions.ShowBaseGame, GoTo);
            }

            ImGui.Unindent(padding);
            foreach (var section in model.Sections)
            {
                Section(model, section);
            }

            Ini(result, analysis, model);
        }

        ImGui.EndChild();
    }

    /// <summary>The header line: the title, the kind as a dim label and the info icon at the right end.</summary>
    private static void Header(MissionPageModel model)
    {
        var padding = MathF.Truncate(HeaderPaddingX * ImGuiHelper.Scale);
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight() * 2;
        ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(EditorTheme.Section));
        ImGui.SetCursorScreenPos(min + new Vector2(padding, MathF.Truncate(ImGui.GetFrameHeight() / 2)));
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        ImGui.Text($"{EditorIcons.Route}  {model.Title}");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled(model.Kind);
        ImGui.SetCursorScreenPos(new Vector2(min.X + width - InfoIcon.Width - padding,
            min.Y + MathF.Truncate((height - ImGui.GetFrameHeight()) / 2)));
        InfoIcon.Draw("header", model.Help);
        ImGui.SetCursorScreenPos(min + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 1));
    }

    private void Section(MissionPageModel model, PageSection section)
    {
        if (!grid.BeginSection($"Mission.{section.Id}", EditorIcons.Bolt, section.Title, !section.Advanced, section.Help))
        {
            return;
        }

        foreach (var field in section.Fields)
        {
            if (field.Help != null)
            {
                grid.Help(field.Help);
            }

            if (field.Link is { } link)
            {
                if (grid.Link(field.Label, field.Value, field.Help ?? "Go to the item."))
                {
                    GoTo(link);
                }

                continue;
            }

            if (field.Severity is { } severity && !field.Muted)
            {
                grid.Row(field.Label, () => ImGui.TextColored(
                    severity == DiagnosticSeverity.Error ? EditorTheme.Error : EditorTheme.Warning, field.Value));
                continue;
            }

            grid.Text(field.Label, field.Value);
        }

        foreach (var note in section.Notes)
        {
            grid.NoteRow(note);
        }

        grid.EndSection();
    }

    /// <summary>The INI block of the section of the page, read-only, with the marks of the checks (section 10.1 item
    /// 7 of the M6a design). The Mission page also has a button that opens the whole script.</summary>
    private void Ini(MissionRunResult result, MissionAnalysis analysis, MissionPageModel model)
    {
        if (model.Ini is not { } target || analysis.Document.Find(target) is not { } section)
        {
            return;
        }

        if (!grid.BeginSection("Mission.ini", EditorIcons.Terminal, "INI", true,
                Data.Missions.MissionSchema.Retail.Help["rawIni"]))
        {
            return;
        }

        grid.EndSection();
        if (!ReferenceEquals(textsOf, result))
        {
            iniTexts.Clear();
            textsOf = result;
        }

        if (!iniTexts.TryGetValue(target, out var text))
        {
            iniTexts[target] = text = RawText.Canonical(target.Path, [section.Section]);
        }

        if (text.ReadOnlySections.TryGetValue(0, out var reason))
        {
            ImGui.TextDisabled(reason);
        }

        var problems = analysis.Problems.Where(p => Equals(p.Target, target)).ToList();
        var marks = IniLint.Marks(text, problems, window.Missions.ShowBaseGame);
        var height = MathF.Min(IniHeight * ImGuiHelper.Scale, (text.Lines.Count + 2) * ImGui.GetTextLineHeightWithSpacing());
        ini.Draw("##ini", text, marks, new Vector2(ImGui.GetContentRegionAvail().X, height));
        if (model.Kind == "Mission" && ImGui.Button("Open the whole script"))
        {
            window.Missions.OpenMission(analysis.Script.Path, show: true);
        }
    }

    /// <summary>Goes to the item of a link: a mission selection, or a section line of a problem.</summary>
    private void GoTo(object link)
    {
        switch (link)
        {
            case MissionRef item:
                window.Selection = item;
                break;
            case ValueTuple<SectionRef, int> (var target, var entry):
                window.Missions.GoTo(target, entry);
                break;
            case SectionRef target:
                window.Missions.GoTo(target, -1);
                break;
        }
    }

    /// <summary>The voices file of a comm line, from the comm line list of the edit view, or null.</summary>
    private string? CommFile(string line) =>
        window.Session?.Edits.Lists.List("comm_line")?.Find(line)?.Section?.Path is { } path
            ? Data.Missions.MissionFiles.Display(path)
            : null;

    /// <summary>Forgets the texts of the profile.</summary>
    public void Reset()
    {
        iniTexts.Clear();
        textsOf = null;
    }

    public void Dispose() => ini.Dispose();
}
