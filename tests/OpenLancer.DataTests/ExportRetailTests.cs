using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The export of the sample mod on the retail data (section 7.19 of the M11 design). The tests only read the retail
/// data. The packages go to a temporary folder.
/// </summary>
public sealed class ExportRetailTests(ITestOutputHelper output)
{
    private const string CityscapePath = @"DATA\Scripts\Bases\li_01_cityscape_hardpoint_01.thn";

    static ExportRetailTests() => AppHandler.ConsoleInit(silent: true);

    private static Mod Sample => Mod.Load(GameDataPaths.SampleMod("ol-sample"));

    private static TargetFiles Build(string root, bool keepPlaceholders = false) =>
        TargetBuild.Run(GameBase.Standard(root), [Sample], ModExport.DllName("ol-sample"), DllStringResourceWriter.Instance,
            ThornEvaluator.Instance, keepPlaceholders);

    [Fact]
    public void TheSampleExportsForVanillaWithTheExpectedFiles()
    {
        var baseFolder = GameDataPaths.RequireBase();

        var files = Build(baseFolder);

        Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(new[]
            {
                @"DATA\EQUIPMENT\goods.ini", @"DATA\EQUIPMENT\market_ships.ini", @"DATA\SHIPS\shiparch.ini",
                @"DATA\Universe\Systems\Li01\Li01.ini", @"DATA\Universe\Systems\St01\St01.ini",
                @"DATA\Scripts\Story\s052x_nrml_Akira_bw_02_Cityscape_01.thn",
                @"DATA\Scripts\Story\s062x_nrml_vonclaussn_pl_06_Pad2_01.thn", CityscapePath,
                ModOverlay.FreelancerIniPath, @"EXE\ol_ol-sample.dll",
            }.Order(StringComparer.OrdinalIgnoreCase),
            files.Files.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase), VfsPath.Comparer);
        // Retail 1.0 lists 6 DLLs, so the mod DLL gets index 7.
        Assert.Equal(7, files.StringIds.DllIndex);
        var errors = FormatChecks.Run(files).Where(p => p.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
        // The merged cutscene of the base is text with the moved camera.
        var cityscape = Encoding.ASCII.GetString(files.File(CityscapePath)!.Bytes);
        Assert.Contains("pos = { 59.658787, 86.31464, 97.57275 }", cityscape);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    [Fact]
    public void TheSampleExportWritesTheDropInAndRefusesTheInfocardInTheFlmmPackage()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var report = GameDetector.CheckRoot(baseFolder, cancel: TestContext.Current.CancellationToken);
        var folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var request = new ExportRequest
            {
                Mod = Sample,
                Targets = [ExportTarget.Vanilla],
                BaseOf = _ => report,
                OutputFolder = folder,
                StringWriter = DllStringResourceWriter.Instance,
                Thn = ThornEvaluator.Instance,
            };

            var result = ModExport.Run(request);
            var probe = ModExport.Run(request with { Forms = ExportForms.Flmm, FlmmProbe = true });

            foreach (var problem in result.Problems)
                output.WriteLine(problem.ToString());
            Assert.Equal(ModTargets.Vanilla, report.Target);
            Assert.NotNull(result.Packages.Single(p => p.FileName == "ol-sample-1.0.0-vanilla-1.1.zip").Path);
            Assert.Null(result.Packages.Single(p => p.FileName == "ol-sample-1.0.0-vanilla-1.1-flmm.zip").Path);
            Assert.Contains(result.Problems, p => p.Message.Contains("the text of '$ifc:starrunner_info' has a quote"));
            Assert.True(probe.Succeeded, string.Join("\n", probe.Problems));
            Assert.True(File.Exists(Path.Combine(folder, "ol-sample-1.0.0-vanilla-1.1-flmm-probe.zip")));
            RetailChecks.AssertBaseIsUnchanged(baseFolder);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EachMergedFileOfTheDropInParsesToTheMergedSections()
    {
        var files = Build(GameDataPaths.RequireBase());

        foreach (var file in files.Files.Where(f => f.Kind == TargetFileKind.Ini))
        {
            var parsed = IniDocument.Parse(file.Path, file.Bytes);
            Assert.False(parsed.IsBinary, file.Path);
            Assert.True(IniTextEdit.SameSections(parsed.Sections, file.Merged!.Sections), file.Path + ": " + Difference(parsed, file.Merged!));
        }
        foreach (var file in files.Files.Where(f => f.Kind == TargetFileKind.Cutscene))
        {
            var tree = ThornEvaluator.Instance.Evaluate(file.Bytes, file.Path);
            Assert.Equal(ThnRetailNames.Apply(file.MergedTree!, ThornEvaluator.Instance.Symbols), tree);
        }
    }

    /// <summary>
    /// Publishing plan E2, proof 1: each value of each retail binary INI file reads the same in its text form. The proof
    /// compares the values of the Librelancer parser and the results of a model of the retail getters: the integer
    /// getter reads text with atoi, with a wrap at 2^32, and the float getter reads text with atof. In a binary file,
    /// the integer getter truncates a float.
    /// </summary>
    [Fact]
    public void AllRetailBiniFilesWriteAsTextWithTheSameValues()
    {
        var baseFolder = GameDataPaths.RequireBase();
        int files = 0, values = 0, integers = 0, otherThanBoolean = 0;
        foreach (var path in Directory.EnumerateFiles(baseFolder, "*.ini", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            var document = IniDocument.Parse(path, bytes);
            if (!document.IsBinary)
                continue;
            files++;
            var text = IniDocument.Parse(path, document.ToText());
            Assert.False(text.IsBinary);
            Assert.True(IniTextEdit.SameSections(text.Sections, document.Sections), path + ": " + Difference(text, document));
            foreach (var (binary, written) in document.Sections.SelectMany(s => s).SelectMany(e => e)
                         .Zip(text.Sections.SelectMany(s => s).SelectMany(e => e)))
            {
                if (binary is not (Int32Value or SingleValue))
                    continue;
                values++;
                var retail = RetailText.Of(binary);
                Assert.Equal(retail, RetailText.Of(written));
                Assert.Equal(BiniFloat(binary), TextFloat(retail));
                if (binary is SingleValue single && float.Abs((float)single) >= 2_147_483_648f)
                    continue;
                integers++;
                Assert.True(BiniInteger(binary) == Atoi(retail), $"{path}: {retail}");
                if (binary is Int32Value && BiniInteger(binary) is not (0 or 1))
                    otherThanBoolean++;
            }
        }
        output.WriteLine($"{files} binary files, {values} numbers, {integers} in the Int32 range. {otherThanBoolean} " +
                         "Int32 values are not 0 or 1, so a boolean getter reads them in another way in text.");
        Assert.True(files > 1200);
    }

    /// <summary>The first line of two documents with other names or value texts, for a message.</summary>
    private static string Difference(IniDocument a, IniDocument b)
    {
        var left = a.Sections.SelectMany(s => s.Select(e => $"[{s.Name}] {IniSectionDiff.LineKey(e)}")).ToList();
        var right = b.Sections.SelectMany(s => s.Select(e => $"[{s.Name}] {IniSectionDiff.LineKey(e)}")).ToList();
        var index = left.Zip(right).TakeWhile(p => p.First == p.Second).Count();
        return index < Math.Min(left.Count, right.Count)
            ? $"{Readable(left[index])} <> {Readable(right[index])}"
            : $"{left.Count} <> {right.Count} lines";
    }

    private static string Readable(string key) => key.Replace((char)1, '=').Replace((char)2, ',');

    /// <summary>The retail integer getter on a binary value: the Int32, or the float truncated toward 0.</summary>
    private static int BiniInteger(ValueBase value) => value switch
    {
        Int32Value integer => (int)integer,
        SingleValue single => (int)MathF.Truncate((float)single),
        _ => throw new ArgumentException(nameof(value)),
    };

    /// <summary>The retail float getter on a binary value.</summary>
    private static float BiniFloat(ValueBase value) => value switch
    {
        Int32Value integer => (int)integer,
        SingleValue single => (float)single,
        _ => throw new ArgumentException(nameof(value)),
    };

    /// <summary>The retail float getter on a decimal text: atof gives a double, and the getter keeps a float.</summary>
    private static float TextFloat(string text) => (float)double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The C function atoi as the retail integer getter uses it: a sign and digits, with a wrap at
    /// 2^32.</summary>
    private static int Atoi(string text)
    {
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        var negative = i < text.Length && text[i] == '-';
        if (i < text.Length && text[i] is '-' or '+')
            i++;
        uint value = 0;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
            value = unchecked(value * 10 + (uint)(text[i] - '0'));
        return unchecked((int)(negative ? 0u - value : value));
    }

    /// <summary>
    /// Publishing plan E4, proof 1: Windows loads the text DLL of an export as a data file. Then it finds the string
    /// block (type 6) and the infocard (type 23) with the language "neutral", as the game client looks for them.
    /// </summary>
    [Fact]
    public void TheExportedTextDllLoadsWithTheWindowsApi()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows loader is only on Windows.");
        var files = Build(GameDataPaths.RequireBase());
        var dll = files.File(@"EXE\ol_ol-sample.dll")!;
        var path = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"), "ol_ol-sample.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, dll.Bytes);
        var module = LoadLibraryExW(path, IntPtr.Zero, LoadLibraryAsDatafile);
        try
        {
            Assert.NotEqual(IntPtr.Zero, module);
            foreach (var id in files.StringIds.Strings.Keys)
                Assert.NotEqual(IntPtr.Zero, FindResourceExA(module, StringTable, id / 16 + 1, Neutral));
            foreach (var id in files.StringIds.Infocards.Keys)
                Assert.NotEqual(IntPtr.Zero, FindResourceExA(module, Infocard, id, Neutral));
            Assert.Equal(IntPtr.Zero, FindResourceExA(module, Infocard, 9999, Neutral));
        }
        finally
        {
            if (module != IntPtr.Zero)
                FreeLibrary(module);
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private const uint LoadLibraryAsDatafile = 0x2;
    private const nint StringTable = 6;
    private const nint Infocard = 23;
    private const ushort Neutral = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResourceExA(IntPtr module, nint type, nint name, ushort language);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}

/// <summary>The retail cutscene scripts and the Librelancer-only names (section 5.6 of the M11 design).</summary>
public sealed class ThnRetailTests
{
    static ThnRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EachRetailCutsceneReads()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var files = Directory.EnumerateFiles(Path.Combine(baseFolder, "DATA"), "*.thn", SearchOption.AllDirectories).ToList();

        foreach (var file in files)
        {
            var tree = ThornEvaluator.Instance.Evaluate(File.ReadAllBytes(file), file);
            // No retail cutscene uses a name that only Librelancer defines.
            Assert.Empty(ThnRetailNames.Find(tree));
        }
        Assert.Equal(1506, files.Count);
    }
}
