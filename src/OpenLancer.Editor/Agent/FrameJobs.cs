using System;
using System.Collections.Generic;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Editor.Agent;

/// <summary>A job of the render service that runs over several frames, for example an image of a cutscene.</summary>
internal interface IFrameJob : IDisposable
{
    /// <summary>True after the client cancelled the call of the job. Any thread can set it.</summary>
    bool Cancelled { get; }

    /// <summary>Runs the job within a budget of time. Gives true when the job ended: done, refused or
    /// cancelled.</summary>
    bool Frame(TimeSpan budget);

    /// <summary>Ends the call of the job with an error.</summary>
    void Fail(EditError error);

    /// <summary>Ends the call of the job as cancelled.</summary>
    void EndCancelled();
}

/// <summary>
/// The jobs of the render service that run over several frames, in the order of their calls. Only the first job runs in
/// a frame, and it gets the whole budget. So all jobs together take no more time in a frame than one job, and each call
/// waits for its turn. A cancelled job ends in the next frame, also while it waits.
/// </summary>
/// <param name="capacity">The most jobs that wait or run at a time.</param>
/// <param name="budget">The time that the jobs can use in one frame.</param>
/// <param name="fault">The error of a job that fails with an exception. The window draws on after it.</param>
internal sealed class FrameJobs(int capacity, TimeSpan budget, Func<Exception, EditError> fault)
{
    private readonly List<IFrameJob> jobs = [];

    /// <summary>The number of jobs that wait or run.</summary>
    public int Count => jobs.Count;

    /// <summary>Adds a job after the others. Gives false and does not add it when <c>capacity</c> jobs wait or
    /// run.</summary>
    public bool TryAdd(IFrameJob job)
    {
        if (jobs.Count >= capacity)
            return false;
        jobs.Add(job);
        return true;
    }

    /// <summary>Ends the cancelled jobs, then runs the first job within the budget. Call it once in each frame, before
    /// the panels draw.</summary>
    public void Frame()
    {
        for (var i = jobs.Count - 1; i >= 0; i--)
        {
            if (!jobs[i].Cancelled)
                continue;
            jobs[i].EndCancelled();
            jobs[i].Dispose();
            jobs.RemoveAt(i);
        }

        if (jobs.Count == 0)
            return;
        var job = jobs[0];
        bool ended;
        try
        {
            ended = job.Frame(budget);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            job.Fail(fault(e));
            ended = true;
        }

        if (!ended)
            return;
        job.Dispose();
        jobs.RemoveAt(0);
    }

    /// <summary>Ends each job with an error, for example when the profile closes.</summary>
    public void Fail(EditError error)
    {
        foreach (var job in jobs)
        {
            job.Fail(error);
            job.Dispose();
        }

        jobs.Clear();
    }
}
