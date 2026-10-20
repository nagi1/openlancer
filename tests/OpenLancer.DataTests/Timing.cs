using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace OpenLancer.DataTests;

/// <summary>
/// The measures of the timing gates of the retail tests. A gate must measure the code, not the load of the computer.
/// Other builds and the unit tests can run at the same time, and they took the processor from the measures. So each
/// measure collects the garbage of the earlier steps first, and runs with a high priority, as a benchmark program does
/// (see <see cref="Measure"/>). The gates are of two kinds:
/// <list type="bullet">
/// <item>A gate on a first run measures one run, and it does not try again. Examples are the first catch-up of a
/// system and the first build of an index. A second run is faster, because the runtime compiled the code and the
/// caches are full. So a second try would hide a slow first run. A gate on a step that the test cannot run again, for
/// example a delete, also measures one run. A burst of other work can fail these gates.</item>
/// <item>A gate on a step that runs often measures warm code, so it can try again (see <see cref="TimingGates"/>). It
/// takes the best of its tries. While the best try is over the limit, it pauses and tries again until the patience of
/// the test ends. So a burst of other work does not fail it, but slow code still fails.</item>
/// </list>
/// </summary>
/// <remarks>
/// Each test with a timing gate has the trait <c>Category=Performance</c>. A busy computer can be slower than a gate
/// for a long time, for example during a long build. Then run the gates alone, on a quiet computer:
/// <c>dotnet test tests/OpenLancer.DataTests --filter Category=Performance</c>.
/// </remarks>
internal static class Timing
{
    /// <summary>The name of the trait: <c>[Trait(Timing.Category, Timing.Performance)]</c>.</summary>
    public const string Category = "Category";

    /// <summary>The value of the trait of a test with a timing gate.</summary>
    public const string Performance = "Performance";

    /// <summary>How long the gates of one test try again while a best try is over its limit.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>Gives the time of one run of an action. The measure collects the garbage first, and runs with a high
    /// priority (see <see cref="HighPriority"/>). A gate on a first run uses one measure.</summary>
    public static TimeSpan Measure(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var priority = HighPriority();
        var start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start);
    }

    /// <summary>
    /// Gives the process the priority class High and the current thread the priority Highest until the result is
    /// disposed. Then the threads of other programs with the normal priority, for example a build, do not take the
    /// processor from a measure. The worker threads of the process also get the class High. When the system refuses
    /// the class, the measure runs with the normal class. Dispose the result on the same thread.
    /// </summary>
    public static IDisposable HighPriority() => new Priority();

    private sealed class Priority : IDisposable
    {
        private readonly ProcessPriorityClass? processBefore = SetProcess(ProcessPriorityClass.High);
        private readonly ThreadPriority threadBefore = Thread.CurrentThread.Priority;

        public Priority() => Thread.CurrentThread.Priority = ThreadPriority.Highest;

        public void Dispose()
        {
            Thread.CurrentThread.Priority = threadBefore;
            if (processBefore is { } before)
                SetProcess(before);
        }

        /// <summary>Sets the priority class of the process. Gives the class before it, or null when the system refuses
        /// the class.</summary>
        private static ProcessPriorityClass? SetProcess(ProcessPriorityClass value)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var before = process.PriorityClass;
                process.PriorityClass = value;
                return before;
            }
            catch (Exception e) when (e is Win32Exception or PlatformNotSupportedException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// The gates of one test on steps that run often (see <see cref="Timing"/>). Each gate takes the best of its tries.
/// While the best try is over the limit, the gate pauses and tries again. All gates of the test share one patience,
/// from the start of the object. So a test with many gates on slow code fails after one patience, not after one
/// patience for each gate.
/// </summary>
/// <remarks>Each try must measure warm code. Run the step once before the gate, or use <see cref="WarmGate"/>. Do not
/// put a first run on a gate: measure it once with <see cref="Timing.Measure"/>.</remarks>
internal sealed class TimingGates
{
    /// <summary>The pause before a gate tries again, so that a burst of other work can end.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    private readonly long start = Stopwatch.GetTimestamp();
    private readonly TimeSpan patience;

    /// <param name="patience">How long the gates try again. Null gives <see cref="Timing.Patience"/>.</param>
    public TimingGates(TimeSpan? patience = null) => this.patience = patience ?? Timing.Patience;

    /// <summary>True while the gates can try again.</summary>
    private bool Patient => Stopwatch.GetElapsedTime(start) < patience;

    /// <summary>
    /// Gives the best time of the tries of a gate. The gate runs <paramref name="tries"/> tries. While the best try is
    /// not under the limit, it pauses and tries again until the patience of the test ends. Each try makes its own
    /// state and gives its time, for example with <see cref="Timing.Measure"/>. The test compares the result with the
    /// limit.
    /// </summary>
    public TimeSpan Gate(TimeSpan limit, Func<TimeSpan> tryOnce, int tries = 1)
    {
        var best = TimeSpan.MaxValue;
        for (var count = 0; count < tries || best >= limit && Patient; count++)
        {
            if (count >= tries)
                Thread.Sleep(Pause);
            var time = tryOnce();
            if (time < best)
                best = time;
        }

        return best;
    }

    /// <summary>A gate on a step that can run again: one run warms up the code and the caches, and is not measured.
    /// Then the gate measures the step (see <see cref="Gate"/>).</summary>
    public TimeSpan WarmGate(TimeSpan limit, int tries, Action action)
    {
        action();
        return Gate(limit, () => Timing.Measure(action), tries);
    }
}
