using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The sections of one W state of one file, with their identities and their indices by section type (decision D121).
/// A W state cannot change, so a table stays valid while W of its file has the same section list.
/// </summary>
internal sealed class FileTable
{
    private readonly Dictionary<string, List<int>> byType = new(StringComparer.OrdinalIgnoreCase);

    public FileTable(EditDocument document)
        : this(document.Path, document.Working, Enumerable.Range(0, document.Working.Count).Select(document.IdentityAt).ToArray())
    {
        Document = document;
    }

    /// <summary>A table of sections that no document holds, for example a whole file of the active mod as the edit
    /// view gives it.</summary>
    /// <param name="path">The install path, in <see cref="VfsPath.Normalize"/> form.</param>
    public FileTable(string path, IReadOnlyList<Section> sections) : this(path, sections, SectionIdentity.Identify(sections))
    {
    }

    private FileTable(string path, IReadOnlyList<Section> sections, SectionIdentity[] ids)
    {
        Path = path;
        Working = sections;
        Ids = ids;
        for (var i = 0; i < Working.Count; i++)
        {
            if (!byType.TryGetValue(Working[i].Name, out var indices))
                byType[Working[i].Name] = indices = [];
            indices.Add(i);
        }
    }

    /// <summary>The install path, in <see cref="VfsPath.Normalize"/> form.</summary>
    public string Path { get; }

    /// <summary>The document whose W state the table holds, or null for sections that no document holds.</summary>
    public EditDocument? Document { get; }

    /// <summary>The sections of the W state.</summary>
    public IReadOnlyList<Section> Working { get; }

    /// <summary>The identity of each section.</summary>
    public SectionIdentity[] Ids { get; }

    /// <summary>The indices of the sections of a type, in file order. The type compares without letter case.</summary>
    public IReadOnlyList<int> OfType(string type) => byType.TryGetValue(type, out var indices) ? indices : [];

    /// <summary>The reference of the section at an index.</summary>
    public SectionRef Ref(int index) => new(Path, Ids[index]);
}

/// <summary>
/// The shared index of the sections of the edit view: one <see cref="FileTable"/> for each file that a reader asked
/// for. A table is built again only when the W reference of its file changes, so an edit of one file builds only the
/// table of that file again. <see cref="ItemIndex"/> and <see cref="Lists.DataLists"/> read their sections from here.
/// </summary>
/// <param name="open">Gives the document of an install path, or null when the edit view has no such INI file.</param>
internal sealed class SectionTable(Func<string, EditDocument?> open)
{
    private readonly Dictionary<string, FileTable> tables = new(VfsPath.Comparer);

    /// <summary>The number of tables that the index built, for the tests.</summary>
    public int Builds { get; private set; }

    /// <summary>The table of the W state of a file now, or null when the edit view has no such INI file.</summary>
    public FileTable? File(string path)
    {
        if (open(path) is not { } document)
        {
            tables.Remove(path);
            return null;
        }
        if (tables.TryGetValue(document.Path, out var table) && ReferenceEquals(table.Working, document.Working) &&
            ReferenceEquals(table.Document, document))
            return table;
        table = new FileTable(document);
        tables[document.Path] = table;
        Builds++;
        return table;
    }
}
