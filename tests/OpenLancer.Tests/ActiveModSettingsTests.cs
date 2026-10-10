using System;
using System.IO;
using System.Runtime.CompilerServices;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The settings and checks of the active mod: the active mod of each profile, a mod in the OpenLancer repo, and the
/// Console entries about a section.
/// </summary>
public sealed class ActiveModSettingsTests
{
    [Fact]
    public void AProfileOpensWithItsStoredModOrWithItsLastMod()
    {
        var settings = new EditorSettings();
        string[] mods = ["ol-sample", "proof"];

        Assert.Equal("proof", settings.ActiveModOf(@"C:\p\sample.json", mods));
        settings.SetActiveMod(@"C:\p\sample.json", "ol-sample");
        Assert.Equal("ol-sample", settings.ActiveModOf(@"C:\p\sample.json", mods));
        settings.SetActiveMod(@"C:\p\sample.json", null);
        Assert.Null(settings.ActiveModOf(@"C:\p\sample.json", mods));
        // A stored mod that the profile does not have any more gives the last mod.
        settings.SetActiveMod(@"C:\p\sample.json", "gone");
        Assert.Equal("proof", settings.ActiveModOf(@"C:\p\sample.json", mods));
        Assert.Null(settings.ActiveModOf(@"C:\p\empty.json", []));
    }

    [Fact]
    public void TheActiveModsStayAfterASaveAndAReadOfTheSettings()
    {
        var settings = new EditorSettings();
        settings.SetActiveMod(@"C:\p\sample.json", "ol-sample");
        settings.SetActiveMod(@"C:\p\other.json", null);

        var read = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        Assert.Equal("ol-sample", read.ActiveModOf(@"C:\p\sample.json", ["ol-sample", "proof"]));
        Assert.Null(read.ActiveModOf(@"C:\p\other.json", ["proof"]));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("ol-sample", read.ActiveModOf(@"c:\P\SAMPLE.json", ["ol-sample", "proof"]));
        }
    }

    [Fact]
    public void AModFolderInTheRepoFindsTheRootOfTheRepo()
    {
        using var ws = new TestWorkspace();
        var repo = Path.Combine(ws.Root, "repo");
        var mod = Path.Combine(repo, "samples", "mods", "ol-sample");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(repo, "OpenLancer.slnx"), "<Solution />");
        var other = Path.Combine(ws.Root, "mods", "proof");
        Directory.CreateDirectory(other);

        Assert.Equal(repo, RepoCheck.RepoRoot(mod));
        Assert.Null(RepoCheck.RepoRoot(other));
        // The real repo: this source file is in it, next to the sample mod.
        var root = RepoCheck.RepoRoot(SourceFolder());
        Assert.NotNull(root);
        Assert.True(Directory.Exists(Path.Combine(root, "samples", "mods", "ol-sample")));
    }

    private static string SourceFolder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [Fact]
    public void AConsoleEntryOfADiagnosticKeepsItsSection()
    {
        var log = new ConsoleLog();
        var target = new SectionRef(@"DATA\EQUIPMENT\goods.ini", new SectionIdentity("Good", "g1"));

        var entry = log.Add(Diagnostic.Error("goods.ini", "message") with { Target = target }, "Save 2: ");

        Assert.Equal(ConsoleSeverity.Error, entry.Severity);
        Assert.Equal("Save 2: message", entry.Message);
        Assert.Equal(target, entry.Target);
        Assert.Null(log.Add(ConsoleSeverity.Info, "Editor", "no target").Target);
    }
}
