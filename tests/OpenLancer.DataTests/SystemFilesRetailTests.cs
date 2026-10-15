using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Validation;
using Xunit;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the system files of the edit session: the session finds the file of each retail system as the
/// engine finds it, Parse reads each section as the engine reads it, and an edit of a system file saves a patch with
/// only the changed key.
/// </summary>
public sealed class SystemFilesRetailTests(ITestOutputHelper output)
{
    private const int RetailSystems = 53;
    private const string Depot = "Li01_14_depot_water";

    /// <summary>The generated parsers of the section classes of universe.ini and of the system files, by section
    /// type.</summary>
    private static readonly Dictionary<string, SectionParse> Parsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = SectionParse.Of<Universe.StarSystem>(Universe.StarSystem.TryParse),
        ["base"] = SectionParse.Of<Universe.Base>(Universe.Base.TryParse),
        ["systeminfo"] = SectionParse.Of<Universe.SystemInfo>(Universe.SystemInfo.TryParse),
        ["archetype"] = SectionParse.Of<Universe.SystemPreloads>(Universe.SystemPreloads.TryParse),
        ["background"] = SectionParse.Of<Universe.SystemBackground>(Universe.SystemBackground.TryParse),
        ["ambient"] = SectionParse.Of<Universe.SystemAmbient>(Universe.SystemAmbient.TryParse),
        ["dust"] = SectionParse.Of<Universe.SystemDust>(Universe.SystemDust.TryParse),
        ["music"] = SectionParse.Of<Universe.SystemMusic>(Universe.SystemMusic.TryParse),
        ["nebula"] = SectionParse.Of<Universe.Nebula>(Universe.Nebula.TryParse),
        ["asteroids"] = SectionParse.Of<Universe.AsteroidField>(Universe.AsteroidField.TryParse),
        ["lightsource"] = SectionParse.Of<Universe.LightSource>(Universe.LightSource.TryParse),
        ["object"] = SectionParse.Of<Universe.SystemObject>(Universe.SystemObject.TryParse),
        ["encounterparameters"] = SectionParse.Of<Universe.EncounterParameter>(Universe.EncounterParameter.TryParse),
        ["texturepanels"] = SectionParse.Of<Universe.TexturePanelsRef>(Universe.TexturePanelsRef.TryParse),
        ["zone"] = SectionParse.Of<Universe.Zone>(Universe.Zone.TryParse),
    };

    static SystemFilesRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// Each retail system gets a path. The file exists in the edit view and is binary (BINI). The path is the folder
    /// of universe.ini and the file of the system that the engine loads (<c>StarSystem.SourceFile</c>).
    /// </summary>
    [Fact]
    public void EachRetailSystemHasTheFileThatTheEngineLoads()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var fs = session.EditView.CreateFileSystem();
        var db = EngineDataLoader.Load(fs).RequireComplete();
        var universeFolder = VfsPath.Parent(session.UniversePath!);

        var found = 0;
        foreach (var system in db.Systems)
        {
            var path = session.SystemPath(system.Nickname);
            Assert.True(path != null, $"{system.Nickname} has no path.");
            Assert.Equal(VfsPath.Normalize($@"{universeFolder}\{system.SourceFile}"), path, VfsPath.Comparer);
            Assert.True(fs.FileExists(path), path);
            Assert.True(IniDocument.Parse(path, fs.ReadAllBytes(path)).IsBinary, path);
            Assert.Equal(session.SystemSection(system.Nickname)!.Id.Key, system.Nickname, ignoreCase: true);
            found++;
        }

        output.WriteLine($"universe: {session.UniversePath}, systems: {found}");
        Assert.Equal(RetailSystems, found);
        Assert.Equal(RetailSystems, session.Sections(session.UniversePath!).Count(s => Is(s.Id.Type, "system")));
    }

    /// <summary>
    /// Parse reads each retail section of universe.ini and of the system files as the engine reads it. The engine log
    /// gets no line, and each value equals the value that the generated parser gives for the section of the file. Parse
    /// reads a [system] without its system file. The sections of Li01 have the counts that the design measured.
    /// </summary>
    [Fact]
    public void EachRetailSectionParsesAsTheEngineReadsIt()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var fs = session.EditView.CreateFileSystem();
        var paths = RetailChecks.SystemFiles(session);

        var parsed = new List<(SectionRef Target, Section Raw, SectionParse Parser, object? Value)>();
        var withoutParser = new SortedDictionary<string, int>(StringComparer.Ordinal);
        IReadOnlyList<string> lines;
        using (var capture = EngineLogCapture.Start())
        {
            foreach (var path in paths)
            {
                var targets = session.Sections(path);
                var raw = IniFile.ParseFile(path, fs).ToList();
                Assert.Equal(raw.Count, targets.Count);
                for (var i = 0; i < targets.Count; i++)
                {
                    if (Parsers.TryGetValue(targets[i].Id.Type, out var parser))
                        parsed.Add((targets[i], raw[i], parser, parser.Session(session, targets[i])));
                    else
                    {
                        // The editor knows no schema for the section, so Parse cannot read it.
                        Assert.Empty(session.Schema(targets[i]));
                        var type = targets[i].Id.Type.ToLowerInvariant();
                        withoutParser[type] = withoutParser.GetValueOrDefault(type) + 1;
                    }
                }
            }
            lines = capture.Stop();
        }

        Assert.Empty(lines);
        Assert.Equal(new SortedDictionary<string, int> { ["asteroidbillboards"] = 1, ["field"] = 1, ["time"] = 1 },
            withoutParser);
        Assert.Equal(10_976, parsed.Count);
        Assert.All(parsed, p => Assert.True(p.Value != null, $"{p.Target} gives no value."));
        // The direct parse reads the section of the file with all its lines, so it can log.
        var differences = parsed.Select(p => Difference(p.Parser.Direct(p.Raw), p.Value, p.Target.ToString()))
            .OfType<string>().ToList();
        foreach (var difference in differences.Take(20))
            output.WriteLine(difference);
        Assert.Empty(differences);

        var systems = parsed.Select(p => p.Value).OfType<Universe.StarSystem>().ToList();
        Assert.Equal(RetailSystems, systems.Count);
        Assert.All(systems, s => Assert.Equal(session.SystemPath(s.Nickname),
            VfsPath.Normalize($@"{VfsPath.Parent(session.UniversePath!)}\{s.File}"), VfsPath.Comparer));
        Assert.All(systems, s => Assert.Empty(s.Objects));

        var li01 = session.Sections(session.SystemPath("Li01")!);
        var counts = li01.GroupBy(s => s.Id.Type.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine($"Li01: {li01.Count} sections, " +
                         string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}")));
        Assert.Equal(612, li01.Count);
        Assert.Equal(140, counts["object"]);
        Assert.Equal(414, counts["zone"]);
        Assert.Equal(4, counts["lightsource"]);
    }

    /// <summary>
    /// The data form of the depot proof: a <c>SetLines</c> of <c>pos</c> on <c>Li01_14_depot_water</c> saves a
    /// patch with the nickname and the new <c>pos</c> only. The base file does not change. An undo and a save delete
    /// the patch, and a redo and a save write the same bytes again.
    /// </summary>
    [Fact]
    public void AnEditOfTheDepotPosSavesOnlyThatKey()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var li01 = session.SystemPath("Li01")!;
        var depot = new SectionRef(li01, new SectionIdentity("Object", Depot));
        var baseFile = Path.Combine(baseFolder, li01);
        var baseHash = Sha256(baseFile);
        Assert.Equal(["-38675, 0, -27705"], Lines(session, depot, "pos"));
        Assert.Empty(Lines(session, depot, "rotate"));

        session.Execute(EditCommand.SetLines(depot, "pos",
            [[new Int32Value(-37675), new Int32Value(0), new Int32Value(-27705)]]));
        Assert.Equal(new System.Numerics.Vector3(-37675, 0, -27705),
            session.Parse<Universe.SystemObject>(depot, Universe.SystemObject.TryParse)!.Pos);
        var saved = session.Save();

        Assert.True(saved.Saved);
        Assert.Empty(saved.Problems);
        var patch = Assert.Single(saved.Written);
        // A new patch file has the letter case of the base file (rule P8).
        Assert.Equal(@"patches\DATA\Universe\Systems\Li01\Li01.ini", Path.GetRelativePath(workspace.ModFolder, patch));
        var bytes = File.ReadAllBytes(patch);
        output.WriteLine(Encoding.UTF8.GetString(bytes));
        Assert.Equal(Encoding.ASCII.GetBytes($"[Object]\r\nnickname = {Depot}\r\npos = -37675, 0, -27705\r\n"), bytes);
        Assert.Equal(baseHash, Sha256(baseFile));

        session.Undo();
        var undone = session.Save();
        Assert.True(undone.Saved);
        Assert.Equal([patch], undone.Deleted);
        Assert.False(File.Exists(patch));

        session.Redo();
        Assert.True(session.Save().Saved);
        Assert.Equal(bytes, File.ReadAllBytes(patch));
        Assert.Equal(baseHash, Sha256(baseFile));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>
    /// The first difference between two values of the engine, or null. Objects of a LibreLancer class compare field
    /// by field, also the private fields. Lists compare item by item. Other values compare with Equals.
    /// </summary>
    private static string? Difference(object? a, object? b, string path)
    {
        if (a == null || b == null)
            return a == b ? null : $"{path}: {a ?? "null"} and {b ?? "null"}";
        var type = a.GetType();
        if (type != b.GetType())
            return $"{path}: {type} and {b.GetType()}";
        if (a is not string && a is IEnumerable items)
        {
            var itemsA = items.Cast<object?>().ToList();
            var itemsB = ((IEnumerable)b).Cast<object?>().ToList();
            return itemsA.Count != itemsB.Count
                ? $"{path}: {itemsA.Count} and {itemsB.Count} items"
                : itemsA.Select((item, i) => Difference(item, itemsB[i], $"{path}[{i}]")).FirstOrDefault(d => d != null);
        }
        if (type.IsClass && type.Namespace?.StartsWith("LibreLancer", StringComparison.Ordinal) == true)
        {
            for (var t = type; t != typeof(object); t = t.BaseType!)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.DeclaredOnly))
                {
                    if (Difference(field.GetValue(a), field.GetValue(b), $"{path}.{field.Name}") is { } difference)
                        return difference;
                }
            }
            return null;
        }
        return a.Equals(b) ? null : $"{path}: {a} and {b}";
    }

    private static string[] Lines(EditSession session, SectionRef target, string name) =>
        session.Get(target)!.Lines(name).Select(l => string.Join(", ", l.Values.Select(v => v.ToString()))).ToArray();

    private static string Sha256(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A parse of a section through the edit session, and the parse of the generated parser alone.</summary>
    private sealed record SectionParse(Func<EditSession, SectionRef, object?> Session, Func<Section, object?> Direct)
    {
        public static SectionParse Of<T>(SectionParser<T> parser) where T : class => new(
            (session, target) => session.Parse(target, parser),
            section => parser(section, out var value, null, IniParseProperties.Empty) ? value : null);
    }
}
