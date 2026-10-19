using System;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The public revision of the session and the cache stamp (decision D764 of the MCP1 design, MCP1 section 13.1 item 14,
/// M6b step 1.0).
/// </summary>
public sealed class EditRevisionTests
{
    [Fact]
    public void TheRevisionGrowsOnlyWhenTheHistoryChangesTheState()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var revision = session.Revision;

        session.Execute(Set(Good("g1"), "price", new Int32Value(1)));
        Assert.Equal(++revision, session.Revision);
        session.Undo();
        Assert.Equal(++revision, session.Revision);
        session.Redo();
        Assert.Equal(++revision, session.Revision);
        var batch = session.BeginBatch("Batch");
        batch.Run(Set(Good("g2"), "price", new Int32Value(2)));
        Assert.Equal(revision, session.Revision);
        batch.Commit();
        Assert.Equal(++revision, session.Revision);
        using (var transaction = session.Begin("Drag"))
        {
            transaction.Apply(Set(Good("g1"), "price", new Int32Value(3)));
            Assert.Equal(revision, session.Revision);
            transaction.Commit();
        }
        Assert.Equal(++revision, session.Revision);
    }

    [Fact]
    public void APreviewACancelARefusalAndASaveKeepTheRevision()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "price", new Int32Value(1)));
        var revision = session.Revision;

        var preview = session.BeginBatch("Preview");
        preview.Run(Set(Good("g1"), "price", new Int32Value(2)));
        Assert.Equal(revision, preview.Preview().Revision);

        var cancelled = session.BeginBatch("Cancel");
        cancelled.Run(Set(Good("g1"), "price", new Int32Value(3)));
        cancelled.Cancel();

        Assert.Throws<ArgumentException>(() => session.Execute(Set(Good("missing"), "price", new Int32Value(4))));

        using (var transaction = session.Begin("Drag"))
            transaction.Apply(Set(Good("g1"), "price", new Int32Value(5)));

        Assert.True(session.Save().Saved);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(["1"], Values(session, Good("g1"), "price"));
    }

    [Fact]
    public void ARestoreOfUnsavedEditsGrowsTheRevision()
    {
        using var ws = Workspace();
        using (var first = ws.Session("act"))
        {
            first.Execute(Set(Good("g1"), "price", new Int32Value(7)));
            Assert.Empty(first.WriteRecovery());
            first.Abandon();
        }
        var session = ws.Session("act");
        var revision = session.Revision;

        Assert.Empty(session.RestoreRecovery());

        Assert.Equal(revision + 1, session.Revision);
        Assert.Equal(EditOrigin.Recovery, session.UndoOrigin);
        Assert.Equal(["7"], Values(session, Good("g1"), "price"));
    }

    [Fact]
    public void TheCacheStampGrowsAtEachStepAlsoInAPreview()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        Assert.Contains(session.Lists.List("base")!.Entries, e => e.Value == "Li01_01_Base");
        var stamp = session.CacheRevision;

        var batch = session.BeginBatch("Preview");
        batch.Run(EditCommand.Rename(new SectionRef(ListWorkspace.UniversePath, new SectionIdentity("Base", "Li01_01_Base")),
            "ol_preview_base"));
        Assert.True(session.CacheRevision > stamp);
        // A read inside the preview sees the step.
        Assert.Contains(session.Lists.List("base")!.Entries, e => e.Value == "ol_preview_base");
        batch.Preview();
        Assert.True(session.CacheRevision > stamp + 1);

        // A read after the preview and a later edit gives the data of the later edit, not of the preview.
        session.Execute(EditCommand.Rename(new SectionRef(ListWorkspace.UniversePath,
            new SectionIdentity("Base", "Li02_01_Base")), "ol_later_base"));
        var values = session.Lists.List("base")!.Entries.Select(e => e.Value).ToList();
        Assert.Contains("ol_later_base", values);
        Assert.Contains("Li01_01_Base", values);
        Assert.DoesNotContain("ol_preview_base", values);
    }

    [Fact]
    public void TheResultOfACommitGivesTheNewRevision()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var batch = session.BeginBatch("Batch");
        batch.Run(Set(Good("g1"), "price", new Int32Value(7)));

        var result = batch.Commit();

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(session.Revision, result.Revision);
        Assert.True(result.Revision > 0);
    }
}
