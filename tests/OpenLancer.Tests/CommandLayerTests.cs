using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenLancer.Data.Editing;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The places of the shared command layer (section 4.0.2 of the M6b design, MCP1 section 12.1 item 3): OpenLancer.Data
/// and OpenLancer.Studio make no ImGui call and have no reference to the MCP SDK, and OpenLancer.Data has no reference to
/// OpenLancer.Studio.
/// </summary>
public sealed class CommandLayerTests
{
    /// <summary>
    /// The metadata of an assembly names each assembly whose types its code uses. So a call of ImGui in the Studio
    /// would name an ImGui assembly. The test does not follow the references further: the engine references its own
    /// ImGui wrapper for its debug view, and the Runtime references the engine.
    /// </summary>
    [Fact]
    public void TheStudioAssemblyUsesNoImGuiNoEditorAndNoMcpSdk()
    {
        var studio = typeof(FieldEdit).Assembly;
        Assert.Equal("OpenLancer.Studio", studio.GetName().Name);

        var names = studio.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.Contains("OpenLancer.Runtime", names);
        Assert.DoesNotContain(names, Forbidden);
    }

    [Fact]
    public void TheDataAssemblyReachesNoStudioNoImGuiAndNoMcpSdk()
    {
        var reached = Reached(typeof(EditSession).Assembly);

        Assert.DoesNotContain("OpenLancer.Studio", reached);
        Assert.DoesNotContain(reached, Forbidden);
    }

    [Fact]
    public void TheRepoRuleGivesAReasonOnlyForAModInTheRepo()
    {
        using var ws = new TestWorkspace();
        var outside = ws.Mod("ol_outside", new());
        var repo = System.IO.Path.Combine(ws.Root, "repo");
        System.IO.Directory.CreateDirectory(repo);
        System.IO.File.WriteAllText(System.IO.Path.Combine(repo, OpenLancer.Data.Mods.RepoCheck.MarkerFile), "");
        var folder = System.IO.Path.Combine(repo, "mods", "ol_inside");
        System.IO.Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, OpenLancer.Data.Mods.ModManifest.FileName),
            """{ "id": "ol_inside", "formatVersion": 1 }""");
        var inside = OpenLancer.Data.Mods.Mod.Load(folder);

        Assert.Null(RepoMods.Reason(outside));
        Assert.Equal("The mod 'ol_inside' is part of the OpenLancer repo, so its files are tracked files.",
            RepoMods.Reason(inside));
    }

    /// <summary>The names of the assemblies of OpenLancer and the engine that an assembly reaches through its
    /// references, the assembly included. A reference that does not load counts by its name.</summary>
    private static HashSet<string> Reached(Assembly start)
    {
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.GetName().Name! };
        var queue = new Queue<Assembly>([start]);
        while (queue.Count > 0)
        {
            foreach (var name in queue.Dequeue().GetReferencedAssemblies())
            {
                if (name.Name is not { } text || text.StartsWith("System", StringComparison.Ordinal) ||
                    text.StartsWith("Microsoft", StringComparison.Ordinal) || text == "netstandard" || !reached.Add(text))
                    continue;
                try
                {
                    queue.Enqueue(Assembly.Load(name));
                }
                catch (Exception e) when (e is System.IO.FileNotFoundException or System.IO.FileLoadException or
                                               BadImageFormatException)
                {
                    // The name still counts.
                }
            }
        }
        return reached;
    }

    private static bool Forbidden(string name) =>
        name.Contains("ImGui", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LibreLancer.ImUI", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OpenLancer.Editor", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ModelContextProtocol", StringComparison.OrdinalIgnoreCase);
}
