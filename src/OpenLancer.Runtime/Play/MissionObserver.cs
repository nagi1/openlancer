using System.Collections.Generic;
using System.Diagnostics;
using LibreLancer.Missions;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// The observer of the mission runtime of a play (section 6.2 of the M6c design, fork commit OL-0070). The server thread
/// calls it, also inside the lock of the runtime, so it only queues each change with the time since the load of the
/// mission. The queue holds at most <see cref="Capacity"/> changes. When it is full, it counts the dropped changes, and
/// the next event line gives the count. The play host takes the lines of the queue at most each 100 ms and writes them
/// as <c>@ol mission</c> events.
/// </summary>
/// <remarks>The members are safe for two threads: the server thread adds, and one other thread takes.</remarks>
public sealed class MissionObserver : IMissionObserver
{
    /// <summary>The most changes in the queue.</summary>
    public const int Capacity = 10_000;

    /// <summary>The most changes in one event line.</summary>
    public const int LineChanges = 500;

    private readonly object gate = new();
    private readonly Queue<Item> queue = new();
    private readonly Stopwatch clock = new();
    private int changes;
    private int dropped;
    private string? mission;
    private bool reset;

    public void MissionLoaded(string name)
    {
        lock (gate)
        {
            // The times of the changes of the new runtime start at its load.
            clock.Restart();
            queue.Enqueue(new Item(name, null));
        }
    }

    public void TriggerChanged(string trigger, MissionTriggerChange change)
    {
        lock (gate)
        {
            if (changes >= Capacity)
            {
                dropped++;
                return;
            }

            changes++;
            queue.Enqueue(new Item(null, new MissionChange(clock.ElapsedMilliseconds, trigger, Kind(change))));
        }
    }

    /// <summary>
    /// Takes the queue as event lines: a line for each run of at most <see cref="LineChanges"/> changes, and a new line
    /// after each load of a mission. The first line after a load has <c>reset</c>, also without a change. A line gives
    /// the changes that the queue dropped before it. Empty when nothing changed.
    /// </summary>
    public IReadOnlyList<PlayEvent.MissionChanges> Take()
    {
        var lines = new List<PlayEvent.MissionChanges>();
        lock (gate)
        {
            var line = new List<MissionChange>();

            void Flush()
            {
                // A change before the first load of a mission has no mission, so it goes.
                if (mission == null)
                    line.Clear();
                if (mission == null || line.Count == 0 && !reset && dropped == 0)
                    return;
                lines.Add(new PlayEvent.MissionChanges(mission, reset, dropped, line));
                line = [];
                reset = false;
                dropped = 0;
            }

            while (queue.TryDequeue(out var item))
            {
                if (item.Loaded is { } name)
                {
                    Flush();
                    mission = name;
                    reset = true;
                    continue;
                }

                changes--;
                line.Add(item.Change!);
                if (line.Count == LineChanges)
                    Flush();
            }

            Flush();
        }

        return lines;
    }

    private static MissionChangeKind Kind(MissionTriggerChange change) => change switch
    {
        MissionTriggerChange.Started => MissionChangeKind.Started,
        MissionTriggerChange.Ran => MissionChangeKind.Ran,
        _ => MissionChangeKind.Stopped,
    };

    /// <summary>A load of a mission, or a change.</summary>
    private readonly record struct Item(string? Loaded, MissionChange? Change);
}
