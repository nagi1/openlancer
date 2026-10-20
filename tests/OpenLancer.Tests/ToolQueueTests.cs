using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>A clock for the tests: the time moves only with <see cref="Advance"/>, and a timer fires when the time
/// passes its due time.</summary>
internal sealed class FakeClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<FakeTimer> timers = [];
    private DateTimeOffset now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return now;
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan time)
    {
        List<FakeTimer> due;
        lock (gate)
        {
            now += time;
            due = timers.Where(t => t.Due <= now).ToList();
            timers.RemoveAll(due.Contains);
        }
        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class FakeTimer(FakeClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                clock.timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = clock.now + dueTime;
                    clock.timers.Add(this);
                }
            }
            return true;
        }

        public void Dispose()
        {
            lock (clock.gate)
                clock.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// The queue of the agent calls (section 13.2, items 1 and 5 of the MCP1 design) with a fake thread of the session and a
/// fake clock: one session part in each run, the waits, <c>busy</c> after the wait limit, the cancel of a call that did
/// not start, the rate limit, the size limit of a result and the audit log.
/// </summary>
public sealed class ToolQueueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Waits for a call that must end soon.</summary>
    private static Task<ToolResult> Done(Task<ToolResult> call) => call.WaitAsync(TimeSpan.FromSeconds(10), Token);

    private static (string?, string?) Code(ToolResult result) =>
        ((string?)result.Content["code"], (string?)result.Content["detail"]);

    [Fact]
    public async Task EachRunStartsTheSessionPartOfOneCall()
    {
        using var queue = new ToolQueue(new TestAgentHost(null), new FakeClock());

        var calls = Enumerable.Range(0, 3).Select(_ => queue.CallAsync("studio_status", null, "test", Token)).ToList();

        Assert.True(queue.RunNext());
        Assert.False((await Done(calls[0])).IsError);
        Assert.False(calls[1].IsCompleted);
        Assert.Equal(2, queue.Waiting);
        Assert.True(queue.RunNext());
        Assert.True(queue.RunNext());
        Assert.False(queue.RunNext());
        Assert.All(await Task.WhenAll(calls), r => Assert.False(r.IsError));
        Assert.Equal("test", queue.LastClient);
    }

    [Fact]
    public async Task ACallWaitsWhileTheEditorIsBusyAndEndsWithBusyAfterTheLimit()
    {
        var clock = new FakeClock();
        using var queue = new ToolQueue(new TestAgentHost(null), clock);
        const string reason = "The user edits a field now.";

        var call = queue.CallAsync("studio_status", null, null, Token);
        Assert.False(queue.RunNext(reason));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(queue.RunNext(reason));
        Assert.False(call.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(queue.RunNext(reason));

        var result = await Done(call);
        Assert.True(result.IsError);
        Assert.Equal(("busy", "Waited"), Code(result));
        Assert.Equal("The user edits a field now. The call waited 30 seconds. Try again in a few seconds.",
            (string?)result.Content["message"]);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public async Task ACallStartsAfterTheGestureOfTheUserEnds()
    {
        var clock = new FakeClock();
        using var queue = new ToolQueue(new TestAgentHost(null), clock);

        var call = queue.CallAsync("studio_status", null, null, Token);
        Assert.False(queue.RunNext("The user drags an item now."));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(queue.RunNext());

        Assert.False((await Done(call)).IsError);
    }

    [Fact]
    public async Task ACancelOfACallThatDidNotStartRemovesIt()
    {
        using var queue = new ToolQueue(new TestAgentHost(null), new FakeClock());
        using var cancel = new CancellationTokenSource();

        var cancelled = queue.CallAsync("studio_status", null, null, cancel.Token);
        var other = queue.CallAsync("studio_status", null, null, Token);
        cancel.Cancel();

        Assert.True(cancelled.IsCanceled);
        Assert.Equal(1, queue.Waiting);
        Assert.True(queue.RunNext());
        Assert.False((await Done(other)).IsError);
    }

    [Fact]
    public async Task MoreCallsThanTheRateLimitInOneSecondGiveLimit()
    {
        var clock = new FakeClock();
        var host = new TestAgentHost(null) { TestLimits = new AgentLimits { CallsPerSecond = 2 } };
        using var queue = new ToolQueue(host, clock);

        var calls = Enumerable.Range(0, 3).Select(_ => queue.CallAsync("studio_status", null, null, Token)).ToList();
        clock.Advance(TimeSpan.FromSeconds(1));
        var later = queue.CallAsync("studio_status", null, null, Token);

        Assert.True(calls[2].IsCompleted);
        Assert.Equal(("limit", "CallRate"), Code(await Done(calls[2])));
        Assert.Equal(3, queue.Waiting);
        Assert.False(later.IsCompleted);
    }

    [Fact]
    public async Task AResultAboveTheSizeLimitGivesLimit()
    {
        var host = new TestAgentHost(null) { TestLimits = new AgentLimits { ResultKilobytes = 0 } };
        using var queue = new ToolQueue(host, new FakeClock());

        var call = queue.CallAsync("studio_status", null, null, Token);
        queue.RunNext();

        var result = await Done(call);
        Assert.Equal(("limit", "ResultSize"), Code(result));
        Assert.StartsWith("The result has 1 KB of text, and one result can have at most 0 KB.",
            (string?)result.Content["message"]);
    }

    [Fact]
    public async Task TheQueueWritesEachCallIntoTheAuditLog()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, AuditLog.FileName(new DateTime(2026, 10, 20, 14, 22, 33, DateTimeKind.Utc)));
        try
        {
            using (var log = new AuditLog(file))
            using (var queue = new ToolQueue(new TestAgentHost(null), new FakeClock(), log))
            {
                var ok = queue.CallAsync("studio_status", null, null, Token);
                var bad = queue.CallAsync("studio_status", JsonNode.Parse("""{ "limit": 2 }""")!.AsObject(), null, Token);
                queue.RunNext();
                queue.RunNext();
                await Done(ok);
                await Done(bad);
            }

            Assert.Equal("agent-20261020-142233.jsonl", Path.GetFileName(file));
            using var reader = new StreamReader(file);
            var entries = AuditLog.Read(reader).ToList();
            Assert.Equal(["studio_status", "studio_status"], entries.Select(e => e.Tool));
            Assert.Equal(new ToolOutcome("ok"), entries[0].Expected);
            Assert.Equal(new ToolOutcome("error", "invalid_argument", "BadArgument"), entries[1].Expected);
            Assert.Equal(2, (int)entries[1].Arguments!["limit"]!);
            Assert.DoesNotContain("token", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TheAuditLogReaderNamesTheLineOfAnError()
    {
        static string Read(string text)
        {
            var e = Assert.Throws<InvalidDataException>(() => AuditLog.Read(new StringReader(text)).ToList());
            return e.Message;
        }

        Assert.StartsWith("Line 2 is not valid JSON:", Read("{\"tool\": \"studio_status\"}\nx"));
        Assert.Equal("Line 1 has no tool name.", Read("{\"arguments\": {}}"));
        Assert.Equal("The result of line 1 has no status.", Read("{\"tool\": \"x\", \"result\": {}}"));
        Assert.Empty(AuditLog.Read(new StringReader("\n\n")).ToList());
    }
}
