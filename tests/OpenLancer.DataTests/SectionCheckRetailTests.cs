using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Strings;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the fast checks (<c>EditSession.Check</c>): the engine reads each retail section of the item files,
/// so the checks find no error in them. The rules of the checks come from the generated parsers by hand, so this proof
/// finds a rule that is too strict, for example a color that needs four values.
/// </summary>
public sealed class SectionCheckRetailTests(ITestOutputHelper output)
{
    /// <summary>
    /// The warnings of the retail item files, by section type and message, with their count. Each one is a real
    /// property of the retail data, for example a key that the engine does not read.
    /// </summary>
    private static readonly Dictionary<string, int> KnownWarnings = new(StringComparer.Ordinal)
    {
        ["[collisiongroup] the engine does not know the key '------------------------------------------'. It ignores the line."] = 1,
        ["[countermeasure] 'force_gun_ori' has 2 lines. The engine reads only the last line."] = 3,
        ["[ship] the engine does not know the key 'camera_angular_slerp_multiplier'. It ignores the line."] = 1,
        ["[ship] the engine does not know the key 'distance_render'. It ignores the line."] = 2,
        ["[ship] the engine does not know the key 'docking_camera'. It ignores the line."] = 2,
        ["[ship] the engine does not know the key 'docking_sphere'. It ignores the line."] = 2,
        ["[simple] the engine does not know the key 'MinSpecLOD'. It ignores the line."] = 1,
    };

    [Fact]
    public void TheRetailSectionsOfTheItemFilesHaveNoError()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var root = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var profilePath = Path.Combine(root, "profiles", "retail.json");
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            File.WriteAllText(profilePath, JsonSerializer.Serialize(new { @base = baseFolder, mods = Array.Empty<string>() }));
            var session = EditSession.Open(ModProfile.Load(profilePath), null, DllStringResourceWriter.Instance);
            var fs = session.EditView.CreateFileSystem();
            var paths = Enum.GetValues<ItemFileKind>().SelectMany(session.List).Select(r => r.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var sections = 0;
            var errors = new List<string>();
            var warnings = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var document = IniDocument.Parse(path, fs.ReadAllBytes(path));
                foreach (var id in SectionIdentity.Identify(document.Sections))
                {
                    sections++;
                    foreach (var problem in session.Check(new SectionRef(path, id)))
                    {
                        if (problem.Severity == DiagnosticSeverity.Error)
                            errors.Add($"{path}: {problem.Message}");
                        else
                        {
                            // The section type and the message without the section in front.
                            var message = problem.Message[(problem.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..];
                            var label = $"[{id.Type.ToLowerInvariant()}] {message}";
                            warnings[label] = warnings.GetValueOrDefault(label) + 1;
                        }
                    }
                }
            }

            output.WriteLine($"{paths.Count} files, {sections} sections");
            foreach (var error in errors)
                output.WriteLine("error: " + error);
            foreach (var (label, count) in warnings)
                output.WriteLine($"warning ({count}): {label}");
            // The proof reads the sections of all item files, so it cannot pass because it checks nothing.
            Assert.Equal(16, paths.Count);
            Assert.True(sections > 2000, $"{sections} sections");
            Assert.Empty(errors);
            Assert.Equal(KnownWarnings, warnings);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
