using System;
using System.IO;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The copy of a lower mod in <see cref="RetailWorkspace"/>. The retail tests copy the repo sample mod, and the user's
/// editor can have that mod open while the tests run.
/// </summary>
public sealed class RetailWorkspaceTests
{
    [Fact]
    public void TheCopyOfAModLeavesOutTheStateOfAnEditorThatHasItOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var state = Path.Combine(source, SaveTransaction.FolderName);
        Directory.CreateDirectory(Path.Combine(source, "patches"));
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(source, "mod.json"), "{}");
        File.WriteAllText(Path.Combine(source, "patches", "goods.ini"), "[Good]\nnickname = x\n");
        File.WriteAllText(Path.Combine(state, "recovery.json"), "{}");
        try
        {
            // An editor holds its lock file open and shares it with no other handle (ModLock).
            using (new FileStream(Path.Combine(state, "lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var copy = RetailWorkspace.Copy(source, Path.Combine(root, "copy"));

                Assert.True(File.Exists(Path.Combine(copy, "mod.json")));
                Assert.True(File.Exists(Path.Combine(copy, "patches", "goods.ini")));
                Assert.False(Directory.Exists(Path.Combine(copy, SaveTransaction.FolderName)));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
