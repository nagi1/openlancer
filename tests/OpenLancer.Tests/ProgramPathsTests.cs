using System;
using System.IO;
using OpenLancer.Editor;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The programs that the editor starts are next to it. The test project references the editor, so the build puts the
/// programs of the editor next to the tests too.
/// </summary>
public sealed class ProgramPathsTests
{
    [Fact]
    public void OltoolAndThePlayProgramAreNextToTheEditor()
    {
        foreach (var program in (string[])[ProgramPaths.Oltool, ProgramPaths.Play])
        {
            Assert.Equal(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(program));
            Assert.True(File.Exists(program), $"{program} is not next to the editor.");
        }
    }
}
