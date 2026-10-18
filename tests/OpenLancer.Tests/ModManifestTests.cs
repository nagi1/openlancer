using System;
using System.IO;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// Format version 2 of <c>mod.json</c> (milestone M25): the tier and the targets of a mod. A mod of format version 1
/// is a tier-1 mod for each target. A reader refuses an unknown format version, tier or target with a clear message.
/// </summary>
public sealed class ModManifestTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AVersionOneModIsATierOneModForEachTarget()
    {
        var manifest = Load("""{ "id": "old", "formatVersion": 1 }""");

        Assert.Equal(ModManifest.DataTier, manifest.Tier);
        Assert.Equal(["hde-0.7.1", "vanilla-1.1", "openlancer"], manifest.Targets);
    }

    [Fact]
    public void AVersionTwoModGivesItsTierAndTargets()
    {
        var runtime = Load("""{ "id": "scripts", "formatVersion": 2, "tier": 2, "targets": [ "openlancer" ] }""");
        var data = Load("""{ "id": "hd", "formatVersion": 2, "tier": 1, "targets": [ "vanilla-1.1", "hde-0.7.1" ] }""");

        Assert.Equal(ModManifest.RuntimeTier, runtime.Tier);
        Assert.Equal(["openlancer"], runtime.Targets);
        Assert.Equal(ModManifest.DataTier, data.Tier);
        // The targets keep the order of the file.
        Assert.Equal(["vanilla-1.1", "hde-0.7.1"], data.Targets);
    }

    /// <summary>Without a tier, a mod is a data mod. Without targets, it is for each target of its tier.</summary>
    [Fact]
    public void AVersionTwoModWithoutTierOrTargetsGetsTheDefaults()
    {
        var plain = Load("""{ "id": "plain", "formatVersion": 2 }""");
        var runtime = Load("""{ "id": "scripts", "formatVersion": 2, "tier": 2 }""");

        Assert.Equal(ModManifest.DataTier, plain.Tier);
        Assert.Equal(ModTargets.All, plain.Targets);
        Assert.Equal(ModManifest.RuntimeTier, runtime.Tier);
        Assert.Equal(["openlancer"], runtime.Targets);
    }

    [Theory]
    [InlineData("""{ "id": "m", "formatVersion": 3 }""",
        "OpenLancer cannot read formatVersion 3. It reads formatVersion 1 and 2.")]
    [InlineData("""{ "id": "m" }""", "OpenLancer cannot read formatVersion 0. It reads formatVersion 1 and 2.")]
    [InlineData("""{ "id": "m", "formatVersion": 1, "tier": 1 }""",
        "tier and targets need formatVersion 2. Set formatVersion to 2.")]
    [InlineData("""{ "id": "m", "formatVersion": 1, "targets": [ "openlancer" ] }""",
        "tier and targets need formatVersion 2. Set formatVersion to 2.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "tier": 3 }""",
        "OpenLancer cannot read tier 3. Use tier 1 for a data mod, or tier 2 for a mod that only OpenLancer runs.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "tier": 0 }""",
        "OpenLancer cannot read tier 0. Use tier 1 for a data mod, or tier 2 for a mod that only OpenLancer runs.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "tier": "two" }""",
        "OpenLancer cannot read tier \"two\". Use tier 1 for a data mod, or tier 2 for a mod that only OpenLancer runs.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "tier": 1.5 }""",
        "OpenLancer cannot read tier 1.5. Use tier 1 for a data mod, or tier 2 for a mod that only OpenLancer runs.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "targets": [ "discovery" ] }""",
        "'discovery' is not a target. Use 'hde-0.7.1', 'vanilla-1.1' or 'openlancer'.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "targets": [ "OpenLancer" ] }""",
        "'OpenLancer' is not a target. Use 'hde-0.7.1', 'vanilla-1.1' or 'openlancer'.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "targets": [ "openlancer", "openlancer" ] }""",
        "targets names 'openlancer' two times. Remove one of them.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "targets": [] }""",
        "targets is empty. Name one target or more, or remove targets.")]
    [InlineData("""{ "id": "m", "formatVersion": 2, "tier": 2, "targets": [ "openlancer", "hde-0.7.1" ] }""",
        "A tier-2 mod runs only on OpenLancer. Remove the target 'hde-0.7.1', or use tier 1.")]
    public void AManifestThatOpenLancerCannotReadIsRefused(string json, string message)
    {
        var error = Assert.Throws<ModLoadException>(() => Load(json));

        Assert.Equal($"{Path.Combine(folder, ModManifest.FileName)}: {message}", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }

    private ModManifest Load(string json)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ModManifest.FileName), json);
        return ModManifest.Load(folder);
    }
}
