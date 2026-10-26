using System;
using System.Numerics;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The arguments of the play process (section 6.3 of the M4 design).</summary>
public sealed class PlayArgsTests
{
    private const string Profile = @"D:\Game Data\my profiles\my.sample v2.json";

    /// <summary>A heading of yaw 90 degrees and pitch -30 degrees.</summary>
    private static readonly Quaternion Heading = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, -MathF.PI / 6, 0);

    [Fact]
    public void AWriteAndAReadGiveTheSameRecord()
    {
        PlayArgs[] samples =
        [
            new(Profile, "m4proof", 3, new PlayStart.InSpace("li01", new Vector3(-33270.5f, 0.1f, -25039.003f), Heading), null),
            new(Profile, null, 0, new PlayStart.AtBase("Li01_01_Base"), "ol_starrunner_package"),
            new(Profile, "m4proof", 12, new PlayStart.InSpace("Li01", new Vector3(1e-7f, -0f, 3.4e38f), Quaternion.Identity),
                "le_package"),
        ];

        foreach (var sample in samples)
            Assert.Equal(sample, PlayArgs.Parse(sample.ToArguments()));
    }

    [Fact]
    public void TheListHasTheOptionsOfTheDesign()
    {
        var args = new PlayArgs(Profile, "m4proof", 3,
            new PlayStart.InSpace("li01", new Vector3(-33270.5f, 0, 0.1f), new Quaternion(0, 0.70710677f, 0, 0.70710677f)),
            "le_package");

        Assert.Equal(
        [
            "--profile", Profile, "--mod", "m4proof", "--save", "3",
            "--space", "li01", "-33270.5", "0", "0.1", "0", "0.70710677", "0", "0.70710677",
            "--package", "le_package",
        ], args.ToArguments());
        Assert.Equal(["--profile", Profile, "--save", "0", "--base", "Li01_01_Base"],
            (args with { ActiveMod = null, Save = 0, Start = new PlayStart.AtBase("Li01_01_Base"), Package = null })
            .ToArguments());
    }

    /// <summary>A mission play gives its start save and its test layer (section 5.6 of the M6c design).</summary>
    [Fact]
    public void AStartSaveAndATestLayerRoundTrip()
    {
        const string save = @"D:\Game Data\my profiles\my.sample v2.play\start\start.fl";
        const string layer = @"D:\Game Data\my profiles\my.sample v2.play\start\layer";
        var args = new PlayArgs(Profile, "m6c", 2, new PlayStart.FromSave(save, layer), null);
        var plain = args with { Start = new PlayStart.FromSave(save, null) };

        Assert.Equal(["--profile", Profile, "--mod", "m6c", "--save", "2", "--start-save", save, "--test-layer", layer],
            args.ToArguments());
        Assert.Equal(args, PlayArgs.Parse(args.ToArguments()));
        Assert.Equal(plain, PlayArgs.Parse(plain.ToArguments()));
        Assert.Equal(args, PlayArgs.Parse(["--test-layer", layer, "--save", "2", "--profile", Profile, "--start-save", save,
            "--mod", "m6c"]));
        // The start, each reload and each restart of the play load the test layer (review fix of M6c-2).
        Assert.Equal(layer, args.TestLayer);
        Assert.Null(plain.TestLayer);
        Assert.Null((args with { Start = new PlayStart.AtBase("Li01_01_Base") }).TestLayer);
    }

    [Fact]
    public void TheOptionsCanComeInAnyOrderAndTheSaveIsOptional()
    {
        var args = PlayArgs.Parse(["--base", "Li01_01_Base", "--mod", "m4proof", "--profile", Profile]);

        Assert.Equal(new PlayArgs(Profile, "m4proof", 0, new PlayStart.AtBase("Li01_01_Base"), null), args);
    }

    [Theory]
    [InlineData("--profile p.json", "no start")]
    [InlineData("--profile p.json --mod m", "no start")]
    [InlineData("--base b", "no --profile")]
    [InlineData("--profile p.json --base a --base b", "two starts")]
    [InlineData("--profile p.json --base a --space li01 0 0 0 0 0 0 1", "two starts")]
    [InlineData("--profile p.json --profile q.json --base b", "--profile two times")]
    [InlineData("--profile p.json --space li01 0 x 0 0 0 0 1", "'x' of --space is not a finite number")]
    [InlineData("--profile p.json --space li01 0 NaN 0 0 0 0 1", "'NaN' of --space is not a finite number")]
    [InlineData("--profile p.json --space li01 0 1e39 0 0 0 0 1", "'1e39' of --space is not a finite number")]
    [InlineData("--profile p.json --space li01 0 0 0 0 0 0 2", "not a unit quaternion")]
    [InlineData("--profile p.json --save -1 --base b", "'-1' of --save is not a save number")]
    [InlineData("--profile p.json --save 1.5 --base b", "'1.5' of --save is not a save number")]
    [InlineData("--profile p.json --space li01 1 2 3", "--space needs 8 values")]
    [InlineData("--profile p.json --space li01 1 2 3 0 0 0 --base b", "--space needs 8 values")]
    [InlineData("--profile p.json --base", "--base needs a value")]
    [InlineData("--profile --base b", "--profile needs a value")]
    [InlineData("--profile p.json --base b --package", "--package needs a value")]
    [InlineData("--profile p.json --base b extra", "'extra' is not known")]
    [InlineData("--profile p.json --start-save s.fl --space li01 0 0 0 0 0 0 1", "two starts")]
    [InlineData("--profile p.json --base b --start-save s.fl", "two starts")]
    [InlineData("--profile p.json --base b --test-layer l", "--test-layer needs --start-save")]
    [InlineData("--profile p.json --test-layer l", "--test-layer needs --start-save")]
    [InlineData("--profile p.json --start-save s.fl --test-layer a --test-layer b", "--test-layer two times")]
    [InlineData("--profile p.json --start-save", "--start-save needs a value")]
    [InlineData("--profile p.json --base b --fullscreen", "'--fullscreen' is not known")]
    public void AWrongListGivesTheReason(string line, string reason)
    {
        var error = Assert.Throws<FormatException>(() => PlayArgs.Parse(line.Split(' ')));

        Assert.Contains(reason, error.Message);
    }
}
