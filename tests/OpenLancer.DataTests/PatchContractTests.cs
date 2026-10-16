using System;
using System.Reflection;
using System.Xml.Serialization;
using LibreLancer;
using LibreLancer.Data.IO;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The engine members that OpenLancer adds with fork commits. A test reads each member by reflection, so a missing
/// member fails the test and does not stop the build of the tests.
/// </summary>
public sealed class PatchContractTests
{
    /// <summary>
    /// OL-0020: a host gives the game its file system, for example the mod overlay of a profile. The field is a
    /// config field such as <c>CustomState</c>, and the XML serializer of the config ignores it.
    /// </summary>
    [Fact]
    public void GameConfigTakesTheFileSystemOfAHost()
    {
        var field = typeof(GameConfig).GetField("CustomFileSystem");

        Assert.NotNull(field);
        Assert.True(field.IsPublic);
        Assert.False(field.IsStatic);
        Assert.Equal(typeof(Func<FileSystem>), field.FieldType);
        Assert.NotNull(field.GetCustomAttribute<XmlIgnoreAttribute>());
    }
}
