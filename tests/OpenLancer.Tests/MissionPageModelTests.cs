using System;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The models of the mission pages of the Inspector (section 10 of the M6a design), on synthetic scripts.
/// </summary>
public sealed class MissionPageModelTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    private const string Full = Story + """

        [Trigger]
        nickname = t_more
        repeatable = true
        Cnd_True = no_params
        Act_SetTitle = 1234

        [NNObjective]
        nickname = o1
        type = ids, 3001

        [MsnShip]
        nickname = s1
        """;

    private static (MissionRunResult Result, MissionAnalysis Analysis) Run(string text)
    {
        using var folder = new MissionFolder();
        folder.Write(Script, text);
        var result = MissionRun.Execute(folder.Inputs());
        return (result, result.Of(Script)!);
    }

    private static string TextOf(int id) => id switch
    {
        1234 => "Invented title",
        3001 => "Invented objective",
        _ => "",
    };

    [Fact]
    public void TheMissionPageGivesItsNamedSections()
    {
        var (result, analysis) = Run(Full.Replace("[Mission]", "[Mission]\nmission_title = 5"));

        var page = MissionPageModel.Mission(result, analysis, TextOf, _ => null, showBaseGame: false, hidesOpenLancer: false);

        Assert.Equal(["slot", "overview", "problems", "openlancer", "voice", "text", "files", "advanced"],
            page.Sections.Select(s => s.Id));
        Assert.Equal("Mission", page.Kind);
        Assert.StartsWith("M01a: ", page.Title);
        Assert.NotNull(page.Completeness);
        Assert.Equal(["Title", "Objective"], page.Section("text")!.Fields.Select(f => f.Label));
        Assert.Equal(["Invented title (1234)", "Invented objective (3001)"], page.Section("text")!.Fields.Select(f => f.Value));
        Assert.Equal("Start trigger", page.Section("overview")!.Fields.Single(f => f.Value == "t_start").Label);
        var advanced = page.Section("advanced")!;
        Assert.True(advanced.Advanced);
        Assert.Equal(MissionPageModel.UnusedMissionKey, Assert.Single(advanced.Fields).Help);
    }

    [Fact]
    public void TheOpenLancerSectionHidesForAModWithoutOpenLancer()
    {
        var (result, analysis) = Run(Full);

        var page = MissionPageModel.Mission(result, analysis, TextOf, _ => null, false, hidesOpenLancer: true);

        Assert.Null(page.Section("openlancer"));
    }

    [Fact]
    public void TheProblemsFollowTheToggle()
    {
        var (result, analysis) = Run(Story + "\n[Trigger]\nnickname = t_x\nCnd_True = no_params\n");

        var hidden = MissionPageModel.Mission(result, analysis, TextOf, _ => null, false, false).Section("problems")!;
        var shown = MissionPageModel.Mission(result, analysis, TextOf, _ => null, true, false).Section("problems")!;

        Assert.Empty(hidden.Fields);
        Assert.Equal(["1 base-game message is hidden."], hidden.Notes);
        var field = Assert.Single(shown.Fields);
        Assert.Equal(("G05", true), (field.Label, field.Muted));
        Assert.Empty(shown.Notes);
    }

    [Fact]
    public void TheTriggerPageHasItsFieldsInTheTriggerSectionAndNotInAdvanced()
    {
        var (_, analysis) = Run(Full);
        var trigger = analysis.Index.Trigger("t_more")!;

        var page = MissionPageModel.Item(analysis, new MissionRef(Script, MissionItemKind.Trigger, trigger.Section.Ref),
            false);

        Assert.Equal("Trigger", page.Kind);
        Assert.Equal("t_more", page.Title);
        var section = page.Section("trigger")!;
        Assert.Equal("Trigger", section.Title);
        Assert.False(section.Advanced);
        Assert.Equal(["Starts with the mission", "Repeats", "Starts when"], section.Fields.Select(f => f.Label));
        Assert.Equal(["No", "Yes"], section.Fields.Take(2).Select(f => f.Value));
        Assert.DoesNotContain(page.Sections, s => s.Advanced);
        Assert.Equal(trigger.Section.Ref, page.Ini);
    }

    [Fact]
    public void EachItemKindGivesAPage()
    {
        var (_, analysis) = Run(Full);
        var ship = analysis.Document.OfKind(MissionSectionKind.Ship).Single();

        foreach (var kind in Enum.GetValues<MissionItemKind>())
        {
            var page = MissionPageModel.Item(analysis, new MissionRef(Script, kind, ship.Ref), false);
            Assert.False(string.IsNullOrEmpty(page.Kind), kind.ToString());
            Assert.NotNull(page.Section("problems"));
        }
        Assert.Equal("Mission ship", MissionPageModel.Item(analysis, new MissionRef(Script, MissionItemKind.Ship, ship.Ref),
            false).Kind);
    }

    [Fact]
    public void AMissingCommLineIsAnError()
    {
        var (result, analysis) = Run(Full + "\n[Trigger]\nnickname = t_comm\nCnd_True = no_params\n" +
                                     "Act_SendComm = s1, Player, comm_known\nAct_SendComm = s1, Player, comm_gone\n");
        string? CommFile(string name) => name == "comm_known" ? @"AUDIO\invented_voice.utf" : null;

        var voice = MissionPageModel.Mission(result, analysis, TextOf, CommFile, false, false).Section("voice")!;
        var empty = MissionPageModel.Mission(Run(Full).Result, Run(Full).Analysis, TextOf, CommFile, false, false)
            .Section("voice")!;

        Assert.Equal(["comm_known", "comm_gone"], voice.Fields.Select(f => f.Label));
        Assert.Equal(@"AUDIO\invented_voice.utf", voice.Fields[0].Value);
        Assert.Null(voice.Fields[0].Severity);
        Assert.Equal(("Missing", DiagnosticSeverity.Error), (voice.Fields[1].Value, voice.Fields[1].Severity));
        Assert.Equal(["The script names no comm line."], empty.Notes);
    }
}
