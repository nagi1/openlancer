using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Validation;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>What the editor host reads from the editor window. The window gives it, and the tests give a
/// fake.</summary>
internal interface IAgentEditor
{
    /// <summary>The open profile, or null.</summary>
    EditorSession? Session { get; }

    /// <summary>The agent settings of the editor.</summary>
    AgentSettings Settings { get; }

    /// <summary>The messages of the Console.</summary>
    ConsoleLog Log { get; }

    /// <summary>What the editor shows now: the open system, mission and cutscene.</summary>
    AgentView View { get; }
}

/// <summary>
/// The editor host of the agent tools (section 5.4 of the MCP1 design): the session of the open profile, shared with
/// the user, the Console, the view, and the target checks in an oltool process. Each member runs on the UI thread,
/// in the session part of a call.
/// </summary>
internal sealed class EditorAgentHost(IAgentEditor editor) : AgentHost
{
    public override AgentHostKind Kind => AgentHostKind.Editor;

    public override EditSession? Session => editor.Session?.Edits;

    /// <summary>The reason of the session and the rule of the mods in the OpenLancer repo: the edits of such a mod
    /// wait for the allowance of the user (decision D738).</summary>
    public override string? NoEditReason => editor.Session?.NoEditReason ?? AgentTexts.Error("no_profile");

    public override AgentLimits Limits => editor.Settings.Limits;

    public override IReadOnlySet<ToolGroup> HiddenGroups => editor.Settings.HiddenToolGroups;

    public override AgentView? View => editor.View;

    public override string NameText(int ids) => editor.Session?.Name(ids) ?? "";

    public override string InfoText(int ids) => editor.Session?.InfoText(ids) ?? "";

    /// <summary>The entries of the Console, newest last. The base-game entries show only on request, as in the Console
    /// (decision D519). An engine line that waits for its origin does not show yet.</summary>
    public override IReadOnlyList<AgentConsoleEntry> Console(ConsoleFilter filter)
    {
        var lowest = filter.Severity switch
        {
            "error" => ConsoleSeverity.Error,
            "warning" => ConsoleSeverity.Warning,
            _ => ConsoleSeverity.Info,
        };
        var session = Session;
        return editor.Log.Entries
            .Where(e => e.Severity >= lowest && (filter.Since == null || e.Sequence > filter.Since))
            .Where(e => filter.Source == null || e.Source.Equals(filter.Source, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Origin.Kind != OriginKind.Pending && (filter.BaseGame || !e.Origin.IsBaseGame))
            .TakeLast(filter.Limit)
            .Select(e => new AgentConsoleEntry(e.Sequence, e.Time.ToUniversalTime(), e.Severity.ToString().ToLowerInvariant(),
                e.Source, e.Message, e.Target is { } target && session != null ? ItemRefs.Canonical(session, target) : null,
                e.Origin.Kind))
            .ToList();
    }

    /// <summary>
    /// The target check in an oltool process, as the tail of the call: the check merges all mods of the profile on the
    /// files of the game, which takes seconds, and the engine log of a data load is process-wide (see
    /// <see cref="Editing.ToolProcess"/>). The game folder is the linked game of the target.
    /// </summary>
    public override ToolStep CheckTarget(string target)
    {
        var session = Session!;
        var arguments = new List<string> { "validate", session.Profile.FilePath, "--target", target, "--json" };
        if (session.TargetGame?.Invoke(target) is { } game)
        {
            arguments.Add("--game");
            arguments.Add(game.Root);
        }
        return ToolStep.Then(token => RunCheck(ProgramPaths.Oltool, arguments, token));
    }

    /// <summary>Runs <c>oltool validate --target</c> and gives its problems. A cancel of the call stops the
    /// process.</summary>
    internal static async Task<ToolResult> RunCheck(string tool, IReadOnlyList<string> arguments, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{tool} did not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancel);
            var errors = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            if (process.ExitCode == 2)
            {
                return ToolResult.Failure(new EditError(EditErrorKind.Refused, "TargetCheckFailed",
                    AgentTexts.Error("check_target_failed", ("reason", (await errors.ConfigureAwait(false)).Trim()))));
            }
            var problems = ValidationJson.Read(await output.ConfigureAwait(false));
            return ToolResult.Of(CheckQueries.Of("target", problems.Select(p => Problem.From(p, null)).ToList(),
                AgentTexts.Error("check_target_note")));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
