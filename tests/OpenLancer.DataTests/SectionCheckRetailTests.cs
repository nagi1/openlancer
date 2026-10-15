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
        ["[collisiongroup] the engine does not know the key '------------------------------------------'. It ignores the line."] = 1,
        ["[countermeasure] 'force_gun_ori' has 2 lines. The engine reads only the last line."] = 3,
        ["[ship] the engine does not know the key 'camera_angular_slerp_multiplier'. It ignores the line."] = 1,
        ["[ship] the engine does not know the key 'distance_render'. It ignores the line."] = 2,
        ["[ship] the engine does not know the key 'docking_camera'. It ignores the line."] = 2,
        ["[ship] the engine does not know the key 'docking_sphere'. It ignores the line."] = 2,
        ["[simple] the engine does not know the key 'MinSpecLOD'. It ignores the line."] = 1,
    };

    /// <summary>
    /// The warnings of universe.ini and of the retail system files, as <see cref="KnownItemWarnings"/> gives them. Each
    /// trade lane zone has a string in its integer key <c>lane_id</c>, for example <c>br01_2</c>. The engine reads it
    /// as -1 and logs nothing. One row counts these warnings, with <c>&lt;text&gt;</c> in place of the string.
    /// </summary>
    private static readonly Dictionary<string, int> KnownSystemWarnings = new(StringComparer.Ordinal)
    {
        ["[background] 'nebulae' has 2 lines. The engine reads only the last line."] = 1,
        ["[lightsource] the engine does not know the key 'behavior'. It ignores the line."] = 1,
        ["[lightsource] the engine does not know the key 'color_curve'. It ignores the line."] = 2,
        ["[object] 'behavior' has 2 lines. The engine reads only the last line."] = 3,
        ["[object] 'ids_info' has 2 lines. The engine reads only the last line."] = 11,
        ["[object] 'pilot' has 2 lines. The engine reads only the last line."] = 1,
        ["[object] 'tradelane_space_name' has 2 lines. The engine reads only the last line."] = 2,
        ["[object] the engine does not know the key '260800'. It ignores the line."] = 6,
        ["[object] the engine does not know the key 'info_card'. It ignores the line."] = 1,
        ["[object] the engine does not know the key 'info_card_ids'. It ignores the line."] = 1,
        ["[object] the engine does not know the key 'info_ids'. It ignores the line."] = 4,
        ["[systeminfo] the engine does not know the key 'name'. It ignores the line."] = 31,
        ["[zone] 'ids_info' has 2 lines. The engine reads only the last line."] = 2,
        ["[zone] 'mission_type' has 2 lines. The engine reads only the last line."] = 1,
        ["[zone] the engine does not know the key 'pacedust'. It ignores the line."] = 3,
        ["[zone] the engine does not know the key 'spacedusr_maxparticles'. It ignores the line."] = 1,
        ["[zone] the engine does not know the key 'spacedust _maxparticles'. It ignores the line."] = 5,
        ["[zone] the engine does not know the key 'spacedust_masparticles'. It ignores the line."] = 2,
        ["[zone] the engine does not know the key 'spacedust_maxdust'. It ignores the line."] = 3,
        ["[zone] the engine reads '<text>' in a 'lane_id' line as the integer -1."] = 91,
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
    [GeneratedRegex("(?<start>^the engine reads ')[^']*")]
    private static partial Regex EngineString();
}
