using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The load of a hot reload refuses a play view with merge errors (section 12.2 of the M4 design), as the start of a play
/// refuses it. The game then keeps its data. <c>PlayReloadRetailTests</c> has the retail proofs of the reload.
/// </summary>
public sealed class PlayReloadTests
{
    [Fact]
    public void ALoadOfAViewWithMergeErrorsThrowsWithTheErrors()
    {
        using var ws = new TestWorkspace();
        ws.Mod("act", new() { [@"patches\DATA\missing.ini"] = "[Good]\nnickname = g1\nprice = 1\n" });
        ws.WriteProfile("act");

        var refused = Assert.Throws<ModLoadException>(() => PlayReload.Load(ws.ProfilePath, "act"));

        Assert.Contains("The mods of the play have merge errors:", refused.Message);
        Assert.Contains("does not exist.", refused.Message);
    }
}
