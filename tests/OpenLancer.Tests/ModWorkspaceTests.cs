using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>New mods, and the profile file that gets them.</summary>
public sealed class ModWorkspaceTests
{
    [Fact]
    public void CreateModWritesTheManifest()
    {
        using var ws = new TestWorkspace();
        var mods = Path.Combine(ws.Root, "newmods");

        var folder = ModWorkspace.CreateMod(mods, "proof", "Proof \"mod\" <1>");

        Assert.Equal(Path.Combine(mods, "proof"), folder);
        Assert.Equal("{\n  \"id\": \"proof\",\n  \"name\": \"Proof \\\"mod\\\" <1>\",\n  \"version\": \"1.0.0\",\n" +
                     "  \"formatVersion\": 2,\n  \"tier\": 1\n}\n",
            File.ReadAllText(Path.Combine(folder, ModManifest.FileName)));
        var manifest = ModManifest.Load(folder);
        Assert.Equal(("proof", "Proof \"mod\" <1>"), (manifest.Id, manifest.Name));
        Assert.Equal(ModManifest.DataTier, manifest.Tier);
        Assert.Equal(ModTargets.All, manifest.Targets);
        Assert.Throws<IOException>(() => ModWorkspace.CreateMod(mods, "proof", "Again"));
        Assert.Throws<ArgumentException>(() => ModWorkspace.CreateMod(mods, "9proof", "Digit"));
        Assert.False(Directory.Exists(Path.Combine(mods, "9proof")));
        // A blank name gives the id.
        Assert.Equal("proof2", ModManifest.Load(ModWorkspace.CreateMod(mods, "proof2", " ")).Name);
    }

    /// <summary>
    /// The profile keeps its byte order mark, its line ends and the fields that the editor does not know, for example
    /// a later engineFlags block (roadmap rule R7).
    /// </summary>
    [Fact]
    public void AddToProfileKeepsTheOtherFieldsAndTheByteOrderMark()
    {
        using var ws = new TestWorkspace();
        ws.Mod("low", new());
        var profile = ws.ProfilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllBytes(profile, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(
            "{ \"base\": \"../base\", \"engineFlags\": { \"fov\": 70.50 }, \"mods\": [ \"../mods/low\" ] }\r\n")]);
        var proof = ModWorkspace.CreateMod(Path.Combine(ws.Root, "mods"), "proof", "Proof");

        ModWorkspace.AddToProfile(profile, proof);

        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(
            "{\r\n  \"base\": \"../base\",\r\n  \"engineFlags\": {\r\n    \"fov\": 70.50\r\n  },\r\n  \"mods\": [\r\n" +
            "    \"../mods/low\",\r\n    \"../mods/proof\"\r\n  ]\r\n}\r\n")], File.ReadAllBytes(profile));
        Assert.Equal(["low", "proof"], ModProfile.Load(profile).Mods.Select(m => m.Id));
    }

    [Fact]
    public void AddToProfileAddsTheModsList()
    {
        using var ws = new TestWorkspace();
        var profile = ws.ProfilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, "{}");
        var proof = ModWorkspace.CreateMod(Path.Combine(ws.Root, "mods"), "proof", "Proof");

        ModWorkspace.AddToProfile(profile, proof);

        Assert.Equal(["proof"], ModProfile.Load(profile).Mods.Select(m => m.Id));
    }

    /// <summary>The reader skips JSON comments, so a write would lose them. The editor refuses such a profile.</summary>
    [Fact]
    public void AddToProfileRefusesAProfileWithCommentsOrTheSameModTwoTimes()
    {
        using var ws = new TestWorkspace();
        ws.Mod("low", new());
        var proof = ModWorkspace.CreateMod(Path.Combine(ws.Root, "mods"), "proof", "Proof");
        var profile = ws.ProfilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        const string commented = "{ \"mods\": [ \"../mods/low\" ] // the sample\n}";
        File.WriteAllText(profile, commented);

        var error = Assert.Throws<ModLoadException>(() => ModWorkspace.AddToProfile(profile, proof));

        Assert.Contains("comment", error.Message);
        Assert.Equal(commented, File.ReadAllText(profile));
        ws.WriteProfile("low");
        ModWorkspace.AddToProfile(profile, proof);
        var once = File.ReadAllText(profile);
        Assert.Throws<ModLoadException>(() => ModWorkspace.AddToProfile(profile, proof));
        Assert.Equal(once, File.ReadAllText(profile));
    }

    /// <summary>The profile must load with the new mod, so a mod that needs a mod outside the profile is refused.</summary>
    [Fact]
    public void AddToProfileRefusesAModWhoseDependencyIsNotInTheProfile()
    {
        using var ws = new TestWorkspace();
        ws.Mod("low", new());
        ws.Mod("needy", new(), ", \"dependsOn\": [ \"absent\" ]");
        ws.WriteProfile("low");
        var before = File.ReadAllBytes(ws.ProfilePath);

        var error = Assert.Throws<ModLoadException>(() => ModWorkspace.AddToProfile(ws.ProfilePath, ws.ModFolder("needy")));

        Assert.Contains("'absent'", error.Message);
        Assert.Equal(before, File.ReadAllBytes(ws.ProfilePath));
        Assert.Equal(["low"], ModProfile.Load(ws.ProfilePath).Mods.Select(m => m.Id));
    }
}
