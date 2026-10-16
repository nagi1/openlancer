using System;
using System.IO;
using System.Runtime.InteropServices;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The export check of the gizmo. Fork commit OL-0019 adds <c>igGuizmoEnable</c> to <c>cimgui</c>. The editor keeps the
/// gizmo off with a <c>cimgui.dll</c> from before the patch, so that the end of a drag cannot stop the editor.
/// </summary>
public sealed class TransformGizmoTests
{
    /// <summary>The test folder gets the natives of the editor from PublishAssets, in the x64 folder.</summary>
    private static string Native(string name) => Path.Combine(AppContext.BaseDirectory, "x64", name);

    [Fact]
    public void TheExportCheckFindsTheFunctionOfPatch0019InTheBuiltCimgui()
    {
        var cimgui = NativeLibrary.Load(Native("cimgui.dll"));
        Assert.True(TransformGizmo.ExportsEnable(cimgui));
    }

    [Fact]
    public void TheExportCheckRefusesALibraryWithoutTheFunction()
    {
        var other = NativeLibrary.Load("kernel32.dll");
        Assert.False(TransformGizmo.ExportsEnable(other));
        Assert.Contains("BuildNatives", TransformGizmo.NativeMessage);
    }
}
