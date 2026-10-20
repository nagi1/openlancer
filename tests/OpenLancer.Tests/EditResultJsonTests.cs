using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The JSON form of the result and the errors of the command layer (section 4.0.6 of the M6b
/// design).</summary>
public sealed class EditResultJsonTests
{
    [Fact]
    public void TheResultUsesSnakeCaseAndTheRevisionIsText()
    {
        var result = new EditResult(EditStatus.NoChange)
        {
            Label = "Add a delay",
            UndoLabel = "Agent: Add a delay",
            Revision = 142,
            Versions = new System.Collections.Generic.Dictionary<string, long> { [@"DATA\MISSIONS\OL_TEST\ol_test.ini"] = 8 },
            Deletes = [new ResultDelete("obj:ol_station_1", DeleteReason.Cascade, "parent = ol_base")],
            NewProblems = [new Problem(DiagnosticSeverity.Error, "A message.") { Rule = "M05", Origin = OriginKind.OpenLancer }],
            NeedsConfirmation = true,
        };

        var json = JsonNode.Parse(JsonSerializer.Serialize(result, EditJson.Options))!.AsObject();

        Assert.Equal("no_change", (string?)json["status"]);
        Assert.Equal("Agent: Add a delay", (string?)json["undo_label"]);
        Assert.Equal("142", (string?)json["rev"]);
        Assert.Equal(8, (long?)json["versions"]![@"DATA\MISSIONS\OL_TEST\ol_test.ini"]);
        Assert.Equal("cascade", (string?)json["deletes"]![0]!["why"]);
        Assert.Equal("error", (string?)json["new_problems"]![0]!["severity"]);
        Assert.Equal("openlancer", (string?)json["new_problems"]![0]!["origin"]);
        Assert.True((bool?)json["needs_confirmation"]);
        Assert.False(json.ContainsKey("select"));
        Assert.Equal(result.Revision, JsonSerializer.Deserialize<EditResult>(json.ToJsonString(), EditJson.Options)!.Revision);
    }

    [Fact]
    public void ARefusedPlanGivesItsRefusalsAsErrors()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var loadout = new OpenLancer.Data.Editing.SectionRef(ListWorkspace.ShipLoadoutsPath,
            new OpenLancer.Data.Editing.SectionIdentity("Loadout", "lo_station"));

        var plan = ScenePlans.Delete(session, [loadout]);
        var result = plan.Report(session);

        Assert.Null(plan.Command);
        Assert.Equal(EditStatus.Refused, result.Status);
        Assert.Empty(result.Refusals);
        var error = Assert.Single(result.Errors);
        Assert.Equal(EditErrorKind.Refused, error.Kind);
        Assert.Equal(EditPlan.PlanRefused, error.Detail);
        Assert.Equal(plan.Refusals[0].Message, error.Message);
        Assert.Equal($@"sec:{ListWorkspace.ShipLoadoutsPath}#Loadout/lo_station", error.Target);
    }

    [Fact]
    public void AProblemThatWaitsForTheBaseGameHasTheOriginUnknown()
    {
        var pending = Diagnostic.Error("x.ini", "A message.") with { Origin = MessageOrigin.Pending };
        var fromMod = Diagnostic.Error("x.ini", "A message.") with { Origin = MessageOrigin.Mod("ol_mod", ModEvidence.Section) };

        Assert.Equal(OriginKind.Unknown, Problem.From(pending, null).Origin);
        Assert.Equal((OriginKind.Mod, "ol_mod"), (Problem.From(fromMod, null).Origin, Problem.From(fromMod, null).ModId));
        Assert.Equal(OriginKind.Unknown, Problem.From(Diagnostic.Error("x.ini", "A message."), null).Origin);
    }

    [Fact]
    public void AnErrorWritesItsKindAsCode()
    {
        var error = new EditError(EditErrorKind.StaleRevision, "RowChanged", "Read the trigger again.") { Index = 2 };

        var json = JsonNode.Parse(JsonSerializer.Serialize(error, EditJson.Options))!.AsObject();

        Assert.Equal("stale_revision", (string?)json["code"]);
        Assert.Equal("RowChanged", (string?)json["detail"]);
        Assert.Equal(2, (int?)json["index"]);
        Assert.Equal(error, JsonSerializer.Deserialize<EditError>(json.ToJsonString(), EditJson.Options));
    }
}
