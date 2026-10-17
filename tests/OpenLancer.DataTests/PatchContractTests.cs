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

    /// <summary>
    /// OL-0027: when the user closes the window, the game asks the host first, and false keeps the window open. The
    /// editor overrides the method, so that it can ask about unsaved edits (see <c>ClosePrompt</c>).
    /// </summary>
    [Fact]
    public void GameAsksTheHostBeforeTheWindowCloses()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var method = typeof(Game).GetMethod("OnCloseRequested", instance, Type.EmptyTypes);
        var editor = typeof(OpenLancer.Editor.EditorWindow).GetMethod("OnCloseRequested", instance, Type.EmptyTypes);

        Assert.NotNull(method);
        Assert.True(method.IsFamily);
        Assert.True(method.IsVirtual);
        Assert.Equal(typeof(bool), method.ReturnType);
        Assert.NotNull(editor);
        Assert.Equal(typeof(OpenLancer.Editor.EditorWindow), editor.DeclaringType);
        Assert.Equal(typeof(Game), editor.GetBaseDefinition().DeclaringType);
    }
}
