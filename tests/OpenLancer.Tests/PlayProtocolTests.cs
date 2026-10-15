using System;
using System.Numerics;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The commands and events between the editor and the play process (decision D42).</summary>
public sealed class PlayProtocolTests
{
    [Theory]
    [InlineData(PlayCommandKind.Start, 0, "start")]
    [InlineData(PlayCommandKind.Stop, 0, "stop")]
    [InlineData(PlayCommandKind.Reload, 7, "reload 7")]
    [InlineData(PlayCommandKind.Reload, 0, "reload 0")]
    public void EachCommandIsWrittenAndRead(PlayCommandKind kind, int save, string line)
    {
        var command = new PlayCommand(kind, save);

        Assert.Equal(line, PlayProtocol.Write(command));
        Assert.Equal(command, PlayProtocol.ReadCommand(line));
        Assert.Equal(command, PlayProtocol.ReadCommand(line + "\r"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("go")]
    [InlineData("Start")]
    [InlineData("start now")]
    [InlineData("reload")]
    [InlineData("reload x")]
    [InlineData("reload -1")]
    [InlineData("reload 1 2")]
    public void AnUnknownLineIsNoCommand(string line)
    {
        Assert.Null(PlayProtocol.ReadCommand(line));
    }

    public static TheoryData<string, string> EventLines => new()
    {
        { "waiting", """@ol {"event":"waiting","loadMs":2345}""" },
        { "spawned in space", """@ol {"event":"spawned","system":"li01","base":null,"pos":[-33270.5,0,-25039.25],"ship":"ge_fighter","shipName":"Starflier","startMs":812}""" },
        { "spawned at a base", """@ol {"event":"spawned","system":"li01","base":"li01_01_base","pos":null,"ship":"ge_fighter","shipName":"Starflier","startMs":640}""" },
        { "reloaded", """@ol {"event":"reloaded","save":3,"ms":2100,"system":"li01","base":null}""" },
        { "reload-failed", """@ol {"event":"reload-failed","save":4,"message":"Caf\u00E9 is not complete.\nOpen the profile.\u0022"}""" },
        { "error with crash", """@ol {"event":"error","message":"No hull \u0027no_such_hull\u0027.","crash":true}""" },
        { "error", """@ol {"event":"error","message":"No package \u0027x\u0027.","crash":false}""" },
    };

    private static PlayEvent Event(string name) => name switch
    {
        "waiting" => new PlayEvent.Waiting(2345),
        "spawned in space" => new PlayEvent.Spawned("li01", null, new Vector3(-33270.5f, 0, -25039.25f), "ge_fighter",
            "Starflier", 812),
        "spawned at a base" => new PlayEvent.Spawned("li01", "li01_01_base", null, "ge_fighter", "Starflier", 640),
        "reloaded" => new PlayEvent.Reloaded(3, 2100, "li01", null),
        "reload-failed" => new PlayEvent.ReloadFailed(4, "Caf\u00e9 is not complete.\nOpen the profile.\""),
        "error with crash" => new PlayEvent.Error("No hull 'no_such_hull'.", true),
        "error" => new PlayEvent.Error("No package 'x'.", false),
        _ => throw new System.ArgumentException(name),
    };

    /// <summary>Each event has the fields of the design table, on one line of ASCII characters.</summary>
    [Theory]
    [MemberData(nameof(EventLines))]
    public void EachEventIsWrittenAndRead(string name, string line)
    {
        var e = Event(name);

        Assert.Equal(line, PlayProtocol.Write(e));
        Assert.Equal(e, PlayProtocol.ReadEvent(line));
    }

    [Fact]
    public void TheEventFieldCanComeLast()
    {
        Assert.Equal(new PlayEvent.Waiting(5), PlayProtocol.ReadEvent("""@ol {"loadMs":5,"event":"waiting"}"""));
    }

    [Theory]
    [InlineData("[Warning: 10:00:00] GameItemDb: Unknown entry hit_ptz")]
    [InlineData("[Info: 10:00:00] Server: started")]
    [InlineData("")]
    [InlineData("@ol")]
    [InlineData("@ol ")]
    [InlineData("@ol {\"event\":\"waiting\",\"loadMs\":")]
    [InlineData("@ol null")]
    [InlineData("@ol []")]
    [InlineData("@ol {}")]
    [InlineData("@ol {\"event\":\"unknown\"}")]
    [InlineData("@ol {\"event\":\"waiting\"}")]
    [InlineData("@ol {\"event\":\"waiting\",\"loadMs\":\"5\"}")]
    [InlineData("@ol {\"event\":\"error\",\"message\":null,\"crash\":true}")]
    [InlineData("@ol {\"event\":\"spawned\",\"system\":\"li01\",\"base\":null,\"pos\":[1,2],\"ship\":\"s\",\"shipName\":\"S\",\"startMs\":1}")]
    [InlineData("@ol {\"event\":\"spawned\",\"system\":\"li01\",\"base\":null,\"pos\":[1e39,0,0],\"ship\":\"s\",\"shipName\":\"S\",\"startMs\":1}")]
    [InlineData("@ol {\"event\":\"error\",\"message\":\"\\uD800\",\"crash\":false}")]
    [InlineData("ol {\"event\":\"waiting\",\"loadMs\":5}")]
    [InlineData(" @ol {\"event\":\"waiting\",\"loadMs\":5}")]
    public void ALineThatIsNoEventIsALogLine(string line)
    {
        Assert.Null(PlayProtocol.ReadEvent(line));
    }

    /// <summary>A line with a lone surrogate, which UTF-8 cannot encode, is a log line.</summary>
    [Fact]
    public void ALineWithALoneSurrogateIsALogLine()
    {
        var line = PlayProtocol.EventPrefix + "{\"event\":\"error\",\"message\":\"" + (char)0xD800 +
                   "\",\"crash\":false}";

        Assert.Null(PlayProtocol.ReadEvent(line));
    }

    /// <summary>JSON has no number for NaN or infinity, so an event with such a number has no line.</summary>
    [Fact]
    public void AnEventWithANumberThatIsNotFiniteHasNoLine()
    {
        var e = new PlayEvent.Spawned("li01", null, new Vector3(float.NaN, 0, 0), "s", "S", 1);

        Assert.Throws<ArgumentException>(() => PlayProtocol.Write(e));
    }
}
