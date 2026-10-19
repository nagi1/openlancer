using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The draft of the publish page with Apply and Revert (decision D571 and section 10.7 of the M11c
/// design).</summary>
public sealed class PublishDraftTests
{
    private static readonly WriteContext Context = WriteContext.Of([]);

    private static JsonObject Json(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    [Fact]
    public void AnEditMarksTheField()
    {
        using var workspace = new PublishWorkspace();
        var draft = PublishDraft.Open(workspace.Mod("m"));

        draft.Values = draft.Values with { Name = "Other", Settings = draft.Values.Settings with { Tags = ["Ships"] } };

        Assert.True(draft.HasChanges);
        Assert.True(draft.IsChanged(PublishField.DisplayName));
        Assert.True(draft.IsChanged(PublishField.Tags));
        Assert.False(draft.IsChanged(PublishField.Version));
        Assert.False(draft.IsChanged(PublishField.Summary));
    }

    [Fact]
    public void RevertDropsTheDraft()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var draft = PublishDraft.Open(mod);
        draft.Values = draft.Values with { Settings = draft.Values.Settings with { Summary = "New." } };
        draft.AddImages([WriteImage(workspace, "shot.png")], []);

        draft.Revert();

        Assert.False(draft.HasChanges);
        Assert.Empty(draft.Imports);
        Assert.False(File.Exists(Path.Combine(mod, PublishSettings.FileName)));
    }

    [Fact]
    public void ApplyWritesModJsonAndPublishJsonAndKeepsUnknownFields()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m", more: ", \"loadAfter\": [ \"x\" ], \"later\": 5");
        PublishWorkspace.Write(Path.Combine(mod, PublishSettings.FileName), """{ "formatVersion": 1, "kept": "yes" }""");
        var draft = PublishDraft.Open(mod);
        draft.Values = draft.Values with
        {
            Name = "Starrunner Deluxe",
            Version = "1.1.0",
            Settings = draft.Values.Settings with { Summary = "A fast ship.", Authors = ["Ann"] },
        };

        var result = draft.Apply(Context, sessionHolds: false);

        Assert.Equal(ApplyState.Applied, result.State);
        Assert.False(result.ReopenProfile);
        Assert.False(draft.HasChanges);
        var manifest = Json(Path.Combine(mod, ModManifest.FileName));
        Assert.Equal("Starrunner Deluxe", manifest["name"]!.GetValue<string>());
        Assert.Equal("1.1.0", manifest["version"]!.GetValue<string>());
        Assert.Equal(5, manifest["later"]!.GetValue<int>());
        Assert.Equal("x", manifest["loadAfter"]![0]!.GetValue<string>());
        Assert.Null(manifest["targets"]);
        var publish = Json(Path.Combine(mod, PublishSettings.FileName));
        Assert.Equal("yes", publish["kept"]!.GetValue<string>());
        Assert.Equal("A fast ship.", PublishSettings.Load(mod).Settings.Summary);
        Assert.Equal("Starrunner Deluxe", ModManifest.Load(mod).Name);
    }

    [Fact]
    public void ApplyAsksWhenAFileChangedOnDisk()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var draft = PublishDraft.Open(mod);
        draft.Values = draft.Values with { Settings = draft.Values.Settings with { Summary = "Mine." } };
        PublishWorkspace.Write(Path.Combine(mod, PublishSettings.FileName), """{ "formatVersion": 1, "summary": "Theirs." }""");

        var asked = draft.Apply(Context, false);
        var theirs = PublishSettings.Load(mod).Settings.Summary;
        var replaced = draft.Apply(Context, false, replaceChangedFiles: true);

        Assert.Equal(ApplyState.ChangedOnDisk, asked.State);
        Assert.Equal("publish.json changed outside this window. Load the file again, or replace it with your changes?",
            asked.Message);
        Assert.Equal("Theirs.", theirs);
        Assert.Equal(ApplyState.Applied, replaced.State);
        Assert.Equal("Mine.", PublishSettings.Load(mod).Settings.Summary);
    }

    [Fact]
    public void ApplyCopiesNewImagesAndAsksBeforeItDeletes()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var old = PublishWorkspace.Screenshot(mod, "old.png", mark: 1);
        var draft = PublishDraft.Open(mod);
        var source = WriteImage(workspace, "My Shot (1).PNG", mark: 2);

        var refused = draft.AddImages([source, Path.Combine(workspace.Root, "missing.png")], []);
        var result = draft.Apply(Context, false);

        Assert.Single(refused);
        Assert.Equal(ApplyState.Applied, result.State);
        Assert.Equal(["publish/screenshots/my-shot-1.png"], PublishSettings.Load(mod).Settings.Screenshots.Select(s => s.File));
        Assert.True(File.Exists(Path.Combine(mod, "publish", "screenshots", "my-shot-1.png")));
        var unused = Path.Combine(mod, old.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal([unused], result.UnusedImages);
        Assert.True(File.Exists(unused), "Apply asks before it deletes an image.");
        Assert.Equal("Delete 1 images from publish\\screenshots? No entry of the list uses them.", PublishMessages.DeleteImages(1));

        Assert.Empty(draft.DeleteImages(result.UnusedImages, Context, false));
        Assert.False(File.Exists(unused));
    }

    [Fact]
    public void AFailedCopyChangesNoJsonFile()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var manifest = File.ReadAllBytes(Path.Combine(mod, ModManifest.FileName));
        var draft = PublishDraft.Open(mod);
        draft.Values = draft.Values with { Name = "Other" };
        draft.AddImages([WriteImage(workspace, "a.png", 1), WriteImage(workspace, "b.png", 2)], []);
        var copies = 0;

        var result = draft.Apply(Context, false, false, null, (from, to) =>
        {
            if (++copies == 2)
                throw new IOException("The disk is full.");
            File.Copy(from, to);
        });

        Assert.Equal(ApplyState.Refused, result.State);
        Assert.Contains("The disk is full.", result.Message);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(mod, ModManifest.FileName)));
        Assert.False(File.Exists(Path.Combine(mod, PublishSettings.FileName)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(mod, "publish", "screenshots")));
        Assert.True(draft.HasChanges);
    }

    [Fact]
    public void ATargetChangeAsksToOpenTheProfileAgain()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var draft = PublishDraft.Open(mod);

        draft.RemoveTarget(ModTargets.Vanilla);
        var result = draft.Apply(Context, false);

        Assert.True(result.ReopenProfile);
        Assert.Equal([ModTargets.HdEdition, ModTargets.OpenLancer], ModManifest.Load(mod).Targets);
    }

    [Fact]
    public void AModJsonWithACommentIsReadOnly()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(Path.Combine(mod, ModManifest.FileName),
            "{ // the id\n \"id\": \"m\", \"name\": \"Starrunner\", \"version\": \"1.0.0\", \"formatVersion\": 2 }");
        var draft = PublishDraft.Open(mod);
        var before = File.ReadAllText(Path.Combine(mod, ModManifest.FileName));
        draft.Values = draft.Values with { Name = "Other" };

        var result = draft.Apply(Context, false);

        Assert.Equal(PublishMessages.ModJsonComment, draft.ManifestReadOnly);
        Assert.Equal(PublishMessages.ModJsonComment, result.Message);
        Assert.Equal(before, File.ReadAllText(Path.Combine(mod, ModManifest.FileName)));
        draft.Revert();
        draft.Values = draft.Values with { Settings = draft.Values.Settings with { Summary = "Still writes." } };
        Assert.Equal(ApplyState.Applied, draft.Apply(Context, false).State);
    }

    [Fact]
    public void AnUnsafeVersionIsRefused()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var draft = PublishDraft.Open(mod);
        draft.Values = draft.Values with { Version = @"..\..\x" };

        var result = draft.Apply(Context, false);

        Assert.Equal(ApplyState.Refused, result.State);
        Assert.Equal(PublishMessages.VersionUnsafe(@"..\..\x"), result.Message);
        Assert.Equal("1.0.0", ModManifest.Load(mod).Version);
    }

    [Fact]
    public void ARepoModNeedsTheEditPermission()
    {
        using var workspace = new PublishWorkspace();
        PublishWorkspace.Write(Path.Combine(workspace.Root, RepoCheck.MarkerFile), "");
        var mod = workspace.Mod("m");
        var draft = PublishDraft.Open(mod);
        draft.Values = draft.Values with { Settings = draft.Values.Settings with { Summary = "New." } };

        var refused = draft.Apply(Context, false);
        var images = draft.AddImages([WriteImage(workspace, "a.png")], []);
        var allowed = draft.Apply(new WriteContext([], _ => true), false);

        Assert.Equal(PublishMessages.RepoMod, refused.Message);
        Assert.Equal([PublishMessages.ImageInRepoMod], images);
        Assert.Equal(ApplyState.Applied, allowed.State);
    }

    [Fact]
    public void TheMarkPutsTheLinkIntoTheDraft()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var draft = PublishDraft.Open(mod);
        PublishHistory.Update(new HistoryLocation(workspace.Exports("m"), "m", mod, []), "mark",
            h => h.Mark("1.0.0", System.DateTime.UtcNow, "https://example.org/mods/1"));

        var changed = draft.TakePageLink("https://example.org/mods/1");

        Assert.True(changed);
        Assert.True(draft.IsChanged(PublishField.PageUrl));
        Assert.Equal("https://example.org/mods/1", draft.Values.Settings.PageUrl);
        Assert.False(File.Exists(Path.Combine(mod, PublishSettings.FileName)));
        Assert.False(draft.TakePageLink("https://example.org/mods/1"));
        Assert.True(PublishHistory.Load(workspace.Exports("m"), "m").History.IsPublished("1.0.0"));
    }

    [Fact]
    public void IncreaseVersionAddsAChangelogEntry()
    {
        using var workspace = new PublishWorkspace();
        var draft = PublishDraft.Open(workspace.Mod("m"));

        var next = draft.IncreaseVersion(VersionPart.Patch, ["1.0.1"]);

        Assert.Equal("1.0.2", next);
        Assert.Equal("1.0.2", draft.Values.Version);
        Assert.Equal("1.0.2", draft.Values.Settings.Changelog[0].Version);
        Assert.True(draft.IsChanged(PublishField.Changelog));
    }

    /// <summary>Writes a PNG image outside the mod, and returns its path.</summary>
    private static string WriteImage(PublishWorkspace workspace, string name, byte mark = 0)
    {
        var path = Path.Combine(workspace.Root, "pictures", name);
        PublishWorkspace.WriteBytes(path, PublishWorkspace.Png(800, 600, mark));
        return path;
    }
}
