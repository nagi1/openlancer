using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using OpenLancer.Runtime.Play;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The play view waits for a save of the editor in a mod of the view, and stops on a save that does not complete
/// (decision D51).
/// </summary>
public sealed class PlayViewTests
{
    private const string GoodsPatch = @"patches\" + GoodsPath;
    private const string WeaponsPatch = @"patches\" + WeaponsPath;
    private const string NewWeapons = "[Gun]\nnickname = gun1\ndamage = 20";

    /// <summary>The mods "low", "act" and "later", in this load order. "act" changes the price of g1 to 120.</summary>
    private static TestWorkspace Workspace()
    {
        var ws = EditSessionTests.Workspace(new() { [GoodsPatch] = "[Good]\nnickname = g1\nprice = 120" });
        ws.Mod("low", new());
        ws.Mod("later", new());
        ws.WriteProfile("low", "act", "later");
        return ws;
    }

    /// <summary>
    /// The profile loads while the save of "act" runs. The save changes a patch, adds a patch, and then deletes its
    /// journal after 300 ms. The build waits for the save and shows both patches.
    /// </summary>
    [Fact]
    public async Task TheBuildWaitsForAJournalThatGoes()
    {
        using var ws = Workspace();
        var act = ws.ModFolder("act");
        WriteJournal(act);
        var profile = ModProfile.Load(ws.ProfilePath);
        var clock = Stopwatch.StartNew();
        var save = EndSaveLater(() =>
        {
            File.WriteAllText(Path.Combine(act, GoodsPatch), "[Good]\nnickname = g1\nprice = 130");
            File.WriteAllText(Path.Combine(act, WeaponsPatch), NewWeapons);
            File.Delete(Journal(act));
        });

        var overlay = PlayView.Build(profile, "act");
        await save;

        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(300), PlayView.SaveWait);
        Assert.Equal(["low", "act"], overlay.Mods.Select(m => m.Id));
        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(130, TestWorkspace.Sections(overlay, GoodsPath)[0]["price"]![0].ToInt32());
        Assert.Equal(20, TestWorkspace.Sections(overlay, WeaponsPath)[0]["damage"]![0].ToInt32());
    }

    [Fact]
    public void AJournalThatStaysStopsTheBuild()
    {
        using var ws = Workspace();
        WriteJournal(ws.ModFolder("act"));
        var profile = ModProfile.Load(ws.ProfilePath);
        var clock = Stopwatch.StartNew();

        var error = Assert.Throws<ModLoadException>(() => PlayView.Build(profile, "act"));

        Assert.InRange(clock.Elapsed, PlayView.SaveWait, PlayView.SaveWait + TimeSpan.FromSeconds(5));
        Assert.Contains("A save of the editor in mod 'act' is not complete.", error.Message);
        Assert.Equal(ws.ModFolder("act"), error.Path);
    }

    [Fact]
    public void AJournalInALowerModStopsTheBuild()
    {
        using var ws = Workspace();
        WriteJournal(ws.ModFolder("low"));
        var profile = ModProfile.Load(ws.ProfilePath);

        var error = Assert.Throws<ModLoadException>(() => PlayView.Build(profile, "act"));

        Assert.Contains("in mod 'low' is not complete", error.Message);
    }

    /// <summary>The view does not show a later mod, so a save of a later mod does not stop the build.</summary>
    [Fact]
    public void AJournalInALaterModDoesNotCount()
    {
        using var ws = Workspace();
        WriteJournal(ws.ModFolder("later"));
        var profile = ModProfile.Load(ws.ProfilePath);
        var clock = Stopwatch.StartNew();

        var overlay = PlayView.Build(profile, "act");

        Assert.True(clock.Elapsed < PlayView.SaveWait, $"The build took {clock.Elapsed}.");
        Assert.Equal(["low", "act"], overlay.Mods.Select(m => m.Id));
        Assert.Equal(120, TestWorkspace.Sections(overlay, GoodsPath)[0]["price"]![0].ToInt32());
    }

    /// <summary>
    /// <see cref="ModProfile.Load"/> reads the mods while a save of "act" runs. The save adds a patch and ends before
    /// the build. The build loads the mods again, so it shows the patch.
    /// </summary>
    [Fact]
    public void TheBuildLoadsTheModsThatTheProfileLoadReadDuringASave()
    {
        using var ws = Workspace();
        var act = ws.ModFolder("act");
        WriteJournal(act);
        var profile = ModProfile.Load(ws.ProfilePath);
        File.WriteAllText(Path.Combine(act, WeaponsPatch), NewWeapons);
        File.Delete(Journal(act));

        var overlay = PlayView.Build(profile, "act");

        Assert.Equal(["low", "act"], overlay.Mods.Select(m => m.Id));
        Assert.Equal(20, TestWorkspace.Sections(overlay, WeaponsPath)[0]["damage"]![0].ToInt32());
    }

    /// <summary>
    /// A save of "act" commits while the build writes the text DLL. It adds a patch and deletes its journal after
    /// 300 ms. The check after the build finds the journal, so the build loads the mods again and shows the patch.
    /// </summary>
    [Fact]
    public async Task TheBuildWaitsForASaveThatCommitsDuringTheBuild()
    {
        using var ws = Workspace();
        var act = ws.ModFolder("act");
        File.WriteAllText(Path.Combine(act, Mod.StringsFile), """{ "strings": { "gun_name": "Gun" } }""");
        var profile = ModProfile.Load(ws.ProfilePath);
        var writer = new SavingWriter(act);
        var clock = Stopwatch.StartNew();

        var overlay = PlayView.Build(profile, "act", writer);
        await writer.Save!;

        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(300), PlayView.SaveWait);
        Assert.Equal(2, writer.Writes);
        Assert.Equal(["low", "act"], overlay.Mods.Select(m => m.Id));
        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(20, TestWorkspace.Sections(overlay, WeaponsPath)[0]["damage"]![0].ToInt32());
    }

    private static string Journal(string modFolder) => Path.Combine(modFolder, SaveTransaction.FolderName, "journal");

    private static void WriteJournal(string modFolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Journal(modFolder))!);
        File.WriteAllText(Journal(modFolder), "olsave journal 1\n");
    }

    /// <summary>Runs <paramref name="end"/>, the end of a save, 300 ms from now.</summary>
    /// <returns>A task that completes when <paramref name="end"/> completes, or fails with its exception.</returns>
    /// <remarks>
    /// The end of the save runs on a thread of its own, as a save of the editor runs in another process. On a thread of
    /// the pool it can wait for a free thread for longer than <see cref="PlayView.SaveWait"/> while the other tests
    /// block the pool, and the build then reports a save that did not complete.
    /// </remarks>
    private static Task EndSaveLater(Action end)
    {
        var done = new TaskCompletionSource();
        new Thread(() =>
        {
            try
            {
                Thread.Sleep(300);
                end();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }) { IsBackground = true, Name = "test save" }.Start();
        return done.Task;
    }

    /// <summary>
    /// Writes the text DLL as the fake writer of the workspace does. At its first write, a save of the mod commits: it
    /// writes the journal and a new patch, and deletes the journal 300 ms later.
    /// </summary>
    private sealed class SavingWriter(string modFolder) : IStringResourceWriter
    {
        public int Writes { get; private set; }

        /// <summary>The end of the save, or null before the first write.</summary>
        public Task? Save { get; private set; }

        public byte[] Write(IReadOnlyDictionary<int, string> strings, IReadOnlyDictionary<int, string> infocards)
        {
            if (Writes++ == 0)
            {
                WriteJournal(modFolder);
                File.WriteAllText(Path.Combine(modFolder, WeaponsPatch), NewWeapons);
                Save = EndSaveLater(() => File.Delete(Journal(modFolder)));
            }
            return TestWorkspace.FakeDllWriter.Instance.Write(strings, infocards);
        }
    }
}
