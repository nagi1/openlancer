using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The target checks of the mission patches with the vanilla data and HD Edition (sections 6.12 and 11.2 of the M6b
/// design). The tests read both data sets at run time and hold no text of them.
/// </summary>
public sealed class MissionTargetRetailTests(ITestOutputHelper output)
{
    /// <summary>An edit of each trigger of a script that HD Edition changes gives T02 on HD Edition and nothing on the
    /// vanilla base of the profile.</summary>
    [Fact]
    public void AnEditOfATriggerThatHdChangesGivesT02OnHd()
    {
        var vanilla = GameDataPaths.RequireBase();
        var hde = GameDataPaths.RequireHde();
        using var workspace = new RetailWorkspace(vanilla, "m6btarget");
        var session = workspace.Open();
        session.Missions.Coverage = EngineCoverage.Instance;
        session.TargetGame = id => id switch
        {
            ModTargets.HdEdition => GameBase.Standard(hde),
            ModTargets.Vanilla => GameBase.Standard(vanilla),
            _ => null,
        };

        var (script, changed) = Scripts(session).Select(s => (Script: s, Triggers: ChangedByHd(session, s, hde)))
            .First(s => s.Triggers.Count > 0);
        foreach (var trigger in changed)
            Assert.Equal(EditStatus.Applied, session.Missions.Apply(
                new SetTriggerSystem(new TriggerRef(script, trigger)) { System = "ol_target" }).Status);
        var watch = Stopwatch.StartNew();
        var problems = session.SaveProblems();
        watch.Stop();

        output.WriteLine($"{script}: {changed.Count} triggers that HD Edition changes. The gate took {watch.ElapsedMilliseconds} ms.");
        var warnings = problems.Where(p => p.Rule == "T02").ToList();
        Assert.Equal(changed.Order(StringComparer.OrdinalIgnoreCase),
            warnings.Select(w => w.Target!.Id.Key!).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(warnings, w => Assert.Contains(" on HD Edition 0.7.1 differs from the base of your edit.", w.Message));
        Assert.DoesNotContain(problems, p => p.Rule == "T01");
    }

    /// <summary>Risk R14 of the M6b design: no two triggers of a script of HD Edition have one game id, so rule M27
    /// gives nothing on the retail data. The vanilla scripts have the same proof in <see cref="MissionRetailTests"/>.</summary>
    [Fact]
    public void NoTwoHdTriggersOfAScriptHaveOneGameId()
    {
        var hde = GameDataPaths.RequireHde();
        var scripts = 0;
        foreach (var slot in MissionSchema.Retail.StorySlots)
        {
            var path = Path.Combine(hde, "DATA", slot.Path);
            if (!File.Exists(path))
                continue;
            scripts++;
            var names = Triggers(IniDocument.Parse(path, File.ReadAllBytes(path)).Sections).Select(t => t.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(names.Count, names.Select(FLHash.CreateID).Distinct().Count());
        }
        Assert.Equal(14, scripts);
    }

    private static IEnumerable<string> Scripts(EditSession session) =>
        MissionSchema.Retail.StorySlots.Select(s => session.DataPath(s.Path)!)
            .Where(p => session.OpenDocument(p) is { HoldsScript: true });

    /// <summary>The triggers of a script of B whose body HD Edition changes. The name of each is unique in both
    /// files.</summary>
    private static List<string> ChangedByHd(EditSession session, string script, string hde)
    {
        var file = Path.Combine(hde, script);
        if (!File.Exists(file))
            return [];
        var hd = Unique(Triggers(IniDocument.Parse(script, File.ReadAllBytes(file)).Sections));
        return Unique(Triggers(session.OpenDocument(script)!.Base))
            .Where(t => hd.TryGetValue(t.Key, out var other) && SectionHash.Of(other) != SectionHash.Of(t.Value))
            .Select(t => t.Key).ToList();
    }

    private static IEnumerable<(string Name, Section Section)> Triggers(IEnumerable<Section> sections) =>
        sections.Where(s => s.Name.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
            .Select(s => (Name: TriggerRules.Nickname(s), Section: s)).Where(t => t.Name != null)
            .Select(t => (t.Name!, t.Section));

    private static Dictionary<string, Section> Unique(IEnumerable<(string Name, Section Section)> triggers) =>
        triggers.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Section, StringComparer.OrdinalIgnoreCase);
}
