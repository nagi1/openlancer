using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The nebula files and the asteroid field files of the edit view, and their owners (milestone M3): each
/// <c>[Nebula]</c> and <c>[Asteroids]</c> section of a system file names its file with its last <c>file</c> line,
/// relative to the data folder. The system files whose sections name a file are its owners. A hole line of a file names a
/// zone of an owner.
/// </summary>
/// <remarks>
/// <para>The map reads a system file from W when the session has its document open, else from the edit view. So the
/// first use opens no document. It keeps the result of each file until the state of the file changes: W of an open
/// document, or the edit view, which changes only with a save. So a change of a <c>file</c> line changes the
/// files.</para>
/// <para>Retail has 259 such sections in the 53 system files. They name 208 files, and one file has owners in two
/// systems.</para>
/// </remarks>
internal sealed class FieldFiles(EditSession session, Func<IReadOnlyList<Section>> freelancerIni)
{
    private const string FileKey = "file";

    /// <summary>The section types of the system files that name a file.</summary>
    private static readonly string[] Types = ["Nebula", "Asteroids"];

    private readonly Dictionary<string, (object? State, List<(string Type, string Path)> Files)> perFile =
        new(VfsPath.Comparer);

    private int revision = int.MinValue;
    private (string Path, object? State)[] read = [];
    private Result result = Result.Empty;

    /// <summary>An object that changes when the files or their owners change. A cache that depends on the owners
    /// compares it by reference.</summary>
    public object State
    {
        get
        {
            Refresh();
            return result;
        }
    }

    /// <summary>The files that the sections of a type name, <c>Nebula</c> or <c>Asteroids</c>, in the order of the
    /// system files and their sections, without repeats.</summary>
    public IReadOnlyList<string> Files(string type)
    {
        Refresh();
        return result.Files.GetValueOrDefault(type) ?? [];
    }

    /// <summary>True when a section of the type names the file.</summary>
    public bool Contains(string type, string path)
    {
        Refresh();
        return VfsPath.Normalize(path) is { } normal && result.Sets.TryGetValue(type, out var set) && set.Contains(normal);
    }

    /// <summary>The system files whose <c>[Nebula]</c> or <c>[Asteroids]</c> sections name the file, in the order of the
    /// system files, without repeats. Empty for another file.</summary>
    public IReadOnlyList<string> OwnersOf(string path)
    {
        Refresh();
        return VfsPath.Normalize(path) is { } normal ? result.Owners.GetValueOrDefault(normal) ?? [] : [];
    }

    private void Refresh()
    {
        if (revision == session.Revision)
            return;
        revision = session.Revision;
        var systems = session.Systems.Files;
        if (read.Length == systems.Count &&
            read.Zip(systems).All(p => VfsPath.Comparer.Equals(p.First.Path, p.Second) &&
                                       ReferenceEquals(StateOf(p.Second), p.First.State)))
            return;
        FileSystem? view = null;
        var ini = freelancerIni();
        var used = new List<(string, object?)>();
        var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, List<string>>(VfsPath.Comparer);
        foreach (var system in systems)
        {
            var state = StateOf(system);
            used.Add((system, state));
            if (!perFile.TryGetValue(system, out var known) || !ReferenceEquals(known.State, state))
            {
                IReadOnlyList<Section> sections;
                if (state is IReadOnlyList<Section> working)
                    sections = working;
                else
                {
                    view ??= session.EditView.CreateFileSystem();
                    sections = view.FileExists(system) ? IniDocument.Parse(system, view.ReadAllBytes(system)).Sections : [];
                }
                known = (state, Named(sections, ini).ToList());
                perFile[system] = known;
            }
            foreach (var (type, path) in known.Files)
            {
                if (!sets.TryGetValue(type, out var set))
                {
                    sets[type] = set = new HashSet<string>(VfsPath.Comparer);
                    files[type] = [];
                }
                if (set.Add(path))
                    files[type].Add(path);
                if (!owners.TryGetValue(path, out var of))
                    owners[path] = of = [];
                if (!of.Contains(system, VfsPath.Comparer))
                    of.Add(system);
            }
        }
        read = used.ToArray();
        var next = new Result(files.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase),
            sets, owners.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, VfsPath.Comparer));
        // An edit of another line of a system file keeps the result, so the caches of the owners stay.
        if (!next.SameAs(result))
            result = next;
    }

    /// <summary>The state of a system file for the map: W of its document when the session has it open, else the edit
    /// view.</summary>
    private object? StateOf(string path) => session.OpenedDocument(path) is { } document ? document.Working : session.EditView;

    /// <summary>The files that the <c>[Nebula]</c> and <c>[Asteroids]</c> sections of a system file name, in order.</summary>
    private static IEnumerable<(string Type, string Path)> Named(IReadOnlyList<Section> sections, IReadOnlyList<Section> ini)
    {
        foreach (var section in sections)
        {
            if (Types.FirstOrDefault(t => t.Equals(section.Name, StringComparison.OrdinalIgnoreCase)) is not { } type)
                continue;
            var file = section.LastOrDefault(e => e.Name.Equals(FileKey, StringComparison.OrdinalIgnoreCase) && e.Count > 0)?
                [0].ToString();
            if (file != null && DataPaths.Resolve(ini, file.Trim()) is { } path)
                yield return (type, path);
        }
    }

    private sealed record Result(
        IReadOnlyDictionary<string, IReadOnlyList<string>> Files,
        IReadOnlyDictionary<string, HashSet<string>> Sets,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Owners)
    {
        public static readonly Result Empty = new(new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, HashSet<string>>(), new Dictionary<string, IReadOnlyList<string>>());

        /// <summary>True when both results have the same files in the same order, with the same owners.</summary>
        public bool SameAs(Result other) => Same(Files, other.Files) && Same(Owners, other.Owners);

        private static bool Same(IReadOnlyDictionary<string, IReadOnlyList<string>> a,
            IReadOnlyDictionary<string, IReadOnlyList<string>> b) =>
            a.Count == b.Count &&
            a.All(p => b.TryGetValue(p.Key, out var other) && other.SequenceEqual(p.Value, VfsPath.Comparer));
    }
}
