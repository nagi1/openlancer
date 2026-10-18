using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.PlayCopies;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The writes of a play copy through <see cref="HardLinks"/> never change the file of another name (section 8.2 of the M11
/// design). A replace and a delete change one name, also for a read-only file. A failed rename never falls back to a
/// copy. The files are hand-written in a temporary folder on the volume of the test.
/// </summary>
public sealed class HardLinkTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));

    public HardLinkTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(folder))
            HardLinks.Instance.Delete(file);
        HardLinks.Instance.Delete(folder);
    }

    /// <summary>A file of the "game" with the text, and its stamp through a handle.</summary>
    private string Source(string text, bool readOnly = false)
    {
        var path = Path.Combine(folder, "source.cmp");
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, new DateTime(2003, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        if (readOnly)
            File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    /// <summary>The stamp of a file without its link count, which a link and a delete change.</summary>
    private static string Stamp(string path)
    {
        var stamp = HardLinks.Instance.Stamp(path);
        return $"{stamp.Volume} {stamp.Index} {stamp.Size} {stamp.Attributes} {stamp.CreationTime.Ticks} " +
               $"{stamp.WriteTime.Ticks} {File.ReadAllText(path)}";
    }

    [Fact]
    public void ReplaceKeepsTheSourceBytesAttributesAndTimes()
    {
        var source = Source("game bytes");
        var link = Path.Combine(folder, "link.cmp");
        HardLinks.Instance.Link(source, link);
        Assert.True(PlayGame.SameFile(source, link));
        var before = Stamp(source);

        HardLinks.Instance.WriteNew(link, Encoding.ASCII.GetBytes("mod bytes"));

        Assert.Equal(before, Stamp(source));
        Assert.Equal("mod bytes", File.ReadAllText(link));
        Assert.False(PlayGame.SameFile(source, link));
        Assert.Equal(1u, HardLinks.Instance.Stamp(source).Links);
    }

    [Fact]
    public void ReplaceOfAReadOnlyLinkKeepsTheReadOnlySource()
    {
        var source = Source("game bytes", readOnly: true);
        var link = Path.Combine(folder, "link.cmp");
        HardLinks.Instance.Link(source, link);
        Assert.True(new FileInfo(link).IsReadOnly);
        var before = Stamp(source);

        HardLinks.Instance.WriteNew(link, Encoding.ASCII.GetBytes("mod bytes"));

        Assert.Equal(before, Stamp(source));
        Assert.True(new FileInfo(source).IsReadOnly);
        Assert.False(new FileInfo(link).IsReadOnly);
        Assert.Equal("mod bytes", File.ReadAllText(link));
    }

    [Fact]
    public void DeleteOfAReadOnlyLinkKeepsTheSourceAttribute()
    {
        var source = Source("game bytes", readOnly: true);
        var link = Path.Combine(folder, "link.cmp");
        HardLinks.Instance.Link(source, link);
        var before = Stamp(source);

        HardLinks.Instance.Delete(link);

        Assert.False(File.Exists(link));
        Assert.Equal(before, Stamp(source));
        Assert.True(new FileInfo(source).IsReadOnly);
        Assert.Equal(1u, HardLinks.Instance.Stamp(source).Links);
    }

    [Fact]
    public void LinkIdentityComesFromAHandle()
    {
        var source = Source("game bytes");
        var link = Path.Combine(folder, "link.cmp");
        HardLinks.Instance.Link(source, link);
        var linked = HardLinks.Instance.Stamp(link);

        // A change through the other name changes the file record. The handle of the link name reads the record.
        using (var stream = new FileStream(source, FileMode.Append, FileAccess.Write))
            stream.Write("and more"u8);

        var changed = HardLinks.Instance.Stamp(link);
        Assert.True(changed.SameFile(linked));
        Assert.True(changed.SameFile(HardLinks.Instance.Stamp(source)));
        Assert.Equal(new FileInfo(source).Length, changed.Size);
        Assert.Equal(2u, changed.Links);
        Assert.Null(HardLinks.Instance.TryStamp(Path.Combine(folder, "missing.cmp")));
    }

    [Fact]
    public void ReplaceHasNoCopyFallback()
    {
        var source = Source("game bytes", readOnly: true);
        var link = Path.Combine(folder, "link.cmp");
        HardLinks.Instance.Link(source, link);
        var before = Stamp(source);
        var failing = new FailingRename();

        Assert.Throws<IOException>(() => failing.WriteNew(link, Encoding.ASCII.GetBytes("mod bytes")));

        Assert.Equal(1, failing.Renames);
        Assert.Equal(before, Stamp(source));
        Assert.True(PlayGame.SameFile(source, link));
        // The temporary file is gone, and the folder holds only the two names of the game file.
        Assert.Equal(["link.cmp", "source.cmp"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void AReadOnlyProbeFileGetsItsAttributeAtItsMake()
    {
        var path = Path.Combine(folder, "probe");

        HardLinks.Instance.CreateReadOnly(path);

        Assert.True(new FileInfo(path).IsReadOnly);
        Assert.Throws<IOException>(() => HardLinks.Instance.CreateReadOnly(path));
        HardLinks.Instance.Delete(path);
        Assert.False(File.Exists(path));
    }

    /// <summary>A rename that the system refuses, as on a volume that does not allow it.</summary>
    private sealed class FailingRename : HardLinks
    {
        public int Renames { get; private set; }

        protected override void Rename(string temp, string target)
        {
            Renames++;
            throw new IOException("The test refuses the rename.");
        }
    }
}
