using System;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The rule of install paths (decision D763 of the MCP1 design, MCP1 section 13.1 item 12): a request and the session
/// refuse each path that can leave the edit view.
/// </summary>
public sealed class InstallPathsTests
{
    [Theory]
    [InlineData(@"C:\x.ini")]
    [InlineData(@"C:x.ini")]
    [InlineData(@"\x.ini")]
    [InlineData("/x.ini")]
    [InlineData(@"\\server\share\x.ini")]
    [InlineData(@"DATA\x.ini:stream")]
    [InlineData(@"..\x.ini")]
    [InlineData(@"DATA\..\..\x.ini")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("DATA\\x\u0001.ini")]
    [InlineData(@".\.")]
    // Windows removes a space or a dot at the end of a part, so these parts name the file of another path.
    [InlineData(@"DATA.\x.ini")]
    [InlineData(@"DATA\x.ini.")]
    [InlineData(@"DATA\x.ini ")]
    [InlineData(@"DATA\.. \x.ini")]
    [InlineData(@"DATA\...\x.ini")]
    [InlineData(@" \x.ini")]
    [InlineData(@"DATA\ x.ini")]
    [InlineData(@"DATA\.x\y.ini")]
    // Windows opens a device for these names, also with an extension, so a write would write to the device.
    [InlineData("NUL")]
    [InlineData(@"DATA\con.ini")]
    [InlineData(@"DATA\Aux\x.ini")]
    [InlineData(@"DATA\prn.txt.ini")]
    [InlineData(@"DATA\COM1.ini")]
    [InlineData(@"DATA\lpt9")]
    [InlineData(@"DATA\LPT0.thn")]
    [InlineData("DATA\\com\u00b9.ini")]
    [InlineData(@"DATA\CONIN$")]
    [InlineData(@"DATA\conout$.ini")]
    [InlineData(@"DATA\nul .ini")]
    public void TheParserRefusesEachPathThatIsNotAnInstallPath(string path)
    {
        var error = Assert.Throws<EditException>(() => InstallPaths.Parse(path)).Error;

        Assert.Equal(EditErrorKind.InvalidArgument, error.Kind);
        Assert.Equal(InstallPaths.NotInstallPath, error.Detail);
        Assert.Null(InstallPaths.TryParse(path));
    }

    [Theory]
    [InlineData(@"DATA\SHIPS\loadouts.ini", @"DATA\SHIPS\loadouts.ini")]
    [InlineData("data/ships/./loadouts.ini", @"data\ships\loadouts.ini")]
    [InlineData(@"EXE\..\DATA\SHIPS\loadouts.ini", @"DATA\SHIPS\loadouts.ini")]
    [InlineData(@"DATA\console.ini", @"DATA\console.ini")]
    [InlineData(@"DATA\com10.ini", @"DATA\com10.ini")]
    [InlineData(@"DATA\nulls\auxiliary.ini", @"DATA\nulls\auxiliary.ini")]
    [InlineData(@"DATA\lpt.ini", @"DATA\lpt.ini")]
    public void TheParserGivesTheNormalForm(string path, string normal)
    {
        Assert.Equal(normal, InstallPaths.Parse(path));
        Assert.Null(InstallPaths.Problem(path));
    }

    [Fact]
    public void TheMessageNamesThePathAndTheForm()
    {
        Assert.Equal(@"The path 'C:\data\x.ini' is not an install path. An install path cannot hold a drive or a ':'. " +
                     @"Use a path such as DATA\SHIPS\loadouts.ini.", InstallPaths.Problem(@"C:\data\x.ini"));
        Assert.Equal(@"The path 'graphs\DATA.\x.ini' is not an install path. The part 'DATA.' starts or ends with a " +
                     @"space or a dot. Use a path such as DATA\SHIPS\loadouts.ini.",
            InstallPaths.Problem(@"graphs\DATA.\x.ini"));
    }

    [Fact]
    public void TheMessageNamesTheDevice()
    {
        Assert.Equal(@"The path 'DATA\nul.ini' is not an install path. The part 'nul.ini' is the name of a Windows device. " +
                     @"Use a path such as DATA\SHIPS\loadouts.ini.", InstallPaths.Problem(@"DATA\nul.ini"));
    }

    [Fact]
    public void AnEditorFileRefusesAPartThatWindowsChanges()
    {
        Assert.Null(ModFileDocument.Normalize(@"graphs\DATA.\x.ini"));
        Assert.Equal(@"graphs\DATA\x.ini", ModFileDocument.Normalize(@"graphs\DATA\x.ini"));
    }

    [Fact]
    public void TheSessionRefusesARootedPath()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var rooted = System.IO.Path.Combine(ws.Base, GoodsPath);

        Assert.Null(session.OpenDocument(rooted));
        Assert.Null(session.OpenDocument(@"\" + GoodsPath));
        Assert.Null(session.Get(new SectionRef(rooted, new SectionIdentity("Good", "g1"))));
        Assert.Empty(session.Sections(rooted));
        Assert.Throws<ArgumentException>(() =>
            session.Execute(Set(new SectionRef("/" + GoodsPath, new SectionIdentity("Good", "g1")), "price",
                new LibreLancer.Data.Ini.Int32Value(1))));
        Assert.Null(session.Cutscene(@"\DATA\x.thn"));
        Assert.Throws<ArgumentException>(() => session.CutsceneSource(@"C:\DATA\x.thn"));
        // The install path itself still opens.
        Assert.NotNull(session.OpenDocument(GoodsPath));
    }
}
