using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Runtime.Validation;

namespace OpenLancer.Editor.Editing;

/// <summary>The result of the validation of one save.</summary>
/// <param name="Save">The number of the save.</param>
/// <param name="Problems">The problems that the active mod adds, or null when the validation could not run.</param>
/// <param name="Failure">Why the validation could not run, or null.</param>
internal sealed record ValidationResult(int Save, IReadOnlyList<Diagnostic>? Problems, string? Failure);

/// <summary>The state of the last validation of a session.</summary>
/// <param name="Save">The number of the save that the validation checks. 0 is the saved files of a session without a
/// save that changed files.</param>
/// <param name="Running">True while the validation runs.</param>
/// <param name="Result">The result, or null while the validation runs and after it stopped without a result.</param>
internal sealed record ValidationState(int Save, bool Running, ValidationResult? Result);

/// <summary>
/// Validates the saved mod out of process, with <c>oltool validate &lt;profile&gt; --active &lt;mod&gt; --json</c>
/// (roadmap rule R4). Without an active mod, the command has no <c>--active</c>, and oltool reports what all mods add
/// to vanilla. The engine log and the material map are process-wide, so the editor does not load the data a second
/// time itself.
/// </summary>
/// <remarks>
/// <para>One process runs at a time. A new save stops the validation of the save before it, because that result is old
/// and the process can read files that the new save writes (review finding F16). Each result has the number of its
/// save.</para>
/// <para>The runner keeps the state of the last validation, which the gate of Play reads (section 9.3 of the M4
/// design). So the state has one owner, and the tests check its changes with stub programs.</para>
/// </remarks>
internal sealed class ValidationRunner : IDisposable
{
    private readonly Func<string, string?, ProcessStartInfo> command;
    private readonly ToolProcess process = new();

    /// <summary>A runner that starts the oltool program next to the editor.</summary>
    public ValidationRunner() : this(OltoolCommand)
    {
    }

    /// <param name="command">Gives the program and the arguments that validate a mod of a profile, from the path of
    /// the profile and the id of the mod, or null for all mods. The tests give a stub program.</param>
    internal ValidationRunner(Func<string, string?, ProcessStartInfo> command) => this.command = command;

    /// <summary>The state of the last validation, or null before the first one and after <see cref="Reset"/>.</summary>
    public ValidationState? State { get; private set; }

    /// <summary>Runs on the UI thread when a validation gives its result, after the <c>done</c> action of
    /// <see cref="Start"/>. It does not run for a validation that stops.</summary>
    public event Action<ValidationState>? Validated;

    /// <summary>Starts the validation of a save, and stops a running validation.</summary>
    /// <param name="modId">The active mod, or null for all mods of the profile.</param>
    /// <param name="done">Gets the result on the UI thread. It does not run for a validation that stops. When the
    /// process does not start, it runs at once.</param>
    public void Start(string profilePath, string? modId, int save, IUIThread ui, Action<ValidationResult> done)
    {
        Stop();
        // The state changes first, because a process that does not start gives its result at once.
        State = new ValidationState(save, Running: true, Result: null);
        process.Start(command(profilePath, modId), ui, output => Finish(output.StartError is { } failure
            ? new ValidationResult(save, null, failure)
            : Result(save, output.ExitCode, output.Output, output.Errors), done));
    }

    private void Finish(ValidationResult result, Action<ValidationResult> done)
    {
        State = new ValidationState(result.Save, Running: false, result);
        done(result);
        Validated?.Invoke(State);
    }

    /// <summary><c>oltool validate &lt;profile&gt; --active &lt;mod&gt; --json</c>, or without <c>--active</c> when no
    /// mod is active.</summary>
    internal static ProcessStartInfo OltoolCommand(string profilePath, string? modId)
    {
        var info = new ProcessStartInfo(ProgramPaths.Oltool) { ArgumentList = { "validate", profilePath } };
        if (modId != null)
        {
            info.ArgumentList.Add("--active");
            info.ArgumentList.Add(modId);
        }

        info.ArgumentList.Add("--json");
        return info;
    }

    /// <summary>The result that the exit code and the output of oltool give. Exit code 0 or 1 has the JSON list of
    /// problems. Exit code 2 means that the validation could not run, and stderr tells why.</summary>
    internal static ValidationResult Result(int save, int exitCode, string output, string errors)
    {
        if (exitCode is not (0 or 1))
        {
            var message = errors.Trim();
            return new ValidationResult(save, null,
                message.Length > 0 ? message : $"oltool ended with exit code {exitCode}.");
        }

        try
        {
            return new ValidationResult(save, ValidationJson.Read(output), null);
        }
        catch (InvalidDataException e)
        {
            return new ValidationResult(save, null, e.Message);
        }
    }

    /// <summary>Stops the running validation. Its result is dropped, and the state keeps its save without a
    /// result.</summary>
    public void Stop()
    {
        // A process runs only after the state of its save is set.
        if (process.Stop())
        {
            State = State! with { Running = false };
        }
    }

    /// <summary>Stops the running validation and forgets the state, for example when the profile closes.</summary>
    public void Reset()
    {
        Stop();
        State = null;
    }

    public void Dispose() => process.Dispose();
}
