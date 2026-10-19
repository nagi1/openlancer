using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;

namespace OpenLancer.Tests;

/// <summary>
/// Synthetic mission scripts for the tests of the mission studio (section 18.1 of the M6a design). All names are
/// invented. The scripts are text INI files.
/// </summary>
internal static class MissionTestData
{
    /// <summary>The install path of the test script.</summary>
    public const string Path = @"DATA\MISSIONS\TEST\test.ini";

    /// <summary>The document of a script text.</summary>
    public static MissionDocument Document(string text, string path = Path) =>
        MissionDocument.Parse(path, Encoding.ASCII.GetBytes(text.ReplaceLineEndings("\r\n")));

    /// <summary>The index of a script text, with the names of a world or none.</summary>
    public static MissionIndex Index(string text, MissionWorld? world = null, IEngineCoverage? coverage = null) =>
        MissionIndex.Build(Document(text), MissionSchema.Retail, world ?? MissionWorld.Empty,
            coverage ?? FullCoverage.Instance);

    /// <summary>The problems of the checks of a script text.</summary>
    public static IReadOnlyList<Diagnostic> Check(string text, MissionWorld? world = null,
        MissionCheckOptions? options = null, IEngineCoverage? coverage = null) =>
        MissionChecks.Run(Index(text, world, coverage), options);

    /// <summary>The problems of one rule.</summary>
    public static List<Diagnostic> Rule(IEnumerable<Diagnostic> problems, string rule) =>
        problems.Where(p => p.Rule == rule).ToList();

    /// <summary>A world with names of each kind and the files of a set.</summary>
    public static MissionWorld World(Dictionary<MissionValueKind, string[]>? names = null, params string[] files) =>
        new((names ?? []).ToDictionary(p => p.Key, p => (IEnumerable<string>)p.Value),
            relative => files.Contains(relative, StringComparer.OrdinalIgnoreCase));

    /// <summary>A start trigger and a success, so that the graph rules of a test script find nothing.</summary>
    public const string Story = """
        [Mission]

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = t_end

        [Trigger]
        nickname = t_end
        Cnd_True = no_params
        Act_ChangeState = SUCCEED
        """;
}

/// <summary>A folder with a game data tree for a run of the mission checks. Deleted on dispose.</summary>
internal sealed class MissionFolder : IDisposable
{
    public MissionFolder()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        Write(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\DATA\n");
    }

    public string Root { get; }

    public void Write(string relative, string text)
    {
        var path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\r\n"), Encoding.ASCII);
    }

    public FileSystem Files() => FileSystem.FromPath(Root);

    /// <summary>The sections of <c>freelancer.ini</c>.</summary>
    public IReadOnlyList<Section> FreelancerIni() =>
        OpenLancer.Data.Ini.IniDocument.Parse(@"EXE\freelancer.ini",
            File.ReadAllBytes(System.IO.Path.Combine(Root, @"EXE\freelancer.ini"))).Sections;

    /// <summary>The inputs of a run over the folder, with another folder as the base game or none.</summary>
    public MissionInputs Inputs(MissionFolder? baseFolder = null, IMissionLedger? ledger = null, string? activeMod = null,
        int generation = 0, MissionWorld? world = null, MissionCheckOptions? options = null) => new()
    {
        EditView = Files(),
        BaseView = baseFolder?.Files(),
        FreelancerIni = FreelancerIni(),
        Ledger = ledger ?? NoMods.Instance,
        ActiveModId = activeMod,
        Generation = generation,
        World = world ?? MissionWorld.Empty,
        Options = options ?? new MissionCheckOptions(),
    };

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, true);
    }
}

/// <summary>A ledger of the tests: the mods that change each file and section.</summary>
internal sealed class TestLedger : IMissionLedger
{
    public Dictionary<string, List<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<(string, string), List<string>> Sections { get; } = [];

    public Dictionary<string, string> Replaced { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ModsThatChangeFile(string path) => Files.GetValueOrDefault(path) ?? [];

    public IReadOnlyList<string> ModsThatChangeSection(string path, SectionIdentity section) =>
        Sections.GetValueOrDefault((path.ToLowerInvariant(), section.ToString().ToLowerInvariant())) ?? [];

    public string? ReplacedBy(string path) => Replaced.GetValueOrDefault(path);

    public void Change(string path, string mod, SectionIdentity? section = null)
    {
        if (!Files.TryGetValue(path, out var mods))
            Files[path] = mods = [];
        mods.Add(mod);
        if (section != null)
        {
            var key = (path.ToLowerInvariant(), section.ToString().ToLowerInvariant());
            if (!Sections.TryGetValue(key, out var list))
                Sections[key] = list = [];
            list.Add(mod);
        }
    }
}
