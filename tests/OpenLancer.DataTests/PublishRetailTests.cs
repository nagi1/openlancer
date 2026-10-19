using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The publish texts, the digests and the same-version guard on the retail data (section 18 of the M11c design). The
/// tests only read the retail data and the sample mod. The packages and the copies of the sample go to a temporary
/// folder.
/// </summary>
public sealed class PublishRetailTests(ITestOutputHelper output) : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));

    static PublishRetailTests() => AppHandler.ConsoleInit(silent: true);

    private static string SampleFolder => GameDataPaths.SampleMod("ol-sample");

    private ExportRequest Request(Mod mod, GameReport report, PublishSettings publish, string name = "out") => new()
    {
        Mod = mod,
        Targets = [ExportTarget.Vanilla],
        BaseOf = _ => report,
        OutputFolder = Path.Combine(temp, name),
        Forms = ExportForms.DropIn,
        Publish = publish,
        StringWriter = DllStringResourceWriter.Instance,
        Thn = ThornEvaluator.Instance,
    };

    /// <summary>A copy of the sample mod in the temporary folder, so that a test can change it.</summary>
    private Mod SampleCopy() => Mod.Load(RetailWorkspace.Copy(SampleFolder, Path.Combine(temp, "mods", "ol-sample")));

    private static string ReadmeOf(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("ol-sample README.txt")!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Exports a mod and records the export in a history in the temporary folder.</summary>
    private (ExportResult Result, RecordResult? Record) ExportAndRecord(ExportRequest request, string baseFolder)
    {
        var result = ModExport.Run(request);
        foreach (var problem in result.Problems)
            output.WriteLine(problem.ToString());
        RecordResult? record = null;
        var write = PublishHistory.Update(new HistoryLocation(Path.Combine(temp, "history"), "ol-sample", request.Mod.Folder,
            [baseFolder]), "export", h => record = h.RecordExport(PublishHistory.EntryOf(result, DateTime.UtcNow)));
        Assert.True(write.Written, string.Join("\n", write.Messages));
        return (result, record);
    }

    private PublishHistory History => PublishHistory.Load(Path.Combine(temp, "history"), "ol-sample").History;

    [Fact]
    public void TheSampleExportsForVanillaWithTheTextsOfPublishJson()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var report = GameDetector.CheckRoot(baseFolder, cancel: TestContext.Current.CancellationToken);
        var sample = Mod.Load(SampleFolder);
        var publish = PublishSettings.Load(sample.Folder);

        var result = ModExport.Run(Request(sample, report, publish.Settings));

        Assert.Null(publish.ReadOnly);
        Assert.Empty(publish.Problems);
        Assert.Empty(publish.Settings.Check());
        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        var readme = ReadmeOf(result.Packages[0].Path!);
        Assert.Contains("\r\n" + publish.Settings.Summary + "\r\n", readme);
        Assert.Contains("By OpenLancer contributors.\r\n", readme);
        Assert.Contains("About:\r\nWhat it changes\r\n\r\n- New ship: the Starrunner", readme);
        Assert.Contains("- The Patriot costs less: 5600 credits.", readme);
        Assert.Contains("Changes:\r\n1.0.0\r\n- First version of the sample.\r\n", readme);
        Assert.Contains("OpenLancer sample: Starrunner was created under Microsoft's", readme);
        var nexus = File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile));
        Assert.Contains("== Summary ==\r\n" + publish.Settings.Summary + "\r\n", nexus);
        Assert.Contains("[size=5][b]What it changes[/b][/size]\r\n[list]\r\n", nexus);
        Assert.Contains("[*]The Patriot costs less: [b]5600[/b] credits.", nexus);
        Assert.Contains("== Changelog ==\r\n1.0.0\r\n- First version of the sample.\r\n", nexus);
        Assert.Contains("OpenLancer sample: Starrunner was created under Microsoft's", nexus);
        Assert.StartsWith("OpenLancer sample: Starrunner 1.0.0 (ol-sample): checklist for testers\r\n\r\nChanges of this version:\r\n" +
                          "- First version of the sample.\r\n\r\nTarget: Freelancer 1.1\r\n",
            File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.ChecklistFile)));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    [Fact]
    public void PrepareAndWriteGiveTheSameProblemsAndDigests()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var report = GameDetector.CheckRoot(baseFolder, cancel: TestContext.Current.CancellationToken);
        var sample = Mod.Load(SampleFolder);
        var request = Request(sample, report, PublishSettings.Load(sample.Folder).Settings) with
        {
            Targets = [ExportTarget.Vanilla, ExportTarget.OpenLancer],
            Forms = ExportForms.All,
        };

        var prepared = ModExport.Prepare(request);
        var written = Directory.Exists(prepared.OutputFolder);
        var result = ModExport.Write(prepared, []);

        Assert.False(written);
        Assert.Equal(prepared.Problems, result.Problems);
        Assert.Equal(prepared.Packages.Select(p => (p.FileName, p.GameFiles)), result.Packages.Select(p => (p.FileName, p.GameFiles)));
        Assert.Equal(prepared.Content, result.Content);
        // The FLMM package of the sample is refused: its infocard has a quote.
        Assert.Equal([false, true, false], prepared.Packages.Select(p => p.Refused));
        Assert.All(result.Packages.Where(p => p.Path != null), p => Assert.Equal(DataDigest.HashFile(p.Path!), p.Sha256));
        Assert.Equal(result.Content, new ModFingerprint().Content(sample.Manifest, sample.Folder));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    [Fact]
    public void TheGuardFindsAChangedPriceAtTheSameVersion()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var report = GameDetector.CheckRoot(baseFolder, cancel: TestContext.Current.CancellationToken);
        var copy = SampleCopy();
        ExportAndRecord(Request(copy, report, PublishSettings.Empty), baseFolder);
        var goods = Path.Combine(copy.Folder, "patches", "DATA", "EQUIPMENT", "goods.ini");
        File.WriteAllText(goods, File.ReadAllText(goods).Replace("price = 5600", "price = 5500"));

        var prepared = ModExport.Prepare(Request(Mod.Load(copy.Folder), report, PublishSettings.Empty));
        var guard = History.Conflict(prepared);

        Assert.Equal(GuardKind.Exported, guard.Kind);
        Assert.True(guard.CanReplace);
        Assert.Contains("has other game files", guard.Text);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    [Fact]
    public void ANewDescriptionGivesNoConflict()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var report = GameDetector.CheckRoot(baseFolder, cancel: TestContext.Current.CancellationToken);
        var copy = SampleCopy();
        var settings = PublishSettings.Load(copy.Folder).Settings;
        ExportAndRecord(Request(copy, report, settings), baseFolder);
        PublishSettings.Write(copy.Folder, settings with { Description = "A new description." });
        var changed = PublishSettings.Load(copy.Folder).Settings;

        var prepared = ModExport.Prepare(Request(copy, report, changed));
        var guard = History.Conflict(prepared);
        var (_, record) = ExportAndRecord(Request(copy, report, changed), baseFolder);

        Assert.Equal(GuardKind.None, guard.Kind);
        Assert.Equal(RecordKind.TextsOnly, record!.Kind);
        Assert.Equal(2, History.Exports.Count);
        Assert.Single(History.ValidExports("1.0.0"));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    public void Dispose()
    {
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
    }
}
