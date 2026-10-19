using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>Remove from profile and the profile entry of a rename (decision D574 of the M11c design).</summary>
public sealed class ProfileFileTests
{
    [Fact]
    public void RemoveModKeepsTheOtherFieldsAndTheFolder()
    {
        using var workspace = new PublishWorkspace();
        var a = workspace.Mod("a-mod");
        var b = workspace.Mod("b-mod");
        var profile = Path.Combine(workspace.Profiles, "p.json");
        PublishWorkspace.Write(profile, "{\r\n  \"base\": \"../base\",\r\n  \"engineFlags\": { \"x\": 1 },\r\n" +
                                        "  \"mods\": [ \"../mods/a-mod\", \"../mods/b-mod\" ]\r\n}\r\n");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "base"));

        ModWorkspace.RemoveFromProfile(profile, a);

        var text = File.ReadAllText(profile);
        Assert.Contains("\r\n", text);
        Assert.Equal(1, JsonNode.Parse(text)!["engineFlags"]!["x"]!.GetValue<int>());
        Assert.Equal(["b-mod"], ModProfile.Load(profile).Mods.Select(m => m.Id));
        Assert.True(Directory.Exists(a));
        var error = Assert.Throws<ModLoadException>(() => ModWorkspace.RemoveFromProfile(profile, a));
        Assert.Contains("The profile has no mod in", error.Message);
        Assert.True(Directory.Exists(b));
    }

    [Fact]
    public void RemoveModRefusesAModThatAnotherModNeeds()
    {
        using var workspace = new PublishWorkspace();
        var a = workspace.Mod("a-mod");
        var b = workspace.Mod("b-mod", more: ", \"dependsOn\": [ \"a-mod\" ]");
        var profile = workspace.Profile("p", a, b);
        var before = File.ReadAllText(profile);

        var error = Assert.Throws<ModLoadException>(() => ModWorkspace.RemoveFromProfile(profile, a));

        Assert.EndsWith(PublishMessages.RemoveRefused("b-mod"), error.Message);
        Assert.EndsWith("Mod 'b-mod' needs this mod. Remove 'b-mod' first.", error.Message);
        Assert.Equal(before, File.ReadAllText(profile));
    }

    [Fact]
    public void RenameModKeepsTheFormOfEachEntry()
    {
        using var workspace = new PublishWorkspace();
        var a = workspace.Mod("a-mod");
        var profile = Path.Combine(workspace.Profiles, "p.json");
        var absolute = a.Replace("\\", "\\\\");
        PublishWorkspace.Write(profile, $$"""{ "mods": [ "../mods/a-mod", "{{absolute}}", "../mods/other" ] }""");
        var renamed = Path.Combine(workspace.Mods, "b-mod");

        var changed = ProfileFile.RenameMod(profile, a, renamed);

        var mods = JsonNode.Parse(File.ReadAllText(profile))!["mods"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.True(changed);
        Assert.Equal(["../mods/b-mod", renamed, "../mods/other"], mods);
        Assert.False(ProfileFile.RenameMod(profile, a, renamed));
    }
}
