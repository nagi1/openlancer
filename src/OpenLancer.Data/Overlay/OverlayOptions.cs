namespace OpenLancer.Data.Overlay;

/// <summary>How the merge gives the index of the generated text DLL in the <c>[Resources]</c> list.</summary>
public enum DllIndexRule
{
    /// <summary>The rule of Librelancer: <c>resources.dll</c> counts when it exists, and each <c>DLL</c> line of the last
    /// <c>[Resources]</c> section counts when its file exists. OpenLancer runs the mods with this rule.</summary>
    Librelancer,

    /// <summary>The rule of the retail game and its server: <c>resources.dll</c> is 0, and each <c>DLL</c> line of each
    /// <c>[Resources]</c> section takes the next index, also when its file does not exist. The new line goes after the
    /// last <c>DLL</c> line of the last <c>[Resources]</c> section.</summary>
    Retail,
}

/// <summary>
/// The options of a merge (milestone M11). The default gives the merge of Play in OpenLancer and of the editor. The
/// export and Play in the game use the retail rules.
/// </summary>
public sealed record OverlayOptions
{
    /// <summary>The options of Play in OpenLancer and of the editor.</summary>
    public static OverlayOptions Default { get; } = new();

    /// <summary>The file name of the generated text DLL in the <c>EXE</c> folder.</summary>
    public string StringsDllName { get; init; } = ModOverlay.StringsDllName;

    public DllIndexRule DllIndexRule { get; init; } = DllIndexRule.Librelancer;

    /// <summary>True to keep the merged document of each merged INI file.</summary>
    public bool KeepDocuments { get; init; }

    /// <summary>True to keep each text placeholder (<c>$str:</c> and <c>$ifc:</c>) as a string, with no error. The
    /// FLMM package writes each placeholder as a text macro of FLMM.</summary>
    internal bool KeepPlaceholders { get; init; }

    /// <summary>The install path of the generated text DLL.</summary>
    public string StringsDllPath => @"EXE\" + StringsDllName;
}
