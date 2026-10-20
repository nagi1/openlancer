using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The JSON forms of the dry run and the dry run query of the shared command layer (sections 7.2 to 7.4 of the
/// M6c design).</summary>
public sealed class RunJsonTests
{
    private static readonly string ScriptRefText = new ScriptRef(Script).ToString();

    private static string Trg(string name) => new TriggerRef(Script, name).ToString();

    private static JsonObject Request(params JsonObject[] inputs) => new()
    {
        ["script"] = ScriptRefText,
        ["inputs"] = new JsonArray(inputs.Select(i => (JsonNode)i).ToArray()),
    };

    private static EditError Error(System.Action action) => Assert.Throws<EditException>(action).Error;

    [Fact]
    public void EachInputKindRoundTripsInSnakeCaseWithTheReferenceStrings()
    {
        DryRunInput[] inputs =
        [
            DryRunInput.StartMission(),
            DryRunInput.LoadCheckpoint("ol_end"),
            DryRunInput.StartAt("ol_wave"),
            DryRunInput.RunTrigger("ol_wave"),
            DryRunInput.Event(LineSpec.Of("Cnd_Destroyed", ("target", "ol_ship_1"), ("count", -1))),
            DryRunInput.Fact(LineSpec.Of("Cnd_InSpace", ("inSpace", true)), true),
            DryRunInput.Heartbeats(90),
            DryRunInput.NextTimer(),
            DryRunInput.PlayerSystem("ol_system"),
            DryRunInput.PlayerSystem(null),
            DryRunInput.PlayerInSpace(false),
        ];

        var json = new JsonArray(inputs.Select(i => (JsonNode)RunJson.Write(i, Script)).ToArray());
        var back = RunJson.ReadInputs(JsonNode.Parse(json.ToJsonString())!.AsArray(), Script);

        Assert.Equal(inputs, back);
        Assert.Equal(
            """{"kind":"load_checkpoint","trigger":"trg:DATA\\MISSIONS\\OL_TEST\\ol_test.ini#ol_end"}""",
            json[1]!.ToJsonString());
        Assert.Equal("""{"kind":"event","line":{"key":"Cnd_Destroyed","values":{"target":"ol_ship_1","count":-1}}}""",
            json[4]!.ToJsonString());
        Assert.Equal("""{"kind":"heartbeats","until_timer":true}""", json[7]!.ToJsonString());
        Assert.Equal("""{"kind":"player_system","system":"system:ol_system"}""", json[8]!.ToJsonString());
        Assert.Equal(RunJson.KindNames.Count, inputs.Select(i => i.Kind).Distinct().Count());
    }

    [Theory]
    [InlineData("""{"kind":"jump"}""", "kind")]
    [InlineData("""{"kind":"start_mission","trigger":"x"}""", "inputs[0].trigger")]
    [InlineData("""{"kind":"run_trigger"}""", "inputs[0].trigger")]
    [InlineData("""{"kind":"run_trigger","trigger":"trg:DATA\\MISSIONS\\OTHER\\other.ini#ol_wave"}""", "inputs[0].trigger")]
    [InlineData("""{"kind":"heartbeats","count":0}""", "inputs[0].count")]
    [InlineData("""{"kind":"heartbeats","count":18001}""", "inputs[0].count")]
    [InlineData("""{"kind":"event","line":{"key":"Cnd_Timer","values":{"seconds":3}}}""", "inputs[0].line")]
    [InlineData("""{"kind":"fact","line":{"key":"Cnd_Destroyed"},"on":true}""", "inputs[0].line")]
    [InlineData("""{"kind":"event","line":{"key":"Cnd_Destroyed","values":{"victim":"x"}}}""", "inputs[0].line")]
    [InlineData("""{"kind":"player_system","system":"base:ol_base"}""", "inputs[0].system")]
    public void AnInputThatDoesNotFitTheSchemaGivesInvalidArgument(string input, string field)
    {
        var error = Error(() => RunJson.ReadInput(JsonNode.Parse(input), Script));

        Assert.Equal(EditErrorKind.InvalidArgument, error.Kind);
        Assert.EndsWith(field, error.Field);
    }

    [Theory]
    [InlineData("timer_bound", "5")]
    [InlineData("timer_bound", "\"long\"")]
    [InlineData("since_step", "\"x\"")]
    [InlineData("since_step", "-1")]
    [InlineData("inputs", "{}")]
    public void ARequestFieldOfAnotherTypeGivesInvalidArgument(string field, string value)
    {
        var request = Request();
        request[field] = JsonNode.Parse(value);

        var error = Error(() => RunJson.ReadRequest(request));

        Assert.Equal((EditErrorKind.InvalidArgument, field), (error.Kind, error.Field));
    }

    [Fact]
    public void TheQueryFoldsTheInputsAndGivesTheStepsAndTheState()
    {
        using var m = Create();
        var request = RunJson.ReadRequest(Request(
            new JsonObject { ["kind"] = "start_mission" },
            new JsonObject { ["kind"] = "heartbeats", ["count"] = 1 },
            new JsonObject { ["kind"] = "heartbeats", ["until_timer"] = true },
            new JsonObject
            {
                ["kind"] = "event",
                ["line"] = new JsonObject { ["key"] = "Cnd_Destroyed", ["values"] = new JsonObject { ["target"] = "ol_ship_1" } },
            }));

        var view = m.Missions.Queries.DryRun(request);
        var json = JsonSerializer.Serialize(view, EditJson.Options);

        Assert.Equal(4, view.TotalSteps);
        Assert.Equal(["Start mission", "Heartbeat", "Next timer", "The last ship of ol_ship_1 is destroyed"],
            view.Steps.Select(s => s.Cause));
        Assert.Equal(91, view.State.Heartbeats);
        Assert.NotNull(view.State.End);
        Assert.Equal(MissionEndKind.Success, view.State.End!.Kind);
        Assert.Equal(Trg("ol_end"), view.State.End.Trigger);
        Assert.Contains("\"kind\":\"ran\"", json);
        Assert.Contains("\"waiting_events\":[]", json);
        Assert.Contains(view.Steps[2].Changes, c => c.Kind == TraceChangeKind.Effect &&
                                                    c.Line == new RowRef(T("ol_wave").Item, 3).Reference);
    }

    [Fact]
    public void TheQueryGivesTheStepsSinceAStepAndTheWaitingEvents()
    {
        using var m = Create();
        var request = RunJson.ReadRequest(Request(
            new JsonObject { ["kind"] = "start_mission" },
            new JsonObject { ["kind"] = "heartbeats", ["count"] = 100 })) with { SinceStep = 1 };

        var view = m.Missions.Queries.DryRun(request);

        Assert.Equal(2, view.TotalSteps);
        Assert.Equal(2, Assert.Single(view.Steps).Step);
        var waiting = Assert.Single(view.State.WaitingEvents);
        Assert.Equal("Cnd_Destroyed", waiting.Line.Key);
        Assert.Equal([Trg("ol_end")], waiting.Triggers);
        Assert.Equal("Starts when the last ship of ol_ship_1 is destroyed.", waiting.Sentence);
        Assert.Equal("Spawned at step 2", waiting.Note);
    }

    [Fact]
    public void AnEventWithAPartOfTheValuesMatchesTheLineWithAllValues()
    {
        using var m = Create(Text.Replace("Cnd_Destroyed = ol_ship_1, -1", "Cnd_Destroyed = ol_ship_1, -1, EXPLODE"));
        var request = RunJson.ReadRequest(Request(
            new JsonObject { ["kind"] = "start_mission" },
            new JsonObject { ["kind"] = "heartbeats", ["count"] = 1 },
            new JsonObject
            {
                ["kind"] = "event",
                ["line"] = new JsonObject { ["key"] = "cnd_destroyed", ["values"] = new JsonObject { ["target"] = "OL_SHIP_1" } },
            }));

        var view = m.Missions.Queries.DryRun(request);

        Assert.NotNull(view.State.End);
    }

    [Fact]
    public void AnEventThatMatchesTwoLinesGivesInvalidArgument()
    {
        var text = Text + "\n\n[Trigger]\nnickname = ol_other\nInitState = ACTIVE\nCnd_Destroyed = ol_ship_2, -1\n";
        using var m = Create(text);
        var request = RunJson.ReadRequest(Request(
            new JsonObject { ["kind"] = "start_mission" },
            new JsonObject { ["kind"] = "heartbeats", ["count"] = 1 },
            new JsonObject
            {
                ["kind"] = "event",
                ["line"] = new JsonObject { ["key"] = "Cnd_Destroyed", ["values"] = new JsonObject { ["count"] = -1 } },
            }));

        var error = Error(() => m.Missions.Queries.DryRun(request));

        Assert.Equal(EditErrorKind.InvalidArgument, error.Kind);
        Assert.Contains("ol_ship_1", error.Message);
        Assert.Contains("ol_ship_2", error.Message);
    }

    [Fact]
    public void TooManyInputsOrHeartbeatsGiveLimit()
    {
        using var m = Create();
        var inputs = Enumerable.Repeat(DryRunInput.Heartbeats(1), 1001).ToList();
        var heartbeats = Enumerable.Repeat(DryRunInput.Heartbeats(18_000), 6).Append(DryRunInput.Heartbeats(1)).ToList();

        var many = Error(() => m.Missions.Queries.DryRun(new DryRunRequest(Script, inputs)));
        var long_ = Error(() => m.Missions.Queries.DryRun(new DryRunRequest(Script, heartbeats)));

        Assert.Equal((EditErrorKind.Limit, "TooManyInputs"), (many.Kind, many.Detail));
        Assert.Equal((EditErrorKind.Limit, "TooManyHeartbeats"), (long_.Kind, long_.Detail));
        m.Missions.Queries.DryRun(new DryRunRequest(Script, inputs.Take(1000).ToList()));
    }

    [Fact]
    public void AReferenceThatDoesNotResolveGivesNotFound()
    {
        using var m = Create();

        var trigger = Error(() => m.Missions.Queries.DryRun(new DryRunRequest(Script, [DryRunInput.RunTrigger("ol_none")])));
        var script = Error(() => m.Missions.Queries.DryRun(new DryRunRequest(@"DATA\MISSIONS\NONE\none.ini", [])));
        var system = Error(() => m.Missions.Queries.DryRun(new DryRunRequest(Script, [DryRunInput.PlayerSystem("ol_none")])));
        var later = Error(() => m.Missions.Queries.DryRun(new DryRunRequest(Script, [DryRunInput.StartAt("ol_wave")])));

        Assert.Equal(EditErrorKind.NotFound, trigger.Kind);
        Assert.Equal(EditErrorKind.NotFound, script.Kind);
        Assert.Equal(EditErrorKind.NotFound, system.Kind);
        Assert.Equal(EditErrorKind.NotAvailable, later.Kind);
    }

    [Fact]
    public void ALoadOfATriggerThatIsNotACheckpointGivesInvalidArgument()
    {
        using var m = Create();

        var error = Error(() => m.Missions.Queries.DryRun(new DryRunRequest(Script, [DryRunInput.LoadCheckpoint("ol_wave")])));

        Assert.Equal((EditErrorKind.InvalidArgument, "NotACheckpoint"), (error.Kind, error.Detail));
    }

    [Fact]
    public void TheJsonHostRunsTheQuery()
    {
        using var m = Create();
        var host = new MissionJsonHost(m.Session);

        var reply = host.DryRun(Request(new JsonObject { ["kind"] = "start_mission" }).ToJsonString());
        var bad = host.DryRun("[1]");
        var missing = host.DryRun(Request(new JsonObject { ["kind"] = "run_trigger", ["trigger"] = Trg("ol_none") }).ToJsonString());

        Assert.False(reply.IsError);
        Assert.Equal(1, (int)JsonNode.Parse(reply.Json)!["total_steps"]!);
        Assert.True(bad.IsError);
        Assert.True(missing.IsError);
        Assert.Contains("\"code\":\"not_found\"", missing.Json);
    }

    [Fact]
    public void TheQueryChangesNothing()
    {
        using var m = Create();
        var revision = m.Session.Revision;
        var version = m.Missions.Version(Script);

        m.Missions.Queries.DryRun(new DryRunRequest(Script, [DryRunInput.StartMission(), DryRunInput.Heartbeats(100)]));

        Assert.Equal(revision, m.Session.Revision);
        Assert.Equal(version, m.Missions.Version(Script));
        Assert.False(m.Session.CanUndo);
    }
}
