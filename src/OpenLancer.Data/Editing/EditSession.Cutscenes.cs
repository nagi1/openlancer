using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private readonly Dictionary<string, ThnDocument> cutscenes = new(VfsPath.Comparer);

    /// <summary>Why a cutscene path that the session tried to open has no document.</summary>
    private readonly Dictionary<string, string> cutsceneProblems = new(VfsPath.Comparer);

    /// <summary>The files of the lower layer, made at the first use.</summary>
    private FileSystem? lowerFiles;

    /// <summary>The install paths of the cutscene scripts that the session opened, in normal form.</summary>
    public IReadOnlyCollection<string> OpenCutscenes => cutscenes.Keys;

    /// <summary>
    /// W of a cutscene script (<c>.thn</c>): the cutscene of the lower layer with the patch of the active mod and the
    /// edits, or the whole file of the active mod with the edits. The session opens the cutscene at the first use. The
    /// edit view stays as the files on disk give it.
    /// </summary>
    /// <returns>W, or null when the edit view has no such cutscene or the editor cannot read it.
    /// <see cref="CutsceneReadOnlyReason(string)"/> then tells why.</returns>
    public ThnScriptModel? Cutscene(string path) => CutsceneDocument(path)?.Working;

    /// <summary>B: the cutscene as the lower layer gives it, or null when the lower layer does not have it or the
    /// session cannot open the cutscene.</summary>
    public ThnScriptModel? LowerCutscene(string path) => CutsceneDocument(path)?.Base;

    /// <summary>Where W of a cutscene comes from, or null when the session cannot open the cutscene.</summary>
    public ThnFileKind? CutsceneKind(string path) => CutsceneDocument(path)?.Kind;

    /// <summary>
    /// Why the editor cannot change a cutscene, or null: the session is read-only, the edit view has no such cutscene,
    /// the editor cannot read it, or the file of the active mod for it has an error.
    /// </summary>
    public string? CutsceneReadOnlyReason(string path) =>
        ReadOnlyReason ?? (TryCutscene(path, out var document) ? document.ReadOnlyReason : CutsceneProblem(path));

    /// <summary>Why the editor cannot change an element of a cutscene with an edit other than a delete, or null. A
    /// later entity with the same name makes an entity read-only, because the engine does not use it.</summary>
    public string? CutsceneReadOnlyReason(ThnRef element) =>
        CutsceneReadOnlyReason(element.Path) ?? CutsceneDocument(element.Path)!.ReadOnlyReasonOf(element.Kind, element.Id);

    /// <summary>
    /// Where the edit view gets a cutscene from, without opening it: the kind of the file of the active mod, as
    /// <see cref="CutsceneKind"/> gives it after an open, and true when a lower mod adds or changes the cutscene.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not an install path.</exception>
    public ThnFileSource CutsceneSource(string path)
    {
        var normal = VfsPath.Normalize(path) ?? throw new ArgumentException($"{path} is not an install path.", nameof(path));
        var lowerMods = lower.Any(m => m.Files.ContainsKey(normal) || m.Patches.ContainsKey(normal));
        ThnFileKind kind;
        if (ActiveMod != null && ActiveMod.Files.ContainsKey(normal))
            kind = LowerFiles.FileExists(normal) ? ThnFileKind.WholeFileHere : ThnFileKind.NewHere;
        else
            kind = ActiveMod != null && ActiveMod.Patches.ContainsKey(normal) ? ThnFileKind.PatchedHere : ThnFileKind.Lower;
        return new ThnFileSource(kind, lowerMods);
    }

    /// <summary>True when the session opened a cutscene and its W differs from the state of the open or of the last
    /// save, so a save would write it.</summary>
    public bool HasUnsavedCutsceneEdits(string path) =>
        VfsPath.Normalize(path) is { } normal && cutscenes.TryGetValue(normal, out var document) &&
        document.State != document.Initial;

    /// <summary>The document of a cutscene, or null when the session cannot open it.</summary>
    internal ThnDocument? CutsceneDocument(string path) => TryCutscene(path, out var document) ? document : null;

    /// <summary>The files of the lower layer, made at the first use.</summary>
    private FileSystem LowerFiles => lowerFiles ??= lowerView.CreateFileSystem();

    /// <summary>Why the session cannot open a cutscene.</summary>
    internal string CutsceneProblem(string path) =>
        (VfsPath.Normalize(path) is { } normal ? cutsceneProblems.GetValueOrDefault(normal) : null) ??
        $"The edit view has no cutscene {path}.";

    /// <summary>The document of a cutscene. Opens it at the first use. A path that does not open keeps its problem, so
    /// the session reads it once.</summary>
    private bool TryCutscene(string path, [NotNullWhen(true)] out ThnDocument? document)
    {
        document = null;
        var normal = VfsPath.Normalize(path);
        // An install path is relative to the game folder, so it has no drive.
        if (normal == null || normal.Contains(':') || !normal.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
            return false;
        if (cutscenes.TryGetValue(normal, out document))
            return true;
        if (cutsceneProblems.ContainsKey(normal))
            return false;
        // The fingerprint first: a change after it shows at the save.
        inputs.Remember(normal, lower);
        byte[]? bytes;
        try
        {
            bytes = LowerFiles.FileExists(normal) ? LowerFiles.ReadAllBytes(normal) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            cutsceneProblems[normal] = e.Message;
            return false;
        }
        document = ThnDocument.Open(normal, bytes, ActiveMod, thn, out var problem);
        if (document == null)
        {
            cutsceneProblems[normal] = problem!;
            return false;
        }
        cutscenes[normal] = document;
        return true;
    }
}
