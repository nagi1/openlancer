using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.ContentEdit;
using LibreLancer.Data.Dll;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;

namespace OpenLancer.Tests;

/// <summary>
/// A fake game folder in a temporary folder for the game link tests (milestone M11a). It has the files that the
/// detector reads: <c>EXE\freelancer.ini</c>, <c>EXE\dacom.ini</c>, <c>EXE\resources.dll</c>, the universe file, a few
/// INI files and the five code files with chosen bytes. No file is a game file. Deleted on dispose.
/// </summary>
internal sealed class FakeGame : IDisposable
{
    /// <summary>The 14 <c>[Libraries]</c> lines of the retail game, with the forms of the retail file.</summary>
    public static readonly string[] RetailLibraries =
    [
        "ReadFile.dll\t; reads files", "x86math.dll", "EngBase.dll", "system.dll", "RP8.dll", "SoundStreamer.dll",
        "SoundManager.dll", "Deformable2.dll", "Thorn.dll", "Shading.dll", "RendComp.dll", "alchemy.dll", "ximage.dll",
        @".\flmaterials.dll",
    ];

    public FakeGame(string name = "Freelancer")
    {
        Folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Folder, name);
        Resources();
        Libraries();
        WriteBytes(@"EXE\resources.dll", Encoding.ASCII.GetBytes("not a real dll"));
        Write(@"DATA\universe\universe.ini", "[system]\nnickname = li01\nfile = systems\\li01\\li01.ini\n");
        Write(@"DATA\EQUIPMENT\goods.ini", "[Good]\nnickname = g1\nprice = 100\n");
        Write(@"DATA\SHIPS\shiparch.ini", "[Ship]\nnickname = s1\n");
        CodeFiles("1.0");
    }

    /// <summary>The temporary folder that holds the game root.</summary>
    public string Folder { get; }

    /// <summary>The game root, with <c>EXE\freelancer.ini</c>.</summary>
    public string Root { get; }

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public void Write(string relative, string text) => WriteBytes(relative, Encoding.ASCII.GetBytes(text.Replace("\n", "\r\n")));

    public void WriteBytes(string relative, byte[] bytes)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Delete(string relative) => File.Delete(PathOf(relative));

    /// <summary>Writes <c>EXE\freelancer.ini</c> with the DLLs of <c>[Resources]</c> after <c>InfoCards.dll</c>.</summary>
    public void Resources(params string[] dlls) =>
        Write(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n[Data]\nuniverse = universe\\universe.ini\n\n" +
                                     "[Resources]\nDLL = InfoCards.dll ; the infocards\n" +
                                     string.Concat(dlls.Select(d => $"DLL = {d}\n")));

    /// <summary>Writes <c>EXE\dacom.ini</c> with the retail lines of <c>[Libraries]</c> and the lines after
    /// them.</summary>
    public void Libraries(params string[] more) =>
        Write(@"EXE\dacom.ini", "[System]\nDACOM.AUTO = 1\n\n[Libraries]\n;;;dosfile.dll\n" +
                                string.Concat(RetailLibraries.Concat(more).Select(l => l + "\n")) +
                                "@include FL_DevOnlyLibs.ini\n\n[Software]\nx = 1\n");

    /// <summary>Writes the five code files with bytes that name the build, so two builds have other hashes.</summary>
    public void CodeFiles(string build)
    {
        foreach (var file in GameDetector.CodeFiles)
            WriteBytes(file, Encoding.ASCII.GetBytes($"code file {file} of build {build}"));
    }

    /// <summary>The manifest of the files of the game now: the code files and the INI files under DATA and EXE.</summary>
    public string Manifest(bool codeFilesOnly = false)
    {
        var files = GameDetector.CodeFiles.Select(PathOf).Where(File.Exists).ToList();
        if (!codeFilesOnly)
            files.AddRange(Directory.EnumerateFiles(Root, "*.ini", SearchOption.AllDirectories));
        return string.Concat(files.Distinct().Select(f =>
            DataDigest.Line(Path.GetRelativePath(Root, f), new FileInfo(f).Length, DataDigest.HashFile(f)) + "\n"));
    }

    public static KnownBuilds Builds(params (string Id, string Manifest)[] builds) =>
        KnownBuilds.FromText(builds.ToDictionary(b => b.Id, b => b.Manifest));

    public GameReport Detect(KnownBuilds? builds = null) => GameDetector.Detect(Root, builds ?? Builds());

    /// <summary>A DLL with a version block of the version.</summary>
    public static byte[] VersionDll(Version version)
    {
        var dll = new ResourceDll { VersionInfo = new VersionInfoResource(VersionBlock(version)) };
        using var stream = new MemoryStream();
        DllWriter.Write(dll, stream);
        return stream.ToArray();
    }

    /// <summary>A DLL with the strings and the infocards.</summary>
    public static byte[] TextDll(Dictionary<int, string> strings, Dictionary<int, string>? infocards = null)
    {
        var dll = new ResourceDll();
        foreach (var (id, text) in strings)
            dll.Strings[id] = text;
        foreach (var (id, text) in infocards ?? [])
            dll.Infocards[id] = text;
        using var stream = new MemoryStream();
        DllWriter.Write(dll, stream);
        return stream.ToArray();
    }

    /// <summary>A <c>VS_VERSIONINFO</c> block with a <c>VS_FIXEDFILEINFO</c> of the version as the file and the
    /// product version.</summary>
    public static byte[] VersionBlock(Version version)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)92);
        writer.Write((ushort)52);
        writer.Write((ushort)0);
        writer.Write(Encoding.Unicode.GetBytes("VS_VERSION_INFO\0"));
        writer.Write((ushort)0);
        var high = (uint)(version.Major << 16 | version.Minor);
        var low = (uint)(version.Build << 16 | version.Revision);
        foreach (var value in new uint[] { 0xFEEF04BD, 0x00010000, high, low, high, low, 0x3F, 0, 0x4, 0x2, 0, 0, 0 })
            writer.Write(value);
        return stream.ToArray();
    }

    /// <summary>The size, the write time, the attributes and the hash of each file, and the list of the files.</summary>
    public static SortedDictionary<string, string> Snapshot(string folder)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            result[Path.GetRelativePath(folder, file)] = File.Exists(file)
                ? $"{info.Length} {info.LastWriteTimeUtc.Ticks} {info.Attributes} {DataDigest.HashFile(file)}"
                : $"folder {new DirectoryInfo(file).Attributes}";
        }
        return result;
    }

    /// <summary>Clears the read-only attribute of each file, so that the folder can be deleted.</summary>
    public static void ClearReadOnly(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                ClearReadOnly(Folder);
                Directory.Delete(Folder, true);
            }
        }
        catch (IOException)
        {
            // A file that a test still holds open stays until the next temp cleanup.
        }
    }
}
