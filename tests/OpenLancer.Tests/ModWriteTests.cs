using System.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The write guard of the Mods window (decision D587 and section 10.8 of the M11c design).</summary>
public sealed class ModWriteTests
{
    [Fact]
    public void AModInTheBaseFolderIsRefused()
    {
        using var game = new FakeGame();
        var mod = game.PathOf(@"DATA\mods\m");
        Directory.CreateDirectory(mod);
        var wrote = false;

        var reason = ModWrite.Run(mod, "m", WriteContext.Of([]), false, () => wrote = true);

        Assert.Equal(PublishMessages.ModInGameFolder(game.Root), reason);
        Assert.Equal($"This mod is in the game folder {game.Root}. OpenLancer does not write into a game folder.", reason);
        Assert.False(wrote);
        Assert.False(Directory.Exists(Path.Combine(mod, SaveTransaction.FolderName)));
    }

    [Fact]
    public void AModInALinkedGameIsRefused()
    {
        using var workspace = new PublishWorkspace();
        var linked = Path.Combine(workspace.Root, "linked-game");
        var mod = Path.Combine(linked, "mods", "m");
        Directory.CreateDirectory(mod);

        Assert.Equal(PublishMessages.ModInGameFolder(linked), ModWrite.Guard(mod, WriteContext.Of([linked])));
        Assert.Null(ModWrite.Guard(mod, WriteContext.Of([])));
        Assert.Equal(GameTexts.ProfileInGame, ModWrite.ProfileGuard(Path.Combine(linked, "p.json"), [linked]));
    }

    [Fact]
    public void AModThatAnotherProcessLocksIsRefused()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        using var held = ModLock.TryTake(mod, "m", out _);
        Assert.NotNull(held);
        var wrote = false;

        var reason = ModWrite.Run(mod, "m", WriteContext.Of([]), false, () => wrote = true);

        Assert.StartsWith("Another editor has mod 'm' open", reason);
        Assert.False(wrote);
        Assert.Equal(reason, ModLock.HeldBy(mod, "m"));
        Assert.Null(ModWrite.Run(mod, "m", WriteContext.Of([]), true, () => wrote = true));
        Assert.True(wrote);
    }

    [Fact]
    public void TheGuardReleasesTheLockAfterTheWrite()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        string? during = null;

        var reason = ModWrite.Run(mod, "m", WriteContext.Of([]), false, () => during = ModLock.HeldBy(mod, "m"));

        Assert.Null(reason);
        Assert.NotNull(during);
        Assert.Null(ModLock.HeldBy(mod, "m"));
        Assert.False(Directory.Exists(Path.Combine(mod, SaveTransaction.FolderName)));
    }

    [Fact]
    public void ARepoModNeedsTheAllowance()
    {
        using var workspace = new PublishWorkspace();
        PublishWorkspace.Write(Path.Combine(workspace.Root, RepoCheck.MarkerFile), "");
        var mod = workspace.Mod("m");

        Assert.Equal(PublishMessages.RepoMod, ModWrite.Guard(mod, WriteContext.Of([])));
        Assert.Null(ModWrite.Guard(mod, new WriteContext([], folder => folder == mod)));
    }

    [Fact]
    public void APendingSaveIsRefused()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(Path.Combine(mod, SaveTransaction.FolderName, "journal"), "olsave journal 1\n");

        Assert.Equal(PublishMessages.PendingSave, ModWrite.Guard(mod, WriteContext.Of([])));
    }
}
