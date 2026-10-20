using System;
using OpenLancer.Editor;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The command line of the editor: a profile, and the scripts of the preview bench.</summary>
public sealed class EditorOptionsTests
{
    [Fact]
    public void AProfileAndBenchScriptsParse()
    {
        var options = EditorOptions.Parse(["retail.json", "--bench-cutscene", @"DATA\a.thn", @"DATA\b.thn"]);

        Assert.Equal("retail.json", options.Profile);
        Assert.Equal([@"DATA\a.thn", @"DATA\b.thn"], options.BenchCutscenes);
        Assert.True(options.Bench);
    }

    [Fact]
    public void NoArgumentsGiveANormalStart()
    {
        var options = EditorOptions.Parse([]);

        Assert.Null(options.Profile);
        Assert.Empty(options.BenchCutscenes);
        Assert.False(options.Bench);
    }

    /// <summary>A wrong command line of the bench throws, so that the script of the bench gets the message and the
    /// exit code.</summary>
    [Theory]
    [InlineData("--bench-cutscene")]
    [InlineData("retail.json", "--bench-cutscene", "--fast", @"DATA\a.thn")]
    [InlineData("a.json", "b.json", "--bench-cutscene", @"DATA\a.thn")]
    public void AWrongCommandLineOfTheBenchThrows(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => EditorOptions.Parse(args));
    }

    /// <summary>A wrong command line of a normal start opens the editor without a profile, and the Welcome screen shows
    /// the problem. The editor has no console window.</summary>
    [Theory]
    [InlineData("two profiles: a.json and b.json", "a.json", "b.json")]
    [InlineData("Unknown option --fast", "--fast")]
    [InlineData("Unknown option --fast", "a.json", "--fast")]
    public void AWrongCommandLineOfANormalStartGivesAProblem(string text, params string[] args)
    {
        var options = EditorOptions.Parse(args);

        Assert.Contains(text, options.Problem);
        Assert.EndsWith("The editor opened no profile.", options.Problem);
        Assert.Null(options.Profile);
        Assert.False(options.Bench);
    }

    /// <summary>The frame bench of the mission graph (section 18.4 of the M6a design) takes the install paths of mission
    /// scripts.</summary>
    [Fact]
    public void TheMissionBenchScriptsParse()
    {
        var options = EditorOptions.Parse(["retail.json", "--bench-mission", @"DATA\MISSIONS\M10\m10.ini"]);

        Assert.Equal([@"DATA\MISSIONS\M10\m10.ini"], options.BenchMissions);
        Assert.Empty(options.BenchCutscenes);
        Assert.True(options.Bench);
    }

    [Theory]
    [InlineData("--bench-mission")]
    [InlineData("a.json", "--bench-mission", @"DATA\a.ini", "--bench-cutscene", @"DATA\a.thn")]
    public void AWrongCommandLineOfTheMissionBenchThrows(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => EditorOptions.Parse(args));
    }

    /// <summary>The render bench of the agent server (section 13.4 of the MCP1 design) takes the nickname of one star
    /// system.</summary>
    [Fact]
    public void TheRenderBenchTakesOneSystem()
    {
        var options = EditorOptions.Parse(["retail.json", "--bench-agent-render", "Li01"]);

        Assert.Equal("retail.json", options.Profile);
        Assert.Equal("Li01", options.BenchAgentRender);
        Assert.Empty(options.BenchCutscenes);
        Assert.Empty(options.BenchMissions);
        Assert.True(options.Bench);
        Assert.Null(EditorOptions.Parse(["a.json"]).BenchAgentRender);
    }

    [Theory]
    [InlineData("a.json", "--bench-agent-render")]
    [InlineData("a.json", "--bench-agent-render", "Li01", "Li02")]
    [InlineData("a.json", "--bench-agent-render", "Li01", "--bench-cutscene", @"DATA.thn")]
    [InlineData("a.json", "--bench-mission", @"DATA.ini", "--bench-agent-render", "Li01")]
    public void AWrongCommandLineOfTheRenderBenchThrows(params string[] args)
    {
        var error = Assert.Throws<ArgumentException>(() => EditorOptions.Parse(args));

        Assert.Contains("--bench-agent-render", error.Message);
    }

    [Fact]
    public void AGoodCommandLineHasNoProblem()
    {
        Assert.Null(EditorOptions.Parse(["a.json"]).Problem);
        Assert.Null(EditorOptions.Parse(["a.json", "--bench-cutscene", @"DATA\a.thn"]).Problem);
    }
}
