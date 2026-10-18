using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// New profiles of the game link (section 5.3 of the M11a design), the base of a moved game, and the write guard of New
/// mod (decision D204).
/// </summary>
public sealed class ModWorkspaceProfileTests : IDisposable
{
    private readonly FakeGame game = new();
    private readonly string workspace;

    public ModWorkspaceProfileTests()
    {
        workspace = Path.Combine(game.Folder, "workspace");
    }

    public void Dispose() => game.Dispose();

    private string ProfilePath(string name) => Path.Combine(workspace, "profiles", name + ".json");

    [Fact]
    public void CreateProfileWritesTheBaseAndAnActiveStarterMod()
    {
        var (path, mod) = ModWorkspace.CreateProfile(workspace, "freelancer-1-0", game.Root, []);

        Assert.Equal(ProfilePath("freelancer-1-0"), path);
        Assert.Equal("my-mod", mod);
        var profile = ModProfile.Load(path);
        Assert.Equal(game.Root, profile.BaseFolder);
        Assert.Equal(["my-mod"], profile.Mods.Select(m => m.Id));
        Assert.Equal(Path.Combine(workspace, "mods", "my-mod"), profile.Mods[0].Folder);
        Assert.Equal("My mod", profile.Mods[0].Manifest.Name);
        // The profile names the game root by its absolute path, and the mod relative to the profile.
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(game.Root, (string?)json["base"]);
        Assert.Equal("../mods/my-mod", (string?)json["mods"]![0]);
        Assert.False(Directory.Exists(Path.Combine(game.Root, "profiles")));
    }

    [Fact]
    public void TheStarterModTakesTheFirstFreeName()
    {
        ModWorkspace.CreateProfile(workspace, "first", game.Root, []);

        var (path, mod) = ModWorkspace.CreateProfile(workspace, "second", game.Root, []);

        Assert.Equal("my-mod-2", mod);
        Assert.Equal(["my-mod-2"], ModProfile.Load(path).Mods.Select(m => m.Id));
    }

    [Fact]
    public void AnExistingProfileNameIsRefused()
    {
        ModWorkspace.CreateProfile(workspace, "first", game.Root, []);

        var error = Assert.Throws<IOException>(() => ModWorkspace.CreateProfile(workspace, "first", game.Root, []));

        Assert.Equal(GameTexts.ProfileNameExists, error.Message);
        Assert.Equal("first-2", ModWorkspace.FreeProfileName(workspace, "first"));
    }

    [Fact]
    public void AnExistingPlayFolderNameIsRefused()
    {
        Directory.CreateDirectory(Path.Combine(workspace, "profiles", "first.play"));

        Assert.Throws<IOException>(() => ModWorkspace.CreateProfile(workspace, "first", game.Root, []));
        Assert.False(File.Exists(ProfilePath("first")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("My Profile")]
    [InlineData("1st")]
    public void AnInvalidNameIsRefused(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => ModWorkspace.CreateProfile(workspace, name, game.Root, []));

        Assert.StartsWith(GameTexts.ProfileNameRule, error.Message);
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void AWorkspaceInTheGameRootIsRefused()
    {
        var error = Assert.Throws<IOException>(() => ModWorkspace.CreateProfile(game.Root, "p", game.Root, []));

        Assert.Equal(GameTexts.WorkspaceInGame, error.Message);
        Assert.False(Directory.Exists(Path.Combine(game.Root, "profiles")));
        Assert.False(Directory.Exists(Path.Combine(game.Root, "mods")));
        Assert.Throws<IOException>(() => ModWorkspace.CreateProfile(game.PathOf("DATA"), "p", game.Root, []));
    }

    [Fact]
    public void AWorkspaceInAnotherLinkedRootIsRefused()
    {
        using var other = new FakeGame();

        Assert.Throws<IOException>(() => ModWorkspace.CreateProfile(other.Root, "p", game.Root, [other.Root]));
    }

    [Fact]
    public void AWorkspaceInTheGameRootThroughAJunctionIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var link = Path.Combine(game.Folder, "link");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{game.Root}\"")
               {
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   RedirectStandardOutput = true,
               })!)
        {
            process.WaitForExit();
            Assert.SkipUnless(process.ExitCode == 0 && Directory.Exists(link), "mklink /J did not make the junction.");
        }

        try
        {
            Assert.Throws<IOException>(() => ModWorkspace.CreateProfile(link, "p", game.Root, []));
            Assert.Throws<IOException>(() => ModWorkspace.CreateMod(Path.Combine(link, "mods"), "m", "", game.Root));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void AFailedCreateLeavesNoFiles()
    {
        // A file in place of the mods folder makes the mod fail after the profile file exists.
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "mods"), "not a folder");

        Assert.ThrowsAny<IOException>(() => ModWorkspace.CreateProfile(workspace, "p", game.Root, []));

        Assert.False(File.Exists(ProfilePath("p")));
        Assert.Equal(["mods"], Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories)
            .Where(File.Exists).Select(Path.GetFileName));
    }

    [Fact]
    public void SetBaseKeepsOtherFieldsBomAndLineEnds()
    {
        using var moved = new FakeGame();
        var path = ProfilePath("p");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = "{\r\n  \"base\": \"C:/old/game\",\r\n  \"mods\": [],\r\n  \"engineFlags\": { \"x\": 1 }\r\n}\r\n";
        File.WriteAllBytes(path, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)]);

        ModWorkspace.SetBase(path, moved.Root);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var written = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("\r\n", written);
        Assert.DoesNotContain("\n", written.Replace("\r\n", ""));
        var json = JsonNode.Parse(written)!;
        Assert.Equal(moved.Root, (string?)json["base"]);
        Assert.Equal(1, (int?)json["engineFlags"]!["x"]);
        Assert.Equal(moved.Root, ModProfile.BaseFolderOf(path));
    }

    [Fact]
    public void SetBaseWorksWhenTheOldBaseIsGone()
    {
        var (path, _) = ModWorkspace.CreateProfile(workspace, "p", game.Root, []);
        using var moved = new FakeGame();
        Directory.Delete(game.Root, true);

        ModWorkspace.SetBase(path, moved.Root);

        Assert.Equal(moved.Root, ModProfile.Load(path).BaseFolder);
    }

    [Fact]
    public void SetBaseRefusesAFileWithAComment()
    {
        var path = ProfilePath("p");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ // the old game\n \"base\": \"C:/old\", \"mods\": [] }");
        var before = File.ReadAllText(path);

        Assert.Throws<ModLoadException>(() => ModWorkspace.SetBase(path, game.Root));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void BaseFolderOfReadsAMissingBase()
    {
        var (path, _) = ModWorkspace.CreateProfile(workspace, "p", game.Root, []);
        Directory.Delete(game.Root, true);

        Assert.Equal(game.Root, ModProfile.BaseFolderOf(path));
    }

    [Fact]
    public void AMissingBaseFolderSetsMissingBaseFolder()
    {
        var (path, _) = ModWorkspace.CreateProfile(workspace, "p", game.Root, []);
        Directory.Delete(game.Root, true);

        var error = Assert.Throws<ModLoadException>(() => ModProfile.Load(path));

        Assert.Equal(game.Root, error.MissingBaseFolder);
        Assert.Null(Assert.Throws<ModLoadException>(() => ModProfile.Load(Path.Combine(workspace, "none.json")))
            .MissingBaseFolder);
    }

    [Fact]
    public void NewModRefusesAModsFolderInTheBase()
    {
        var error = Assert.Throws<IOException>(() =>
            ModWorkspace.CreateMod(Path.Combine(game.Root, "mods"), "m", "", game.Root));

        Assert.Equal(GameTexts.ModsFolderInGame, error.Message);
        Assert.False(Directory.Exists(Path.Combine(game.Root, "mods")));
        // Without a base folder, the old rule stays.
        Assert.True(Directory.Exists(ModWorkspace.CreateMod(Path.Combine(workspace, "mods"), "m", "")));
    }
}
