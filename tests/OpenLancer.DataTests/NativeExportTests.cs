using System;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Local patch 0019 changes native code: <c>cimgui</c> exports <c>igGuizmoEnable</c>, the only reset of a stuck drag of
/// ImGuizmo. The test folder gets the natives from PublishAssets, so this test fails when <c>bin\natives\x64</c> has a
/// <c>cimgui.dll</c> from before the patch (run <c>build.ps1 BuildNatives</c>, see docs/UPSTREAM.md).
/// </summary>
public sealed class NativeExportTests
{
    [Fact]
    public void CimguiInTheTestFolderExportsTheResetOfTheGizmo()
    {
        var cimgui = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "x64", "cimgui.dll"));
        Assert.True(NativeLibrary.TryGetExport(cimgui, "igGuizmoEnable", out _));
        // The exports that the editor already used are still there.
        foreach (var name in new[] { "igGuizmoIsUsing", "igGuizmoIsOver", "igGuizmoManipulate", "igGuizmoSetRect" })
        {
            Assert.True(NativeLibrary.TryGetExport(cimgui, name, out _), name);
        }
    }
}
