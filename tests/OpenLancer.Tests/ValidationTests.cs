using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The validation after a save: the JSON list that <c>oltool validate --json</c> writes, the result that the editor
/// reads from the process, and the runner that starts one process at a time. The retail test
/// <c>OltoolValidateTests</c> runs the real oltool.
/// </summary>
public sealed class ValidationTests
{
    [Fact]
    public void TheJsonListKeepsEachProblem()
    {
        Diagnostic[] problems =
        [
            Diagnostic.Error(@"C:\mods\proof\patches\goods.ini", "a \"quoted\" error"),
            Diagnostic.Warning("Engine", "a warning"),
        ];

        var json = ValidationJson.Write(problems);

        Assert.Contains("\"severity\": \"error\"", json);
        Assert.Equal(problems, ValidationJson.Read(json));
        Assert.Empty(ValidationJson.Read(ValidationJson.Write([])));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[{\"severity\": \"fatal\", \"source\": \"s\", \"message\": \"m\"}]")]
    [InlineData("[{\"severity\": \"error\", \"message\": \"m\"}]")]
    [InlineData("[null]")]
    public void TextThatIsNotAListOfProblemsIsRefused(string json)
    {
        Assert.Throws<InvalidDataException>(() => ValidationJson.Read(json));
    }

    [Fact]
    public void ExitCodesZeroAndOneHaveTheProblemsAndTwoHasTheFailure()
    {
        var json = ValidationJson.Write([Diagnostic.Error("s", "m")]);

        Assert.Equal([Diagnostic.Error("s", "m")], ValidationRunner.Result(4, 1, json, "")!.Problems);
        Assert.Empty(ValidationRunner.Result(4, 0, "[]", "")!.Problems!);
        var failed = ValidationRunner.Result(4, 2, "", "profile.json: File not found.\n");
        Assert.Null(failed.Problems);
        Assert.Equal("profile.json: File not found.", failed.Failure);
        Assert.Equal(4, failed.Save);
        Assert.Contains("exit code 3", ValidationRunner.Result(4, 3, "", "").Failure);
        Assert.NotNull(ValidationRunner.Result(4, 0, "garbage", "").Failure);
    }

    /// <summary>
    /// A new validation stops the running one: its process ends before the files change, and its result does not get
    /// to the editor (review finding F16). The first stub program writes a file when it is not stopped.
    /// </summary>
    [Fact]
    public void ANewValidationStopsTheRunningOneAndDropsItsResult()
    {
        using var ws = new TestWorkspace();
        var ended = Path.Combine(ws.Root, "slow-ended.txt");
        var ui = new QueuedUIThread();
        var results = new List<ValidationResult>();
        using var runner = new ValidationRunner((profile, _) => profile == "slow" ? Slow(ended) : Fast());

        runner.Start("slow", "act", 1, ui, results.Add);
        Assert.Equal(1, runner.RunningSave);
        runner.Start("fast", "act", 2, ui, results.Add);
        Assert.Equal(2, runner.RunningSave);
        // Each process queues its result when it ends: the first when the second start stopped it.
        ui.Run(2);

        var result = Assert.Single(results);
        Assert.Equal(2, result.Save);
        Assert.Empty(result.Problems!);
        Assert.Null(runner.RunningSave);
        Assert.False(File.Exists(ended));
    }

    /// <summary>A stub program that waits about three seconds, writes the file, then writes an empty list.</summary>
    private static ProcessStartInfo Slow(string ended) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & echo ended>\"{ended}\" & echo []")
        : new ProcessStartInfo("/bin/sh", ["-c", $"sleep 3; echo ended > '{ended}'; echo []"]);

    /// <summary>A stub program that writes an empty list.</summary>
    private static ProcessStartInfo Fast() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c echo []")
        : new ProcessStartInfo("/bin/sh", ["-c", "echo []"]);

    /// <summary>A UI thread that keeps the queued actions until the test runs them.</summary>
    private sealed class QueuedUIThread : IUIThread
    {
        private readonly BlockingCollection<Action> queue = [];

        public void QueueUIThread(Action work) => queue.Add(work);

        /// <summary>Runs the next actions, and waits for each one.</summary>
        public void Run(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(queue.TryTake(out var work, TimeSpan.FromSeconds(30)), "No action was queued.");
                work();
            }
        }
    }
}
