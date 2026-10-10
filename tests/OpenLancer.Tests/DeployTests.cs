using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

public sealed class DeployTests
{
    [Fact]
    public void DeploysTheMergedViewAndRemovesStaleFiles()
    {
        using var ws = Workspace();
        var mod = ws.Mod("m", new() { [@"files\DATA\MODS\m\new.txt"] = "new" });
        var profile = Profile(ws, "p", mod.Folder);

        var first = ProfileDeployer.Deploy(ws.Build(mod), profile);
        File.WriteAllText(Path.Combine(first.Folder, "DATA", "stale.txt"), "stale");
        var second = ProfileDeployer.Deploy(ws.Build(), Profile(ws, "p"));

        Assert.True(File.Exists(Path.Combine(first.Folder, "EXE", "freelancer.ini")));
        Assert.Equal(2, second.Deleted);
        Assert.False(File.Exists(Path.Combine(first.Folder, "DATA", "MODS", "m", "new.txt")));
        Assert.Equal("complete", File.ReadAllText(Path.Combine(first.Folder, ProfileDeployer.MarkerFileName)));
        var config = File.ReadAllBytes(ProfileDeployer.GameConfigPath(profile));
        Assert.Equal([0xEF, 0xBB, 0xBF], config.Take(3));
    }

    [Fact]
    public void RefusesAFolderThatADeployDidNotMake()
    {
        using var ws = Workspace();
        var profile = Profile(ws, "p");
        var foreign = Path.Combine(ws.Root, "profiles", "p", "mine.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "keep me");

        Assert.Throws<DeployException>(() => ProfileDeployer.Deploy(ws.Build(), profile));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void RefusesAFolderThatOverlapsAMod()
    {
        using var ws = Workspace();
        // A profile next to its mod folder, with the same name: the deploy folder would be the mod folder.
        var mod = ws.Mod("m", new());
        var profilePath = Path.Combine(ws.Root, "mods", "m.json");
        File.WriteAllText(profilePath, JsonSerializer.Serialize(new { @base = ws.Base, mods = new[] { "m" } }));
        var profile = ModProfile.Load(profilePath);

        Assert.Throws<DeployException>(() => ProfileDeployer.Deploy(ws.Build(mod), profile));
        Assert.True(File.Exists(Path.Combine(mod.Folder, ModManifest.FileName)));
    }

    [Fact]
    public void RefusesAProfileWithoutName()
    {
        using var ws = Workspace();
        Assert.Throws<DeployException>(() => ProfileDeployer.Deploy(ws.Build(), Profile(ws, "")));
    }

    [Fact]
    public void RefusesAPathThatTheGameCannotRead()
    {
        using var ws = Workspace();
        Assert.Throws<DeployException>(() => ProfileDeployer.Deploy(ws.Build(), Profile(ws, "a,b")));
    }

    [Fact]
    public void AReadOnlyFileStopsTheDeployBeforeItWritesAnything()
    {
        using var ws = Workspace();
        var profile = Profile(ws, "p");
        var result = ProfileDeployer.Deploy(ws.Build(), profile);
        var readOnly = Path.Combine(result.Folder, "EXE", "freelancer.ini");
        var goods = Path.Combine(result.Folder, "DATA", "goods.ini");
        var goodsBefore = File.ReadAllText(goods);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var mod = ws.Mod("m", new()
        {
            [@"files\EXE\freelancer.ini"] = "[Freelancer]\n",
            [@"files\DATA\goods.ini"] = "[Good]\nnickname = changed",
        });

        try
        {
            Assert.Throws<DeployException>(() => ProfileDeployer.Deploy(ws.Build(mod), Profile(ws, "p", mod.Folder)));
            Assert.Equal(goodsBefore, File.ReadAllText(goods));
            Assert.Equal("complete", File.ReadAllText(Path.Combine(result.Folder, ProfileDeployer.MarkerFileName)));
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    [Fact]
    public void CopiesOfReadOnlyBaseFilesAreWritable()
    {
        using var ws = Workspace();
        foreach (var file in Directory.EnumerateFiles(ws.Base, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.ReadOnly);

        try
        {
            var result = ProfileDeployer.Deploy(ws.Build(), Profile(ws, "p"));
            Assert.All(Directory.EnumerateFiles(result.Folder, "*", SearchOption.AllDirectories),
                f => Assert.False(new FileInfo(f).IsReadOnly, f));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(ws.Base, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static TestWorkspace Workspace()
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\goods.ini", "[Good]\nnickname = a");
        return ws;
    }

    private static ModProfile Profile(TestWorkspace ws, string name, params string[] mods)
    {
        var folder = Path.Combine(ws.Root, "profiles");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { @base = ws.Base, mods }));
        return ModProfile.Load(path);
    }
}
