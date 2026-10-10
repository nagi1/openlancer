using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;

namespace OpenLancer.Tests;

/// <summary>
/// A temporary folder with a small synthetic game install and mod folders. Deleted on dispose.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    public const string ResourcesSection = """
        [Resources]
        DLL = InfoCards.dll
        DLL = Missing.dll
        """;

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        Base = Path.Combine(Root, "base");
        Directory.CreateDirectory(Base);
        WriteBase(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n" + ResourcesSection);
        WriteBase(@"EXE\resources.dll", "not a real dll");
        WriteBase(@"EXE\InfoCards.dll", "not a real dll");
    }

    public string Root { get; }

    public string Base { get; }

    public void WriteBase(string path, string text) => Write(Path.Combine(Base, path), text);

    /// <summary>Writes a binary (BINI) file into the base folder. The file stores the exact value types of the
    /// sections, for example a boolean as a string.</summary>
    public void WriteBaseBini(string path, params Section[] sections)
    {
        var full = Path.Combine(Base, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = File.Create(full);
        IniWriter.WriteBini(stream, sections);
    }

    public Mod Mod(string id, Dictionary<string, string> files, string? manifestExtra = null)
    {
        var folder = Path.Combine(Root, "mods", id);
        Write(Path.Combine(folder, ModManifest.FileName),
            $$"""{ "id": "{{id}}", "formatVersion": 1 {{manifestExtra}} }""");
        foreach (var (path, text) in files)
            Write(Path.Combine(folder, path), text);
        return Data.Mods.Mod.Load(folder);
    }

    public ModOverlay Build(params Mod[] mods) => ModOverlay.Build(Base, mods, FakeDllWriter.Instance);

    /// <summary>The profile file that <see cref="WriteProfile"/> writes.</summary>
    public string ProfilePath => Path.Combine(Root, "profiles", "test.json");

    public string ModFolder(string id) => Path.Combine(Root, "mods", id);

    /// <summary>Writes a profile with the mods of this workspace, in load order.</summary>
    public void WriteProfile(params string[] modIds) =>
        Write(ProfilePath, $$"""{ "base": "../base", "mods": [ {{string.Join(", ", modIds.Select(id => $"\"../mods/{id}\""))}} ] }""");

    /// <summary>Loads the profile of <see cref="WriteProfile"/> and opens an edit session on it.</summary>
    public EditSession Session(string? activeModId) =>
        EditSession.Open(ModProfile.Load(ProfilePath), activeModId, FakeDllWriter.Instance);

    /// <summary>The files of a folder: the relative path to the content. An empty result when the folder is missing.</summary>
    /// <param name="exclude">A subfolder to leave out, or null.</param>
    public static SortedDictionary<string, string> Files(string folder, string? exclude = null)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
            return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file);
            if (exclude == null || !relative.StartsWith(exclude + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                result[relative] = Convert.ToHexString(File.ReadAllBytes(file));
        }
        return result;
    }

    /// <summary>Parses a merged file of the overlay.</summary>
    public static List<Section> Sections(ModOverlay overlay, string path)
    {
        using var stream = overlay.CreateFileSystem().Open(path);
        return IniFile.ParseFile(path, stream).ToList();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // A file that a test still holds open stays until the next temp cleanup.
        }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Encoding.ASCII);
    }

    /// <summary>Records the text it gets, instead of a real DLL.</summary>
    internal sealed class FakeDllWriter : IStringResourceWriter
    {
        public static readonly FakeDllWriter Instance = new();

        public byte[] Write(IReadOnlyDictionary<int, string> strings, IReadOnlyDictionary<int, string> infocards) =>
            Encoding.UTF8.GetBytes(string.Join("\n",
                strings.Select(s => $"S{s.Key}={s.Value}").Concat(infocards.Select(i => $"I{i.Key}={i.Value}"))));
    }
}
