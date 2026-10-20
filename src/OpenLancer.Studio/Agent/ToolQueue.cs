using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The queue of the agent calls (section 5.5 of the MCP1 design). The SDK takes requests on other threads and runs
/// their handlers at the same time, but a session is for one thread. So each handler puts its call into this queue and
/// waits. The thread that owns the session runs the session part of one call in each <see cref="RunNext"/>. A tail runs
/// on a worker, and the next call can start while it runs.
/// </summary>
/// <remarks>
/// <para>The editor calls <see cref="RunNext"/> once in each frame, before the panels draw, with the reason why the call
/// must wait, for example an open field edit. The headless host calls it on its session thread after
/// <see cref="WaitForCall"/>.</para>
/// <para>A call that waits longer than <see cref="AgentLimits.WaitSeconds"/> ends with <c>busy</c> and the reason. A
/// cancel of a call that did not start removes it. A call whose session part started runs that part to its end.</para>
/// </remarks>
public sealed class ToolQueue : IDisposable
{
    private readonly object gate = new();
    private readonly LinkedList<Pending> waiting = new();
    private readonly Queue<long> accepted = new();
    private readonly SemaphoreSlim queued = new(0);
    private readonly AgentHost host;
    private readonly TimeProvider clock;
    private readonly AuditLog? audit;

    /// <param name="clock">The clock of the waits, the rate and the tails. The tests give a fake clock.</param>
    /// <param name="audit">The audit log, or null (decision D728).</param>
    public ToolQueue(AgentHost host, TimeProvider? clock = null, AuditLog? audit = null)
    {
        this.host = host;
        this.clock = clock ?? TimeProvider.System;
        this.audit = audit;
    }

    /// <summary>The name of the client of the last call, as the client gives it, or null.</summary>
    public string? LastClient { get; private set; }

    /// <summary>The time of the last call, or null.</summary>
    public DateTimeOffset? LastCall { get; private set; }

    /// <summary>The number of calls that wait for their session part.</summary>
    public int Waiting
    {
        get
        {
            lock (gate)
                return waiting.Count;
        }
    }

    /// <summary>
    /// Puts a call into the queue. It can run on any thread. The task ends with the result, or as cancelled when the
    /// client cancels the call before its session part starts.
    /// </summary>
    /// <param name="tool">The name of a tool of <see cref="AgentTools.All"/>.</param>
    /// <param name="client">The name of the client program, for the status bar of the editor, or null.</param>
    public Task<ToolResult> CallAsync(string tool, JsonObject? arguments, string? client, CancellationToken cancel)
    {
        var limits = host.Limits;
        Pending pending;
        lock (gate)
        {
            var now = clock.GetTimestamp();
            while (accepted.Count > 0 && clock.GetElapsedTime(accepted.Peek(), now) >= TimeSpan.FromSeconds(1))
                accepted.Dequeue();
            LastClient = client ?? LastClient;
            LastCall = clock.GetUtcNow();
            if (accepted.Count >= limits.CallsPerSecond)
            {
                var refused = ToolResult.Failure(EditErrorKind.Limit, "CallRate",
                    AgentTexts.Error("limit_calls", ("max", limits.CallsPerSecond)));
                audit?.Write(clock.GetUtcNow(), tool, arguments, refused.Outcome);
                return Task.FromResult(refused);
            }
            accepted.Enqueue(now);
            pending = new Pending(tool, arguments, now, cancel);
            pending.Node = waiting.AddLast(pending);
        }
        if (cancel.CanBeCanceled)
            pending.Registration = cancel.Register(() => Withdraw(pending));
        queued.Release();
        return pending.Done.Task;
    }

    /// <summary>Waits until a call is in the queue or the token cancels. For the session thread of a host without a
    /// window.</summary>
    /// <exception cref="OperationCanceledException">The token cancelled.</exception>
    public void WaitForCall(CancellationToken stop) => queued.Wait(stop);

    /// <summary>
    /// Runs the session part of the first call, on the thread that owns the session. While <paramref name="waitReason"/>
    /// is not null, no call starts, and each call that waited too long ends with <c>busy</c> and the reason.
    /// </summary>
    /// <param name="waitReason">Why the calls must wait now, in strict STE, or null.</param>
    /// <returns>True when a session part ran.</returns>
    public bool RunNext(string? waitReason = null)
    {
        Pending? next;
        lock (gate)
        {
            if (waitReason != null)
            {
                var limit = TimeSpan.FromSeconds(host.Limits.WaitSeconds);
                var now = clock.GetTimestamp();
                for (var node = waiting.First; node != null;)
                {
                    var after = node.Next;
                    if (clock.GetElapsedTime(node.Value.Queued, now) >= limit)
                    {
                        waiting.Remove(node);
                        node.Value.Node = null;
                        Expire(node.Value, waitReason);
                    }
                    node = after;
                }
                return false;
            }
            if (waiting.First is not { } first)
                return false;
            next = first.Value;
            waiting.RemoveFirst();
            next.Node = null;
        }
        next.Registration.Dispose();
        ToolStep step;
        try
        {
            step = AgentTools.Run(host, next.Tool, next.Arguments);
        }
        catch (Exception e)
        {
            next.Done.TrySetException(e);
            return true;
        }
        if (step.Result is { } result)
            Finish(next, result);
        else
            StartTail(next, step.Tail!);
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var pending in waiting)
                pending.Done.TrySetCanceled();
            waiting.Clear();
        }
        queued.Dispose();
    }

    /// <summary>Removes a call that did not start, after the client cancelled it.</summary>
    private void Withdraw(Pending pending)
    {
        lock (gate)
        {
            if (pending.Node == null)
                return;
            waiting.Remove(pending.Node);
            pending.Node = null;
        }
        pending.Done.TrySetCanceled(pending.Cancel);
    }

    private void Expire(Pending pending, string reason)
    {
        pending.Registration.Dispose();
        Finish(pending, ToolResult.Failure(EditErrorKind.Busy, "Waited",
            AgentTexts.Error("busy", ("reason", reason), ("seconds", host.Limits.WaitSeconds))));
    }

    /// <summary>Runs the tail of a call on a worker, with the time limit of a tail.</summary>
    private void StartTail(Pending pending, Func<CancellationToken, Task<ToolResult>> tail)
    {
        var seconds = host.Limits.TailSeconds;
        // The time limit starts with the session part, not when the worker starts.
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds), clock);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, pending.Cancel);
        _ = Task.Run(async () =>
        {
            try
            {
                Finish(pending, await tail(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Finish(pending, ToolResult.Failure(EditErrorKind.Limit, "TailTime",
                    AgentTexts.Error("limit_tail", ("seconds", seconds))));
            }
            catch (OperationCanceledException)
            {
                pending.Done.TrySetCanceled(pending.Cancel);
            }
            catch (Exception e)
            {
                pending.Done.TrySetException(e);
            }
            finally
            {
                linked.Dispose();
                timeout.Dispose();
            }
        });
    }

    /// <summary>Ends a call with its result. A result above the size limit becomes the error <c>limit</c>.</summary>
    private void Finish(Pending pending, ToolResult result)
    {
        var kilobytes = host.Limits.ResultKilobytes;
        if (result.Bytes > kilobytes * 1024)
        {
            result = ToolResult.Failure(EditErrorKind.Limit, "ResultSize",
                AgentTexts.Error("limit_result", ("size", (result.Bytes + 1023) / 1024), ("max", kilobytes)));
        }
        audit?.Write(clock.GetUtcNow(), pending.Tool, pending.Arguments, result.Outcome);
        pending.Done.TrySetResult(result);
    }

    /// <summary>A call in the queue.</summary>
    private sealed class Pending(string tool, JsonObject? arguments, long queued, CancellationToken cancel)
    {
        public string Tool { get; } = tool;

        public JsonObject? Arguments { get; } = arguments;

        /// <summary>The time stamp of the clock when the call came.</summary>
        public long Queued { get; } = queued;

        public CancellationToken Cancel { get; } = cancel;

        /// <summary>The place of the call in the queue, or null after it left the queue.</summary>
        public LinkedListNode<Pending>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }

        public TaskCompletionSource<ToolResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
