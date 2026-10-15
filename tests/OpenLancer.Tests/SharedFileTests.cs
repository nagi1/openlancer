using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Play;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The game and oltool read mod files while the editor saves them (decision D51). A read lets a save replace the file,
/// and a save replaces a file that such a read holds open (review finding R5).
/// </summary>
public sealed class SharedFileTests
{
    private const string PatchPath = @"patches\DATA\EQUIPMENT\goods.ini";
    private const string ScenePath = @"DATA\SCRIPTS\intro.thn";

    private static readonly byte[] Old = "[Good]\r\nnickname = g1\r\nprice = 120\r\n"u8.ToArray();
    private static readonly byte[] New = "[Good]\r\nnickname = g1\r\nprice = 130\r\n"u8.ToArray();
    private static readonly byte[] Third = "[Good]\r\nnickname = g1\r\nprice = 140\r\n"u8.ToArray();

    /// <summary>
    /// A reader that the game opened before a save keeps the old bytes. The save is complete, and the file has the new
    /// bytes.
    /// </summary>
    [Fact]
    public void ASaveReplacesAFileThatASharedReaderHoldsOpen()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("act", new() { [PatchPath] = "" });
        var target = Path.Combine(mod.Folder, PatchPath);
        File.WriteAllBytes(target, Old);
        var conflicts = new List<Diagnostic>();

        using (var reader = SharedFile.OpenRead(target))
        {
            var failure = SaveTransaction.Commit(mod.Folder, [new FileChange(target, New)], conflicts);

            Assert.Null(failure);
            Assert.Empty(conflicts);
            Assert.False(mod.HasPendingSave);
            Assert.Equal(New, File.ReadAllBytes(target));
            Assert.Equal(Old, ReadToEnd(reader));
        }
        Assert.Equal(New, File.ReadAllBytes(target));
    }

    /// <summary>
    /// Each read of <see cref="SharedFile"/> works while another process holds the file with delete access, as a
    /// rename does. A read of <see cref="File"/> does not. A process that writes the file in place stops each read of
    /// <see cref="SharedFile"/>, so a parser never gets a part of the file.
    /// </summary>
    [Fact]
    public void ASharedReadAllowsADeleteButNotAWrite()
    {
        using var ws = new TestWorkspace();
        var path = Path.Combine(ws.Root, "strings.json");
        var text = "{ \"strings\": { \"name\": \"Caf\u00e9\" } }";
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var bytes = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Throws<IOException>(() => SharedFile.ReadAllBytes(path));
            Assert.Throws<IOException>(() => SharedFile.ReadAllText(path));
            Assert.Throws<IOException>(() => SharedFile.OpenRead(path));
        }

        using var holder = HoldForDelete(path);
        Assert.Throws<IOException>(() => File.ReadAllBytes(path));
        Assert.Equal(bytes, SharedFile.ReadAllBytes(path));
        Assert.Equal(text, SharedFile.ReadAllText(path));
        using var stream = SharedFile.OpenRead(path);
        Assert.Equal(bytes, ReadToEnd(stream));
    }

    [Fact]
    public void HasPendingSaveTellsWhetherTheJournalExists()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("act", new());
        var journal = Journal(mod.Folder);

        Assert.False(mod.HasPendingSave);
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "olsave journal 1\n");
        Assert.True(mod.HasPendingSave);
        File.Delete(journal);
        Assert.False(mod.HasPendingSave);
    }

    /// <summary>
    /// <see cref="AtomicFile.MoveOver"/> and <see cref="AtomicFile.WriteAllBytes"/> replace a file that a shared reader
    /// holds open. A reader that shares only read access stops the move, as before. A save then stays incomplete, and
    /// the recovery completes it after the reader closes the file.
    /// </summary>
    [Fact]
    public void MoveOverReplacesAFileThatOnlyASharedReaderHolds()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("act", new() { [PatchPath] = "" });
        var target = Path.Combine(mod.Folder, PatchPath);
        var source = Path.Combine(mod.Folder, "goods.new");
        File.WriteAllBytes(target, Old);
        File.WriteAllBytes(source, New);

        using (var reader = SharedFile.OpenRead(target))
        {
            AtomicFile.MoveOver(source, target);
            Assert.Equal(Old, ReadToEnd(reader));
        }
        Assert.Equal(New, File.ReadAllBytes(target));
        Assert.False(File.Exists(source));
        using (var reader = SharedFile.OpenRead(target))
        {
            AtomicFile.WriteAllBytes(target, Third);
            Assert.Equal(New, ReadToEnd(reader));
        }
        Assert.Equal(Third, File.ReadAllBytes(target));

        var conflicts = new List<Diagnostic>();
        File.WriteAllBytes(source, New);
        using (var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => AtomicFile.MoveOver(source, target));
            Assert.True(File.Exists(source));

            var failure = SaveTransaction.Commit(mod.Folder, [new FileChange(target, Old)], conflicts);

            Assert.IsType<IOException>(failure);
            Assert.True(mod.HasPendingSave);
        }
        Assert.Equal(Third, File.ReadAllBytes(target));
        Assert.True(SaveTransaction.Recover(mod.Folder, conflicts));
        Assert.Empty(conflicts);
        Assert.False(mod.HasPendingSave);
        Assert.Equal(Old, File.ReadAllBytes(target));
    }

    /// <summary>A move in a folder with a path of more than 260 characters works, as a move of <see cref="File"/>
    /// does.</summary>
    [Fact]
    public void MoveOverTakesALongPath()
    {
        using var ws = new TestWorkspace();
        var folder = Path.Combine(ws.Root, new string('a', 120), new string('b', 120), new string('c', 40));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "goods.ini");
        var source = Path.Combine(folder, "goods.ini.oltmp");
        Assert.True(target.Length > 260);
        File.WriteAllBytes(target, Old);
        File.WriteAllBytes(source, New);

        using (var reader = SharedFile.OpenRead(target))
        {
            AtomicFile.MoveOver(source, target);
            Assert.Equal(Old, ReadToEnd(reader));
        }

        Assert.Equal(New, File.ReadAllBytes(target));
        Assert.False(File.Exists(source));
    }

    /// <summary>
    /// The game holds a whole file of a mod open through the file system of the play view, for example a cutscene that
    /// it plays. A save replaces the file all the same, and the game keeps the old bytes.
    /// </summary>
    [Fact]
    public void ASaveReplacesAWholeFileThatThePlayViewHoldsOpen()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("act", new() { [@"files\" + ScenePath] = "" });
        var target = Path.Combine(mod.Folder, Mod.FilesFolder, ScenePath);
        File.WriteAllBytes(target, Old);
        ws.WriteProfile("act");
        var overlay = PlayView.Build(ModProfile.Load(ws.ProfilePath), "act");
        var conflicts = new List<Diagnostic>();

        using (var stream = overlay.CreateFileSystem().Open(ScenePath))
        {
            var failure = SaveTransaction.Commit(mod.Folder, [new FileChange(target, New)], conflicts);

            Assert.Null(failure);
            Assert.Empty(conflicts);
            Assert.False(mod.HasPendingSave);
            Assert.Equal(New, File.ReadAllBytes(target));
            Assert.Equal(Old, ReadToEnd(stream));
        }
    }

    /// <summary>
    /// Each read of a mod file on the load path of a play shares delete: the profile, the manifests, the text, a patch,
    /// the whole INI file of a lower mod that the patch changes, and a whole file that the game opens. Another handle
    /// holds each of these files with delete access during the load, so a read without delete sharing fails. A save
    /// can replace each of these files while the game or oltool reads it.
    /// </summary>
    [Fact]
    public void EachReadOfThePlayLoadSharesDelete()
    {
        using var ws = Workspace(new()
        {
            [Mod.StringsFile] = """{ "strings": { "gun_name": "Gun" } }""",
            [@"patches\" + WeaponsPath] = "[Gun]\nnickname = gun1\ndamage = 20",
            [@"files\" + ScenePath] = "scene",
        });
        ws.Mod("low", new() { [@"files\" + WeaponsPath] = "[Gun]\nnickname = gun1\ndamage = 15" });
        ws.WriteProfile("low", "act");
        var act = ws.ModFolder("act");
        var low = ws.ModFolder("low");

        using var profile = HoldForDelete(ws.ProfilePath);
        using var lowManifest = HoldForDelete(Path.Combine(low, ModManifest.FileName));
        using var lowWeapons = HoldForDelete(Path.Combine(low, Mod.FilesFolder, WeaponsPath));
        using var actManifest = HoldForDelete(Path.Combine(act, ModManifest.FileName));
        using var actStrings = HoldForDelete(Path.Combine(act, Mod.StringsFile));
        using var actPatch = HoldForDelete(Path.Combine(act, Mod.PatchesFolder, WeaponsPath));
        using var actScene = HoldForDelete(Path.Combine(act, Mod.FilesFolder, ScenePath));

        var overlay = PlayView.Build(ModProfile.Load(ws.ProfilePath), "act");

        Assert.Equal(["low", "act"], overlay.Mods.Select(m => m.Id));
        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(20, TestWorkspace.Sections(overlay, WeaponsPath)[0]["damage"]![0].ToInt32());
        using var stream = overlay.CreateFileSystem().Open(ScenePath);
        Assert.Equal("scene"u8.ToArray(), ReadToEnd(stream));
    }

    /// <summary>
    /// Opens a file with delete access, as a rename or a delete does. While the stream is open, each other open of the
    /// file must share delete (CreateFile, <c>FILE_FLAG_DELETE_ON_CLOSE</c>). The file goes when the stream closes.
    /// </summary>
    private static FileStream HoldForDelete(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);

    private static string Journal(string modFolder) => Path.Combine(modFolder, SaveTransaction.FolderName, "journal");

    private static byte[] ReadToEnd(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
