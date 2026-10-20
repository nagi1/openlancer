using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Run;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The reply of a call of <see cref="MissionJsonHost"/>.</summary>
/// <param name="Json">The result as JSON in snake case.</param>
/// <param name="IsError">True for a refusal or a reference that does not resolve. An MCP tool gives it as
/// <c>isError</c>.</param>
public sealed record MissionHostReply(string Json, bool IsError);

/// <summary>
/// A host of the mission layer without UI (sections 4.13 and 11.3 of the M6b design). It reads the JSON requests,
/// runs them on the session with the edit origin Agent and in strict mode, and writes the JSON replies. The query tools
/// and the edit tool of MCP1 can use the same calls. The headless test runs the layer through it.
/// </summary>
/// <remarks>
/// <para>Each edit is one batch and one entry of the history (section 4.0.4). The host runs on the thread of the
/// session (section 4.0.3). Each edit runs in strict mode: a batch that adds an error refuses (section 4.0.5, decision
/// D681). A request can also ask to refuse new warnings, but it cannot turn strict mode off.</para>
/// <para>A refusal gives a reply with <see cref="MissionHostReply.IsError"/>. The JSON then has the status
/// <c>refused</c> and the errors with their <c>code</c>, <c>detail</c> and message. A problem is data in a result, not an
/// error.</para>
/// </remarks>
public sealed class MissionJsonHost(EditSession session)
{
    /// <summary>Runs a JSON batch (the tool <c>mission_edit</c>).</summary>
    public MissionHostReply Edit(string json) => Run(json, preview: false);

    /// <summary>Runs a JSON batch, reports the result, and puts back the state before. No history entry.</summary>
    public MissionHostReply Preview(string json) => Run(json, preview: true);

    /// <summary>The rows of the Missions browser (the tool <c>list_missions</c>).</summary>
    public MissionHostReply ListScripts() => Read(() => Queries.ListScripts());

    /// <summary>
    /// The view of an item (the tools <c>get_mission</c>, <c>get_trigger</c> and <c>get_item</c>): a script for
    /// <c>msn:</c>, a trigger for <c>trg:</c>, an item for <c>sec:</c> or <c>label:</c>, and a prefab instance for
    /// <c>inst:</c>.
    /// </summary>
    public MissionHostReply Get(string reference) => Read<object>(() => ItemRefs.Parse(reference).Kind switch
    {
        ItemRefKind.Script => Queries.GetMission(ScriptRef.Parse(reference)),
        ItemRefKind.Trigger => Queries.GetTrigger(TriggerRef.Parse(reference)),
        ItemRefKind.Section => Queries.GetItem(ItemRef.Parse(reference)),
        ItemRefKind.Label => Queries.GetItem(LabelRef.Parse(reference)),
        ItemRefKind.Instance => Queries.GetPrefabInstance(reference),
        _ => throw new EditException(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference,
            $"{reference} is not a mission item. Use a msn:, trg:, sec:, label: or inst: reference.")),
    });

    /// <summary>Each line that names an item (the tool <c>find_references</c>).</summary>
    public MissionHostReply FindReferences(string reference) => Read(() => Queries.FindReferences(reference));

    /// <summary>The conditions, actions and orders (the tool <c>describe_mission_name</c>).</summary>
    public MissionHostReply Palette() => Read(() => Queries.ListPalette());

    /// <summary>The built-in prefabs (the resource <c>openlancer://catalog/prefabs</c>).</summary>
    public MissionHostReply Prefabs() => Read(() => Queries.ListPrefabs());

    /// <summary>The problems of a script that show (the tool <c>check</c> with the scope <c>mission</c>).</summary>
    /// <param name="baseGame">True to give the problems of the base game too.</param>
    public MissionHostReply Problems(string script, bool baseGame = false) =>
        Read(() => Queries.GetProblems(ScriptRef.Parse(script), baseGame));

    /// <summary>
    /// The dry run query (the tool <c>mission_dry_run</c>, section 7.3 of the M6c design): a request with
    /// <c>script</c>, <c>inputs</c>, <c>timer_bound</c> and <c>since_step</c>. It changes nothing.
    /// </summary>
    public MissionHostReply DryRun(string json) => Read(() =>
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            node = null;
        }
        return Queries.DryRun(RunJson.ReadRequest(node as JsonObject ?? throw new EditException(
            MissionErrors.BadJsonError("The request is a JSON object with script and inputs."))));
    });

    /// <summary>
    /// The start plan query (the tool <c>mission_start_plan</c>, section 7.3 of the M6c design): a request with
    /// <c>script</c>, <c>start</c>, <c>target</c> and <c>start_place</c>. It changes nothing.
    /// </summary>
    public MissionHostReply StartPlan(string json) => Read(() =>
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            node = null;
        }
        return Queries.StartPlan(RunJson.ReadStartRequest(node as JsonObject ?? throw new EditException(
            MissionErrors.BadJsonError("The request is a JSON object with script and start."))));
    });

    /// <summary>The JSON Schema of a batch, the input schema of the tool <c>mission_edit</c>.</summary>
    public static string Schema() => MissionJson.Schema().ToJsonString();

    private MissionQueries Queries => session.Missions.Queries;

    private MissionHostReply Run(string json, bool preview)
    {
        try
        {
            // The host sets the origin and strict mode. A request cannot change them (section 4.13 of the M6b design).
            var read = MissionJson.ReadBatch(json);
            var batch = read with
            {
                Origin = EditOrigin.Agent,
                Options = read.Options with { RejectNewErrors = true },
            };
            var result = preview ? session.Missions.Preview(batch) : session.Missions.Apply(batch);
            return new MissionHostReply(MissionJson.Write(result), result.Status == EditStatus.Refused);
        }
        catch (EditException e)
        {
            return Error(e.Error);
        }
    }

    private MissionHostReply Read<T>(Func<T> query)
    {
        try
        {
            return new MissionHostReply(MissionJson.Write(query()), false);
        }
        catch (EditException e)
        {
            return Error(e.Error);
        }
    }

    /// <summary>The reply of a refusal, with the revision of the session (section 4.0.6 of the M6b design).</summary>
    private MissionHostReply Error(EditError error) =>
        new(MissionJson.Write(new EditResult(EditStatus.Refused)
        {
            Errors = [error],
            Revision = session.Revision,
            Summary = error.Message,
        }), true);
}
