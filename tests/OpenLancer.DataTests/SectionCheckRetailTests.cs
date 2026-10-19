using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the fast checks (<c>EditSession.Check</c>): the engine reads each retail section of the item files,
/// of universe.ini and of the star system files, so the checks find no error in them. The rules of the checks come
/// from the generated parsers by hand, so this proof finds a rule that is too strict, for example a color that needs
/// four values.
/// </summary>
public sealed partial class SectionCheckRetailTests(ITestOutputHelper output)
{
    /// <summary>
    /// The warnings of the retail item files, by section type and message, with their count. Each one is a real
    /// property of the retail data, for example a key that the engine does not read.
    /// </summary>
    private static readonly Dictionary<string, int> KnownItemWarnings = new(StringComparer.Ordinal)
    {
        ["[collisiongroup] the section type has no key '------------------------------------------'. The game ignores the line."] = 1,
        ["[countermeasure] 'force_gun_ori' has 2 lines. The game reads only the last line."] = 3,
        ["[ship] the section type has no key 'camera_angular_slerp_multiplier'. The game ignores the line."] = 1,
        ["[ship] the section type has no key 'distance_render'. The game ignores the line."] = 2,
        ["[ship] the section type has no key 'docking_camera'. The game ignores the line."] = 2,
        ["[ship] the section type has no key 'docking_sphere'. The game ignores the line."] = 2,
        ["[simple] the section type has no key 'MinSpecLOD'. The game ignores the line."] = 1,
    };

    /// <summary>
    /// The warnings of universe.ini and of the retail system files, as <see cref="KnownItemWarnings"/> gives them. Each
    /// trade lane zone has a string in <c>lane_id</c>, for example <c>br01_2</c>. The retail game never reads the key,
    /// so the checks skip it (decision D513 of the console filter design), and no row counts these lines.
    /// </summary>
    private static readonly Dictionary<string, int> KnownSystemWarnings = new(StringComparer.Ordinal)
    {
        ["[background] 'nebulae' has 2 lines. The game reads only the last line."] = 1,
        ["[lightsource] the section type has no key 'behavior'. The game ignores the line."] = 1,
        ["[lightsource] the section type has no key 'color_curve'. The game ignores the line."] = 2,
        ["[object] 'behavior' has 2 lines. The game reads only the last line."] = 3,
        ["[object] 'ids_info' has 2 lines. The game reads only the last line."] = 11,
        ["[object] 'pilot' has 2 lines. The game reads only the last line."] = 1,
        ["[object] 'tradelane_space_name' has 2 lines. The game reads only the last line."] = 2,
        ["[object] the section type has no key '260800'. The game ignores the line."] = 6,
        ["[object] the section type has no key 'info_card'. The game ignores the line."] = 1,
        ["[object] the section type has no key 'info_card_ids'. The game ignores the line."] = 1,
        ["[object] the section type has no key 'info_ids'. The game ignores the line."] = 4,
        ["[systeminfo] the section type has no key 'name'. The game ignores the line."] = 31,
        ["[zone] 'ids_info' has 2 lines. The game reads only the last line."] = 2,
        ["[zone] 'mission_type' has 2 lines. The game reads only the last line."] = 1,
        ["[zone] the section type has no key 'pacedust'. The game ignores the line."] = 3,
        ["[zone] the section type has no key 'spacedusr_maxparticles'. The game ignores the line."] = 1,
        ["[zone] the section type has no key 'spacedust _maxparticles'. The game ignores the line."] = 5,
        ["[zone] the section type has no key 'spacedust_masparticles'. The game ignores the line."] = 2,
        ["[zone] the section type has no key 'spacedust_maxdust'. The game ignores the line."] = 3,
    };

    [Fact]
    public void TheRetailSectionsOfTheItemFilesHaveNoError()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "check");
        var session = workspace.Open();
        var paths = Enum.GetValues<ItemFileKind>().SelectMany(session.List).Select(r => r.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var (sections, warnings) = CheckAll(session, paths);

        // The proof reads the sections of all item files, so it cannot pass because it checks nothing.
        Assert.Equal(16, paths.Count);
        Assert.True(sections > 2000, $"{sections} sections");
        Assert.Equal(KnownItemWarnings, warnings);
    }

    [Fact]
    public void TheRetailSectionsOfTheSystemFilesHaveNoError()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "check");
        var session = workspace.Open();
        var paths = RetailChecks.SystemFiles(session);

        var (sections, warnings) = CheckAll(session, paths);

        // universe.ini and 53 system files.
        Assert.Equal(54, paths.Count);
        Assert.Equal(10_979, sections);
        Assert.Equal(KnownSystemWarnings, warnings);
    }

    /// <summary>
    /// Runs the fast checks on each section of the files, and asserts that they give no error. Returns the number of
    /// sections and the warnings by section type and message, with their count.
    /// </summary>
    private (int Sections, SortedDictionary<string, int> Warnings) CheckAll(EditSession session, IReadOnlyList<string> paths)
    {
        var sections = 0;
        var errors = new List<string>();
        var warnings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            foreach (var target in session.Sections(path))
            {
                sections++;
                foreach (var problem in session.Check(target))
                {
                    if (problem.Severity == DiagnosticSeverity.Error)
                    {
                        errors.Add($"{path}: {problem.Message}");
                        continue;
                    }
                    // The section type and the message without the section in front.
                    var message = problem.Message[(problem.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..];
                    var label = $"[{target.Id.Type.ToLowerInvariant()}] {EngineString().Replace(message, "${start}<text>")}";
                    warnings[label] = warnings.GetValueOrDefault(label) + 1;
                }
            }
        }

        output.WriteLine($"{paths.Count} files, {sections} sections");
        foreach (var error in errors)
            output.WriteLine("error: " + error);
        foreach (var (label, count) in warnings)
            output.WriteLine($"warning ({count}): {label}");
        Assert.Empty(errors);
        return (sections, warnings);
    }

    /// <summary>The string in the message of a string that the engine reads as an integer.</summary>
    [GeneratedRegex("(?<start>^the game reads ')[^']*")]
    private static partial Regex EngineString();
}
