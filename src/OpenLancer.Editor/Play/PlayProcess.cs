using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LibreLancer;
using Microsoft.Win32.SafeHandles;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

/// <summary>
/// The game process of a play (sections 6.4 and 9.4 of the M4 design). It writes the commands of the editor on the
/// standard input of the game. It gives the events of the game and the warnings and errors of the engine log to the UI
/// thread.
/// </summary>
/// <remarks>
/// <para>On Windows, the process is in a job object that ends the process when the job closes (decision D53). Only
/// this object holds the job. So the game ends at <see cref="Dispose"/>, and also when the editor process ends in any
/// way, for example by a crash or a kill, because Windows then closes the handle. When Windows does not give the job,
/// <see cref="Dispose"/> kills the process.</para>
/// <para>A task reads the output. It drops the lines that are not events, warnings or errors: the game log of the
/// engine keeps them. After the end of the output and of the process, it gives the exit to the UI thread. So each
/// event and each log line comes before the exit.</para>
/// </remarks>
internal sealed partial class PlayProcess : IDisposable
{
    /// <summary>The lines of the error output that the exit gives. The runtime writes the cause of a crash there,
    /// for example a failed assert, and the stack trace after it.</summary>
    private const int ErrorLines = 3;

    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;

    private readonly Process process;
    private readonly StreamWriter input;
    private readonly SafeJobHandle? job;
    private readonly Task reader;

    // Kill and the end of the reader use the process object on two threads.
    private readonly object processLock = new();
    private bool ended;

    private PlayProcess(Process process, IUIThread ui, Action<PlayEvent> onEvent, Action<string> onLogLine,
        Action<int, string> onExit)
    {
        this.process = process;
        input = process.StandardInput;
        job = OperatingSystem.IsWindows() ? KillOnCloseJob(process) : null;
        reader = Task.Run(() => Read(ui, onEvent, onLogLine, onExit));
    }

    /// <summary>The program and the arguments that start the play program next to the editor.</summary>
    public static ProcessStartInfo Command(PlayArgs args)
    {
        var info = new ProcessStartInfo(ProgramPaths.Play);
        foreach (var argument in args.ToArguments())
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>Starts the program with redirected input and output, and without a console window. The game window
    /// shows, because it is not a console window.</summary>
    /// <param name="onEvent">Gets each event on the UI thread.</param>
    /// <param name="onLogLine">Gets each warning or error line of the engine log on the UI thread (see
    /// <see cref="ConsoleLog.ParseGameLine"/>).</param>
    /// <param name="onExit">Gets the exit code and the first lines of the error output on the UI thread, after the
    /// last event.</param>
    /// <exception cref="Win32Exception">The program did not start, for example because it is missing.</exception>
    public static PlayProcess Start(ProcessStartInfo info, IUIThread ui, Action<PlayEvent> onEvent,
        Action<string> onLogLine, Action<int, string> onExit)
    {
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException($"{info.FileName} did not start.");
        return new PlayProcess(process, ui, onEvent, onLogLine, onExit);
    }

    /// <summary>Writes a command on the standard input of the game.</summary>
    /// <returns>False when the game ended or its input is closed. It never throws for an ended game (review finding
    /// R6).</returns>
    public bool Send(PlayCommand command)
    {
        try
        {
            input.WriteLine(PlayProtocol.Write(command));
            input.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Closes the standard input. The game exits at the end of its input, as at stop.</summary>
    public void CloseInput()
    {
        try
        {
            input.Dispose();
        }
        catch (IOException)
        {
            // The game ended, so the last write of the input cannot reach it.
        }
    }

    /// <summary>Waits for the end of the game and of its output.</summary>
    /// <returns>False when the game did not end in the time.</returns>
    public bool WaitForExit(TimeSpan limit) => reader.Wait(limit);

    /// <summary>Ends the game process at once. It does nothing when the game ended.</summary>
    public void Kill()
    {
        lock (processLock)
        {
            if (ended)
            {
                return;
            }

            try
            {
                process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // The process ends now. The reader gives its exit.
            }
        }
    }

    /// <summary>Closes the job, which ends the game. Without a job, it kills the game.</summary>
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

        CloseInput();
    }

    private async Task Read(IUIThread ui, Action<PlayEvent> onEvent, Action<string> onLogLine,
        Action<int, string> onExit)
    {
        var errors = FirstLines(process.StandardError);
        using (var output = process.StandardOutput)
        {
            while (await output.ReadLineAsync() is { } line)
            {
                if (PlayProtocol.ReadEvent(line) is { } e)
                {
                    ui.QueueUIThread(() => onEvent(e));
                }
                else if (ConsoleLog.ParseGameLine(line) != null)
                {
                    ui.QueueUIThread(() => onLogLine(line));
                }
            }
        }

        var errorText = await errors;
        await process.WaitForExitAsync();
        int code;
        lock (processLock)
        {
            code = process.ExitCode;
            ended = true;
            process.Dispose();
        }

        ui.QueueUIThread(() => onExit(code, errorText));
    }

    /// <summary>The first non-empty lines of the error output, in one line. It reads the output to its end.</summary>
    private static async Task<string> FirstLines(StreamReader errors)
    {
        var lines = new List<string>();
        using (errors)
        {
            while (await errors.ReadLineAsync() is { } line)
            {
                if (lines.Count < ErrorLines && line.Trim() is { Length: > 0 } text)
                {
                    lines.Add(text);
                }
            }
        }

        return string.Join(" ", lines);
    }

    /// <summary>A job that ends the process when the job closes, or null when Windows does not give it.</summary>
    private static SafeJobHandle? KillOnCloseJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        if (!job.IsInvalid &&
            SetInformationJobObject(job, ExtendedLimitInformationClass, limits, Marshal.SizeOf<ExtendedLimitInformation>()) &&
            AssignProcessToJobObject(job, process.SafeHandle))
        {
            return job;
        }

        job.Dispose();
        return null;
    }

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass,
        in ExtendedLimitInformation info, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
