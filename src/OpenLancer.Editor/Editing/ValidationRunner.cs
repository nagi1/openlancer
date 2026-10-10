using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Runtime.Validation;

namespace OpenLancer.Editor.Editing;

/// <summary>The result of the validation of one save.</summary>
/// <param name="Save">The number of the save.</param>
/// <param name="Problems">The problems that the active mod adds, or null when the validation could not run.</param>
/// <param name="Failure">Why the validation could not run, or null.</param>
internal sealed record ValidationResult(int Save, IReadOnlyList<Diagnostic>? Problems, string? Failure);

/// <summary>
/// Validates the saved mod out of process, with <c>oltool validate &lt;profile&gt; --active &lt;mod&gt; --json</c>
/// (roadmap rule R4). The engine log and the material map are process-wide, so the editor does not load the data a
/// second time itself.
/// </summary>
/// <remarks>
/// One process runs at a time. A new save stops the validation of the save before it, because that result is old and
/// the process can read files that the new save writes (review finding F16). Each result has the number of its save.
/// </remarks>
internal sealed class ValidationRunner : IDisposable
{
    private readonly Func<string, string, ProcessStartInfo> command;
    private Process? process;

    // Changes when a validation starts or stops, so that the result of a stopped process is dropped.
    private int generation;

    /// <summary>A runner that starts the oltool program next to the editor.</summary>
    public ValidationRunner() : this(OltoolCommand)
    {
    }

    /// <param name="command">Gives the program and the arguments that validate a mod of a profile, from the path of
    /// the profile and the id of the mod. The tests give a stub program.</param>
    internal ValidationRunner(Func<string, string, ProcessStartInfo> command) => this.command = command;

    /// <summary>The number of the save that the running validation checks, or null.</summary>
    public int? RunningSave { get; private set; }

    /// <summary>The oltool program next to the editor. The editor project references the tool project for it.</summary>
    private static string ToolPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oltool.exe" : "oltool");

    /// <summary>Starts the validation of a save, and stops a running validation.</summary>
    /// <param name="done">Gets the result on the UI thread. It does not run for a validation that stops.</param>
    public void Start(string profilePath, string modId, int save, IUIThread ui, Action<ValidationResult> done)
    {
        Stop();
        var info = command(profilePath, modId);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        Process started;
        try
        {
            started = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            done(new ValidationResult(save, null, $"{info.FileName} did not start: {e.Message}"));
            return;
        }

        process = started;
        RunningSave = save;
        var id = ++generation;
        Task.Run(async () =>
        {
            ValidationResult result;
            try
            {
                var output = started.StandardOutput.ReadToEndAsync();
                var errors = started.StandardError.ReadToEndAsync();
                await started.WaitForExitAsync();
                result = Result(save, started.ExitCode, await output, await errors);
            }
            finally
            {
                started.Dispose();
            }
            ui.QueueUIThread(() =>
            {
                if (id != generation)
                {
                    return;
                }

                process = null;
                RunningSave = null;
                done(result);
            });
        });
    }

    /// <summary><c>oltool validate &lt;profile&gt; --active &lt;mod&gt; --json</c>.</summary>
    private static ProcessStartInfo OltoolCommand(string profilePath, string modId)
    {
        var info = new ProcessStartInfo(ToolPath);
        foreach (var argument in (string[])["validate", profilePath, "--active", modId, "--json"])
            info.ArgumentList.Add(argument);
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

    /// <summary>Stops the running validation. Its result is dropped.</summary>
    public void Stop()
    {
        if (process == null)
        {
            return;
        }

        generation++;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process ended already. Its task disposes it.
        }

        process = null;
        RunningSave = null;
    }

    public void Dispose() => Stop();
}
