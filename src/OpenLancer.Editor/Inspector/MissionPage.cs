using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a mission selection (section 10 of the M6a design). From the top, it has the header line with
/// the glyph of the kind and the info icon, the read-only note and the completeness panel. Then come the component
/// sections and the INI block in the raw INI editor. It draws from the page model (<see cref="MissionPageModel"/>). In
/// M6a-1, the Mission page has all its sections. The other mission items show their problems and their INI block until
/// M6a-2 gives each kind its page.
/// </summary>
/// <remarks>The page keeps its model and the marks of its INI block until the run of the checks, the selection or a
/// toggle changes. So it does not make them again in each frame.</remarks>
internal sealed class MissionPage(EditorWindow window, PropertyGrid grid) : IDisposable
{
    // Sizes in unscaled points.
    private const float HeaderPaddingX = 10;
    private const float IniHeight = 220;

    private readonly RawIniEditor ini = new(MissionWorkspace.Vocabulary);
    private (MissionRunResult? Result, MissionRef? Item, bool ShowBaseGame, bool HidesOpenLancer, MissionPageModel? Model)
        page;
    private (MissionRunResult? Result, SectionRef? Target, bool ShowBaseGame, RawText? Text, IReadOnlyList<RawMark> Marks)
        block = (null, null, false, null, []);

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

        var model = Model(result, analysis, item);
        if (missions.PendingSection is var (script, section) && VfsPath.Comparer.Equals(script, item.Script) &&
            item.Kind == MissionItemKind.Mission)
        {
            missions.PendingSection = null;
            grid.RequestSection(SectionKey(section));
        }

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
            foreach (var pageSection in model.Sections)
            {
                Section(pageSection);
            }

            Ini(result, analysis, model, wholeScript: item.Kind == MissionItemKind.Mission);
        }

        ImGui.EndChild();
    }

    /// <summary>The key of a section of the page in the settings and in a section request.</summary>
    private static string SectionKey(string id) => $"Mission.{id}";

    /// <summary>The model of the page, made again only when the run, the selection or a toggle changes.</summary>
    private MissionPageModel Model(MissionRunResult result, MissionAnalysis analysis, MissionRef item)
    {
        var session = window.Session!;
        var showBaseGame = window.Missions.ShowBaseGame;
        var hidesOpenLancer = session.Edits.HidesOpenLancer;
        if (page.Model is { } known && ReferenceEquals(page.Result, result) && Equals(page.Item, item) &&
            page.ShowBaseGame == showBaseGame && page.HidesOpenLancer == hidesOpenLancer)
        {
            return known;
        }

        var model = item.Kind == MissionItemKind.Mission
            ? MissionPageModel.Mission(result, analysis, id => session.Name(id), CommFile, showBaseGame, hidesOpenLancer,
                SystemName)
            : MissionPageModel.Item(analysis, item, showBaseGame);
        page = (result, item, showBaseGame, hidesOpenLancer, model);
        return model;
    }

    /// <summary>The header line: the glyph of the kind, the title, the kind as a dim label and the info icon at the
    /// right end.</summary>
    private static void Header(MissionPageModel model)
    {
        var padding = MathF.Truncate(HeaderPaddingX * ImGuiHelper.Scale);
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight() * 2;
        ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(EditorTheme.Section));
        ImGui.SetCursorScreenPos(min + new Vector2(padding, MathF.Truncate(ImGui.GetFrameHeight() / 2)));
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        ImGui.Text($"{model.Glyph}  {model.Title}");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled(model.Kind);
        ImGui.SetCursorScreenPos(new Vector2(min.X + width - InfoIcon.Width - padding,
            min.Y + MathF.Truncate((height - ImGui.GetFrameHeight()) / 2)));
        InfoIcon.Draw("header", model.Help);
        ImGui.SetCursorScreenPos(min + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 1));
    }

    private void Section(PageSection section)
    {
        if (!grid.BeginSection(SectionKey(section.Id), section.Glyph, section.Title, !section.Advanced, section.Help))
        {
            return;
        }

        foreach (var field in section.Fields)
        {
            if (field.Help != null)
            {
                grid.Help(field.Help, field.Hard);
            }

            if (field.Severity != null && field.Link is LineLink)
            {
                Problem(field);
            }
            else if (field.Chips.Count > 0)
            {
                grid.Row(field.Label, () =>
                {
                    var clicked = Chip.Flow(field.Chips.Select(c => new ChipItem(c.Text, ChipLook.Grey, c.Tooltip,
                        c.Link != null)).ToList());
                    if (clicked >= 0 && field.Chips[clicked].Link is { } link)
                    {
                        GoTo(link);
                    }
                });
            }
            else if (field.Link is { } link)
            {
                if (grid.Link(field.Label, field.Value, field.Help ?? "Go to the item."))
                {
                    GoTo(link);
                }
            }
            else if (field.Severity is { } severity)
            {
                grid.Row(field.Label, () => ImGui.TextColored(
                    severity == DiagnosticSeverity.Error ? EditorTheme.Error : EditorTheme.Warning, field.Value));
            }
            else
            {
                grid.Text(field.Label, field.Value);
            }
        }

        foreach (var note in section.Notes)
        {
            grid.NoteRow(note);
        }

        grid.EndSection();
    }

    /// <summary>A row of the Problems section: the glyph of the severity, or the lock glyph of a base-game problem, the
    /// message, and "Go to". As in the Console, only the glyph has the severity color, and a base-game problem is muted
    /// (decision D512).</summary>
    private void Problem(PageField field)
    {
        grid.Row(field.Label, () =>
        {
            var (glyph, color) = field.Muted ? (EditorIcons.Lock, EditorTheme.TextDim)
                : field.Severity == DiagnosticSeverity.Error ? (EditorIcons.TimesCircle, EditorTheme.Error)
                : (EditorIcons.ExclamationTriangle, EditorTheme.Warning);
            ImGui.TextColored(color, glyph.ToString());
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, field.Muted ? EditorTheme.TextDim : EditorTheme.Text);
            ImGui.TextWrapped(field.Value);
            ImGui.PopStyleColor();
            if (field.Link is { } link && ImGui.SmallButton("Go to"))
            {
                GoTo(link);
            }

            ImGui.SetItemTooltip("Open the mission at the line in the Mission tab.");
        });
    }

    /// <summary>The INI block of the section of the page, read-only, with the marks of the checks (section 10.1 item
    /// 7 of the M6a design). The Mission page also has a button that opens the whole script.</summary>
    /// <param name="wholeScript">True for the Mission page, which has the button for the whole script.</param>
    private void Ini(MissionRunResult result, MissionAnalysis analysis, MissionPageModel model, bool wholeScript)
    {
        if (model.Ini is not { } target || analysis.Document.Find(target) is not { } section)
        {
            return;
        }

        if (!grid.BeginSection(SectionKey("ini"), EditorIcons.Terminal, "INI", true, MissionSchema.Retail.Help["rawIni"]))
        {
            return;
        }

        grid.EndSection();
        var showBaseGame = window.Missions.ShowBaseGame;
        if (block.Text is not { } text || !ReferenceEquals(block.Result, result) || !Equals(block.Target, target) ||
            block.ShowBaseGame != showBaseGame)
        {
            // The block keeps the identity of the section in its file, so a repeated nickname finds its marks.
            text = RawText.Canonical(target.Path, [section.Section], ids: [target.Id]);
            block = (result, target, showBaseGame, text,
                IniLint.Marks(text, analysis.Problems.Where(p => Equals(p.Target, target)), showBaseGame));
        }

        var height = MathF.Min(IniHeight * ImGuiHelper.Scale, (text.Lines.Count + 2) * ImGui.GetTextLineHeightWithSpacing());
        ini.Draw("##ini", text, block.Marks, new Vector2(ImGui.GetContentRegionAvail().X, height));
        if (wholeScript && ImGui.Button("Open the whole script"))
        {
            window.Missions.OpenMission(analysis.Script.Path, show: true);
        }
    }

    /// <summary>Goes to the item of a link: a mission selection, a line of a problem, a system, or the action of a bundle
    /// row.</summary>
    private void GoTo(object link)
    {
        var missions = window.Missions;
        switch (link)
        {
            case MissionRef item:
                window.Selection = item;
                break;
            case LineLink line:
                missions.GoTo(line.Target, line.Entry);
                break;
            case SectionRef target:
                missions.GoTo(target, -1);
                break;
            case SystemLink system when window.Session?.GameData?.Items.Systems.Get(system.Nickname) is { } found:
                window.OpenSystem(found);
                break;
            case BundleLink { Row.Action: MissionBundleAction.ShowSection, Row.Section: { } id }:
                grid.RequestSection(SectionKey(id));
                break;
            case BundleLink { Row.Action: MissionBundleAction.OpenCutscene, Row.Files: [var file, ..] }:
                missions.OpenCutscene(file);
                break;
            case BundleLink bundle:
                missions.OpenMission(bundle.Script, show: true);
                break;
        }
    }

    /// <summary>The voices file of a comm line, from the comm line list of the edit view, or null.</summary>
    private string? CommFile(string line) =>
        window.Session?.Edits.Lists.List("comm_line")?.Find(line)?.Section?.Path is { } path
            ? MissionFiles.Display(path)
            : null;

    /// <summary>The name of a system of the game data, or an empty text for a system without a name. Null when the game
    /// data has no such system.</summary>
    private string? SystemName(string nickname) =>
        window.Session is { GameData: { } data } session && data.Items.Systems.Get(nickname) is { } system
            ? session.ItemName(system)
            : null;

    /// <summary>Forgets the model and the texts of the profile.</summary>
    public void Reset()
    {
        page = default;
        block = (null, null, false, null, []);
    }

    public void Dispose() => ini.Dispose();
}
