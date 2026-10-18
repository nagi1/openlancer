using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.PlayCopies;

namespace OpenLancer.Tests;

/// <summary>
/// A fake game for the play copy tests (milestone M11). It is an <see cref="ExportGame"/> with binary data files, a game
/// program and the EULA files. It also has two uninstallers with other letter cases and a <c>SAVE</c> folder. Some files
/// of the game are read-only. All files are hand-written.
/// </summary>
/// <remarks>
/// The play copies of a test go next to the game, in the temporary folder. On dispose, the fake game removes each play
/// copy with <see cref="PlayCopy.Remove(string)"/> first, and only then deletes its own folder. So no test code clears an
/// attribute or deletes a folder tree in a play copy (section 8.11 of the M11 design).
/// </remarks>
internal sealed class PlayGame : IDisposable
{
    public const string ModelPath = @"DATA\SHIPS\patriot.cmp";
    public const string SoundPath = @"DATA\AUDIO\music.wav";
    public const string NotesPath = @"DATA\INTERFACE\notes.txt";
    public const string LayoutPath = @"DATA\INTERFACE\layout.xml";
    public const string ContentPath = @"DLLS\BIN\Content.dll";
    public const string ProgramPath = @"EXE\Freelancer.exe";
    public const string FlplusplusPath = @"EXE\flplusplus.ini";

    /// <summary>The <c>flplusplus.ini</c> of the fake HD Edition, with comments.</summary>
    public const string Flplusplus = """
        ; Settings file for the test
        [flplusplus]
        ; the folder in Documents
        save_folder_name = Freelancer
        ; true puts the saves next to EXE
        save_in_directory = false
        """;

    private readonly List<string> copies = [];

    public PlayGame(bool hd = false)
    {
        Export = new ExportGame(hd);
        Game.WriteBytes(ModelPath, Encoding.ASCII.GetBytes("a model of the test"));
        Game.WriteBytes(SoundPath, Encoding.ASCII.GetBytes("a sound of the test"));
        Game.Write(NotesPath, "notes of the test\n");
        Game.Write(LayoutPath, "<layout/>\n");
        Game.WriteBytes(ProgramPath, Encoding.ASCII.GetBytes("not a real program"));
        Game.WriteBytes("EBUEula.dll", Encoding.ASCII.GetBytes("not a real dll"));
        Game.Write("EULA.RTF", "the EULA of the test\n");
        Game.Write("unins000.exe", "an uninstaller\n");
        Game.Write("unins000.dat", "the log of an uninstaller\n");
        Game.Write("UNINSTAL.EXE", "an uninstaller\n");
        Game.Write(@"SAVE\PerfOptions.ini", "[Options]\nx = 1\n");
        if (hd)
            Game.Write(FlplusplusPath, Flplusplus);
        foreach (var path in new[] { ModelPath, ContentPath, ExportGame.GoodsPath })
            File.SetAttributes(Game.PathOf(path), File.GetAttributes(Game.PathOf(path)) | FileAttributes.ReadOnly);
    }

    public ExportGame Export { get; }

    public FakeGame Game => Export.Game;

    public string Root => Game.Root;

    public GameBase Base => GameBase.Standard(Root);

    /// <summary>The default play copy folder of the game.</summary>
    public string CopyFolder => PlayCopy.DefaultFolder(Root);

    /// <summary>The path of a file in the default play copy.</summary>
    public string CopyPath(string relative) => Path.Combine(CopyFolder, relative);

    /// <summary>Makes or completes the play copy of the game.</summary>
    public PlayCopy Make(PlayCopyMode mode = PlayCopyMode.Links, HardLinks? links = null, string? folder = null)
    {
        folder ??= CopyFolder;
        copies.Add(folder);
        return PlayCopy.Make(Base, folder, mode, "test", links ?? HardLinks.Instance);
    }

    /// <summary>Merges the mods on the game, with the rules of Play in the game.</summary>
    public TargetFiles Build(params Mod[] mods) =>
        TargetBuild.Run(Base, mods, ModOverlay.StringsDllName, TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

    /// <summary>Writes a profile with the game as its base and the mods, and returns its path.</summary>
    public string Profile(params Mod[] mods)
    {
        var profile = Path.Combine(Game.Folder, "profiles", "test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, JsonSerializer.Serialize(new { @base = Root, mods = mods.Select(m => m.Folder) }));
        return profile;
    }

    /// <summary>Keeps a play copy folder for the remove at the end of the test.</summary>
    public void Track(string folder) => copies.Add(folder);

    /// <summary>The size, the attributes, the creation time, the write time and the SHA-256 of each file below a
    /// folder, read through handles. A hard link to a file of the folder gives the same values.</summary>
    public static SortedDictionary<string, string> Stamps(string folder)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, _) in PlayCopyPlan.Files(folder))
        {
            var path = Path.Combine(folder, relative);
            var stamp = HardLinks.Instance.Stamp(path);
            result[relative] = $"{stamp.Size} {stamp.Attributes} {stamp.CreationTime.Ticks} {stamp.WriteTime.Ticks} " +
                               DataDigest.HashFile(path);
        }
        return result;
    }

    /// <summary>True when the two names are hard links of one file.</summary>
    public static bool SameFile(string a, string b) => HardLinks.Instance.Stamp(a).SameFile(HardLinks.Instance.Stamp(b));

    public void Dispose()
    {
        foreach (var folder in copies.Append(CopyFolder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder))
                continue;
            try
            {
                PlayCopy.Remove(folder);
            }
            catch (Exception e) when (e is PlayCopyException or IOException or UnauthorizedAccessException)
            {
                DeleteNames(folder);
            }
        }

        // The fake game clears the read-only attribute of its files before it deletes them. A play copy that is still
        // there keeps the folder, so that no attribute changes through a link of the play copy.
        if (copies.Append(CopyFolder).All(f => !Directory.Exists(f)))
            Export.Dispose();
    }

    /// <summary>Deletes each name below a folder and the folder, from the deepest folder up, for a play copy without a
    /// valid record. It never goes into a link to a folder.</summary>
    private static void DeleteNames(string folder)
    {
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*",
                         new EnumerationOptions { AttributesToSkip = 0 }).ToList())
            {
                if (info is DirectoryInfo && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    DeleteNames(info.FullName);
                else
                    HardLinks.Instance.Delete(info.FullName);
            }
            HardLinks.Instance.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A name that a failed test still holds open stays until the next temp cleanup. It has no write access to
            // the game.
        }
    }
}
