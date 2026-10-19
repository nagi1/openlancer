using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LibreLancer;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Validation;

namespace OpenLancer.Editor.Logs;

/// <summary>The result of a base run: the count of each engine line key, or why the run failed.</summary>
internal sealed record BaseRunResult(IReadOnlyDictionary<string, int>? Counts, string? Failure);

/// <summary>
/// Runs the base run of the Console out of process, with <c>oltool engine-lines &lt;folder&gt; --json</c> (decision
/// D516 of the console filter design). The run loads the base folder of the profile without mods, once for each open
/// of a profile, in parallel with the data load of the editor.
/// </summary>
/// <remarks>One run goes at a time. A new run and <see cref="Stop"/> drop the result of the run before, for example the
/// run of a profile that closed.</remarks>
internal sealed class BaseRunRunner : IDisposable
{
    private readonly Func<string, ProcessStartInfo> command;
    private readonly ToolProcess process = new();

    /// <summary>A runner that starts the oltool program next to the editor.</summary>
    public BaseRunRunner() : this(OltoolCommand)
    {
    }

    /// <param name="command">Gives the program and the arguments that load a base folder. The tests give a stub
    /// program.</param>
    internal BaseRunRunner(Func<string, ProcessStartInfo> command) => this.command = command;

    /// <summary>True while a run goes.</summary>
    public bool Running => process.Running;

    /// <summary>Starts the base run of a folder, and stops a run that goes.</summary>
    /// <param name="done">Gets the result on the UI thread. It does not run for a run that stops. When the process does
    /// not start, it runs at once.</param>
    public void Start(string baseFolder, IUIThread ui, Action<BaseRunResult> done) =>
        process.Start(command(baseFolder), ui, output => done(output.StartError is { } failure
            ? new BaseRunResult(null, failure)
            : Result(output.ExitCode, output.Output, output.Errors)));

    /// <summary>Stops the run that goes. Its result is dropped.</summary>
    public void Stop() => process.Stop();

    /// <summary>The Console warning when the base run fails (decision D517).</summary>
    internal static string FailedText(string reason) =>
        "OpenLancer cannot compare the engine messages with the base game. The Console shows all engine messages of " +
        $"this profile. Cause: {reason}";

    /// <summary><c>oltool engine-lines &lt;folder&gt; --json</c>.</summary>
    internal static ProcessStartInfo OltoolCommand(string baseFolder) =>
        new(ProgramPaths.Oltool) { ArgumentList = { "engine-lines", baseFolder, "--json" } };

    /// <summary>The result that the exit code and the output of oltool give. Exit code 0 has the JSON object of counts.
    /// Another exit code means that the run failed, and stderr tells why.</summary>
    internal static BaseRunResult Result(int exitCode, string output, string errors)
    {
        if (exitCode != 0)
        {
            var message = errors.Trim();
            return new BaseRunResult(null, message.Length > 0 ? message : $"oltool ended with exit code {exitCode}.");
        }

        try
        {
            return new BaseRunResult(BaseEngineLines.ReadJson(output), null);
        }
        catch (InvalidDataException e)
        {
            return new BaseRunResult(null, e.Message);
        }
    }

    public void Dispose() => process.Dispose();
}
