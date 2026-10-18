using System;
using System.ComponentModel;
using System.Diagnostics;
using LibreLancer;

namespace OpenLancer.Editor.Play;

/// <summary>A program of a play copy that the editor started: the game or FLServer (milestone M11). The tests give a
/// fake.</summary>
internal interface IGameProcess : IDisposable
{
    /// <summary>Asks the program to close its main window, as its close button does. False when it has no main
    /// window.</summary>
    bool CloseMainWindow();

    /// <summary>Ends the program at once.</summary>
    void Kill();
}

/// <summary>Starts a program of a play copy and gives its exit code on the UI thread.</summary>
internal delegate IGameProcess GameStarter(ProcessStartInfo info, Action<int> exited);

/// <summary>
/// The game or FLServer of a play copy (section 8.6 of the M11 design, decision D313). The process is in a job that ends
/// it with the editor, as the game of Play in OpenLancer is (<see cref="ProcessJob"/>). It has no redirected input or
/// output, because the retail programs have no protocol with the editor.
/// </summary>
internal sealed class GameProcess : IGameProcess
{
    private readonly Process process;
    private readonly ProcessJob? job;

    private GameProcess(Process process)
    {
        this.process = process;
        job = ProcessJob.For(process);
    }

    /// <summary>A starter that gives the exit code on the UI thread.</summary>
    public static GameStarter Starter(IUIThread ui) => (info, exited) => Start(info, ui, exited);

    /// <summary>Starts the program with the working folder and the arguments of the start info.</summary>
    /// <param name="exited">Gets the exit code on the UI thread, or -1 when Windows does not give it.</param>
    /// <exception cref="Win32Exception">The program did not start.</exception>
    public static GameProcess Start(ProcessStartInfo info, IUIThread ui, Action<int> exited)
    {
        info.UseShellExecute = false;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            int code;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }

            ui.QueueUIThread(() => exited(code));
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"{info.FileName} did not start.");
        }

        return new GameProcess(process);
    }

    public bool CloseMainWindow()
    {
        try
        {
            process.Refresh();
            return process.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            // The program ended. Its exit follows.
            return false;
        }
    }

    public void Kill()
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // The program ends now. Its exit follows.
        }
    }

    /// <summary>Closes the job, which ends the program. Without a job, it kills the program.</summary>
    public void Dispose()
    {
        if (job != null)
        {
            job.Dispose();
        }
        else
        {
            Kill();
        }

        process.Dispose();
    }
}
