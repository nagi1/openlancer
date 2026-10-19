using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>Rename of a mod (decisions D562 and D588 and section 12.3 of the M11c design).</summary>
public sealed class ModFolderRenameTests
{
    private static RenamePlan Plan(PublishWorkspace workspace, string folder, string newId, params string[] recent) =>
        ModFolderRename.Plan(new RenameRequest(folder, newId, workspace.Root) { RecentProfiles = recent });

    private static SortedDictionary<string, string> Snapshot(string folder) =>
        Directory.Exists(folder) ? FakeGame.Snapshot(folder) : new SortedDictionary<string, string>();

    [Fact]
    public void RenamesTheFolderTheIdAndTheProfileEntries()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        var profile = workspace.Profile("p", mod);
        var absolute = Path.Combine(workspace.Root, "elsewhere", "q.json");
        PublishWorkspace.Write(absolute, JsonSerializer.Serialize(new { @base = Path.Combine(workspace.Root, "base"), mods = new[] { mod } }));

        var plan = Plan(workspace, mod, "new-id", absolute);
        var result = ModFolderRename.Run(plan);

        Assert.Null(plan.Refusal);
        Assert.Equal([profile, absolute], plan.Profiles);
        Assert.True(result.Done, result.Error);
        var renamed = Path.Combine(workspace.Mods, "new-id");
        Assert.False(Directory.Exists(mod));
        Assert.Equal("new-id", ModManifest.Load(renamed).Id);
        Assert.Equal("new-id", Assert.Single(ModProfile.Load(profile).Mods).Id);
        Assert.Equal("../mods/new-id", JsonNode.Parse(File.ReadAllText(profile))!["mods"]![0]!.GetValue<string>());
        Assert.Equal(renamed, JsonNode.Parse(File.ReadAllText(absolute))!["mods"]![0]!.GetValue<string>());
        Assert.Equal(4, result.Log.Count);
    }

    [Fact]
    public void NeverWritesAnotherMod()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        var other = workspace.Mod("other", more: ", \"loadAfter\": [ \"old-id\" ]");
        var before = Snapshot(other);

        var result = ModFolderRename.Run(Plan(workspace, mod, "new-id"));

        Assert.True(result.Done, result.Error);
        Assert.Equal(before, Snapshot(other));
    }

    [Fact]
    public void ADependsOnOfAnotherModRefuses()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        workspace.Mod("needs-it", more: ", \"dependsOn\": [ \"old-id\" ]");

        var plan = Plan(workspace, mod, "new-id");
        var result = ModFolderRename.Run(plan);

        Assert.Equal(PublishMessages.RenameRefusedDependsOn("needs-it", "old-id"), plan.Refusal);
        Assert.Equal("Mod 'needs-it' needs 'old-id' in its dependsOn list. OpenLancer does not change the files of another " +
                     "mod. Remove 'old-id' from that list first.", plan.Refusal);
        Assert.False(result.Done);
        Assert.True(Directory.Exists(mod));
    }

    [Fact]
    public void ALoadAfterOfAnotherModWarns()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        workspace.Mod("later", more: ", \"loadAfter\": [ \"old-id\" ]");

        var plan = Plan(workspace, mod, "new-id");

        Assert.Null(plan.Refusal);
        Assert.Equal([PublishMessages.RenameWarningLoadAfter("later", "old-id")], plan.Warnings);
    }

    [Theory]
    [InlineData("the move of the mod folder")]
    [InlineData("mod.json")]
    [InlineData("profile")]
    [InlineData("the move of the publish history")]
    public void AFailedStepRestoresEachFile(string failing)
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        var profile = workspace.Profile("p", mod);
        workspace.Profile("q", mod);
        PublishHistory.Update(new HistoryLocation(workspace.Exports("old-id"), "old-id", mod, []), "export",
            h => h.Mark("1.0.0", DateTime.UtcNow, ""));
        var before = Snapshot(workspace.Root);
        var plan = Plan(workspace, mod, "new-id");
        var steps = new List<string>();

        var result = ModFolderRename.Run(plan, step =>
        {
            steps.Add(step);
            if (step == failing || failing == "profile" && step == Path.Combine(workspace.Profiles, "q.json"))
                throw new IOException("The test stops this step.");
        });

        Assert.False(result.Done);
        Assert.StartsWith("The rename stopped at ", result.Error);
        Assert.EndsWith("The test stops this step. OpenLancer restored each changed file.", result.Error);
        Assert.Equal(before.Keys, Snapshot(workspace.Root).Keys);
        Assert.Equal(before.Where(f => !f.Value.StartsWith("folder")).Select(f => (f.Key, f.Value.Split(' ')[^1])),
            Snapshot(workspace.Root).Where(f => !f.Value.StartsWith("folder")).Select(f => (f.Key, f.Value.Split(' ')[^1])));
        string[] all = ["the move of the mod folder", "mod.json", profile, Path.Combine(workspace.Profiles, "q.json"),
            "the move of the publish history"];
        Assert.Equal(all.Take(steps.Count), steps);
    }

    [Fact]
    public void RefusesAPendingSave()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        PublishWorkspace.Write(Path.Combine(mod, SaveTransaction.FolderName, "journal"), "olsave journal 1\n");

        Assert.Equal(PublishMessages.RenamePendingSave, Plan(workspace, mod, "new-id").Refusal);
    }

    [Fact]
    public void RefusesAnIdInUse()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        workspace.Mod("taken");
        var differentFolder = Path.Combine(workspace.Mods, "folder-name");
        PublishWorkspace.Write(Path.Combine(differentFolder, ModManifest.FileName), """{ "id": "inner-id", "formatVersion": 1 }""");

        Assert.Equal(PublishMessages.IdInUse("taken"), Plan(workspace, mod, "taken").Refusal);
        Assert.Equal("A mod with the id inner-id exists. Use another id.", Plan(workspace, mod, "inner-id").Refusal);
        Assert.Equal(ModManifest.IdRule, Plan(workspace, mod, "Bad Id").Refusal);
        Assert.Equal(PublishMessages.IdInUse("old-id"), Plan(workspace, mod, "old-id").Refusal);
    }

    [Fact]
    public void RefusesARepoMod()
    {
        using var workspace = new PublishWorkspace();
        PublishWorkspace.Write(Path.Combine(workspace.Root, RepoCheck.MarkerFile), "");
        var mod = workspace.Mod("old-id");

        Assert.Equal(PublishMessages.RenameRepoMod, Plan(workspace, mod, "new-id").Refusal);
    }

    [Fact]
    public void RefusesAModThatAnotherEditorHolds()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        using var held = ModLock.TryTake(mod, "old-id", out _);

        Assert.StartsWith("Another editor has mod 'old-id' open", Plan(workspace, mod, "new-id").Refusal);
        Assert.Null(ModFolderRename.Plan(new RenameRequest(mod, "new-id", workspace.Root) { SessionHolds = true }).Refusal);
        Assert.Equal(PublishMessages.RenameBusy,
            ModFolderRename.Plan(new RenameRequest(mod, "new-id", workspace.Root) { Busy = true, SessionHolds = true }).Refusal);
    }

    [Fact]
    public void KeepsUnknownFieldsOfTheProfiles()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        var profile = Path.Combine(workspace.Profiles, "p.json");
        PublishWorkspace.Write(profile, "{\r\n  \"base\": \"../base\",\r\n  \"engineFlags\": { \"x\": 1 },\r\n" +
                                        "  \"mods\": [ \"../mods/old-id\" ]\r\n}\r\n");

        var result = ModFolderRename.Run(Plan(workspace, mod, "new-id"));

        Assert.True(result.Done, result.Error);
        var text = File.ReadAllText(profile);
        Assert.Contains("\r\n", text);
        Assert.Equal(1, JsonNode.Parse(text)!["engineFlags"]!["x"]!.GetValue<int>());
        Assert.Equal("../mods/new-id", JsonNode.Parse(text)!["mods"]![0]!.GetValue<string>());
    }

    [Fact]
    public void WarnsAfterAPublishedVersion()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        PublishHistory.Update(new HistoryLocation(workspace.Exports("old-id"), "old-id", mod, []), "mark",
            h => h.Mark("1.0.1", DateTime.UtcNow, ""));
        var missing = Path.Combine(workspace.Root, "gone", "r.json");

        var plan = Plan(workspace, mod, "new-id", missing);

        Assert.Contains(PublishMessages.RenameAfterRelease("1.0.1", "old-id"), plan.Warnings);
        Assert.Contains("EXE\\ol_old-id.dll", plan.Warnings.First(w => w.StartsWith("Players")));
        Assert.Contains(PublishMessages.RenameProfileNotFound(missing), plan.Warnings);
    }

    [Fact]
    public void MovesThePublishHistory()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("old-id");
        PublishHistory.Update(new HistoryLocation(workspace.Exports("old-id"), "old-id", mod, []), "mark",
            h => h.Mark("1.0.0", DateTime.UtcNow, ""));
        PublishWorkspace.Write(Path.Combine(workspace.Exports("old-id"), "1.0.0", "old-id-1.0.0-openlancer.zip"), "zip");

        var plan = Plan(workspace, mod, "new-id");
        var result = ModFolderRename.Run(plan);

        Assert.True(result.Done, result.Error);
        Assert.Equal(workspace.Exports("old-id"), plan.HistoryFrom);
        Assert.False(Directory.Exists(workspace.Exports("old-id")));
        Assert.True(File.Exists(Path.Combine(workspace.Exports("new-id"), "1.0.0", "old-id-1.0.0-openlancer.zip")));
        var (history, problem) = PublishHistory.Load(workspace.Exports("new-id"), "new-id");
        Assert.Null(problem);
        Assert.Equal("new-id", history.ModId);
        Assert.Null(history.FolderWarning(Path.Combine(workspace.Mods, "new-id")));
        Assert.True(history.IsPublished("1.0.0"));
    }
}
