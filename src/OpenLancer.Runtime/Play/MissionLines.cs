using System;
using System.IO;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// Writes the trigger changes of a <see cref="MissionObserver"/> as <c>@ol mission</c> event lines (section 6.2 of the
/// M6c design). A timer of the play host calls <see cref="Write"/>, so the writes come from a thread of the pool. A write
/// that fails ends the writes, and <see cref="Close"/> ends them after an error event. So no mission line follows the
/// error event, and a closed output does not end the play process from the timer.
/// </summary>
/// <remarks>The members are safe for two threads.</remarks>
public sealed class MissionLines(MissionObserver observer, TextWriter output)
{
    private readonly object gate = new();
    private bool closed;

    /// <summary>True after <see cref="Close"/>, or after a write that failed.</summary>
    public bool Closed
    {
        get
        {
            lock (gate)
                return closed;
        }
    }

    /// <summary>Writes the lines that the observer has, in order. It does nothing after <see cref="Close"/>. When the
    /// output is closed, for example because the editor closed the pipe, the writes end. The game then exits at the end
    /// of its input.</summary>
    public void Write()
    {
        lock (gate)
        {
            if (closed)
                return;
            try
            {
                foreach (var line in observer.Take())
                    output.WriteLine(PlayProtocol.Write(line));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                closed = true;
            }
        }
    }

    /// <summary>Ends the writes, for example before an error event. A write that runs ends first.</summary>
    public void Close()
    {
        lock (gate)
            closed = true;
    }
}
