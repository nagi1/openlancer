using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The room files of the edit view, as the engine finds them (decision D135 of the M8b design): each <c>[Base]</c> of
/// <c>universe.ini</c> names the file of a base with its last <c>file</c> line (Base.cs:45-52), and each <c>[Room]</c>
/// of a base file names the file of a room with its last <c>file</c> line (Room.cs:44-50). Both values are relative to
/// the data folder of <c>freelancer.ini</c>. A system file is relative to the folder of <c>universe.ini</c> (see
/// <see cref="SystemFiles"/>), so the two rules differ.
/// </summary>
/// <remarks>
/// <para>The map reads a file from W when the session has its document open, else from the edit view. So the first use
/// opens no document. Retail has 197 <c>[Base]</c> sections and 461 <c>[Room]</c> sections, which name 446 room
/// files.</para>
/// <para>The map keeps its result until the revision of W changes. Then it compares the state of each file that it
/// read: W of an open document, or the edit view, which changes only with a save. It reads the files again only when
/// one of them changed.</para>
/// </remarks>
internal sealed class RoomFiles(EditSession session, Func<IReadOnlyList<Section>> freelancerIni)
{
    private const string FileKey = "file";

    private int revision = int.MinValue;

    // The files that the last read used, with their states: W of an open document, or the edit view.
    private (string Path, object? State)[] read = [];
    private List<string> files = [];
    private HashSet<string> set = new(VfsPath.Comparer);

    /// <summary>The room files, in the order of the bases and their rooms, without repeats.</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            Refresh();
            return files;
        }
    }

    /// <summary>True when a <c>[Room]</c> of a base file names the file.</summary>
    public bool Contains(string path)
    {
        Refresh();
        return VfsPath.Normalize(path) is { } normal && set.Contains(normal);
    }

    private void Refresh()
    {
        if (revision == session.Revision)
            return;
        revision = session.Revision;
        if (read.Length > 0 && read.All(r => ReferenceEquals(StateOf(r.Path), r.State)))
            return;
        var used = new List<(string, object?)>();
        FileSystem? view = null;
        IReadOnlyList<Section> Read(string path)
        {
            var state = StateOf(path);
            used.Add((path, state));
            if (state is IReadOnlyList<Section> working)
                return working;
            view ??= session.EditView.CreateFileSystem();
            return view.FileExists(path) ? IniDocument.Parse(path, view.ReadAllBytes(path)).Sections : [];
        }

        var result = new List<string>();
        var found = new HashSet<string>(VfsPath.Comparer);
        if (session.UniversePath is { } universe)
        {
            var ini = freelancerIni();
            foreach (var basePath in Named(Read(universe), "Base", ini))
            {
                foreach (var roomPath in Named(Read(basePath), "Room", ini))
                {
                    if (found.Add(roomPath))
                        result.Add(roomPath);
                }
            }
        }
        (read, files, set) = (used.ToArray(), result, found);
    }

    /// <summary>The state of a file for the map: W of its document when the session has it open, else the edit
    /// view.</summary>
    private object? StateOf(string path) => session.OpenedDocument(path) is { } document ? document.Working : session.EditView;

    /// <summary>The install paths that the sections of a type name with their last <c>file</c> line, relative to the
    /// data folder, in order, without repeats.</summary>
    private static IEnumerable<string> Named(IReadOnlyList<Section> sections, string type, IReadOnlyList<Section> ini)
    {
        var seen = new HashSet<string>(VfsPath.Comparer);
        foreach (var section in sections.Where(s => s.Name.Equals(type, StringComparison.OrdinalIgnoreCase)))
        {
            var file = section.LastOrDefault(e => e.Name.Equals(FileKey, StringComparison.OrdinalIgnoreCase) && e.Count > 0)?
                [0].ToString();
            if (file != null && DataPaths.Resolve(ini, file) is { } path && seen.Add(path))
                yield return path;
        }
    }
}
