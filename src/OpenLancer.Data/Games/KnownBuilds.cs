using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Games;

/// <summary>One file of a build manifest.</summary>
/// <param name="Path">The relative path, lowercase, with '/'.</param>
/// <param name="Sha256">The lowercase SHA-256.</param>
internal readonly record struct ManifestRow(string Path, long Size, string Sha256);

/// <summary>
/// The game builds that the detector knows, with the file list of each one. A build manifest is a file
/// <c>data-manifest\&lt;build id&gt;.tsv</c> with one line for each file:
/// <c>&lt;path&gt;\t&lt;size&gt;\t&lt;sha256&gt;</c>, as <c>tools\verify_base.py</c> and <see cref="DataDigest"/> write it.
/// The manifests hold paths, sizes and hashes only, never game content.
/// </summary>
public sealed class KnownBuilds
{
    /// <summary>The prefix of the logical names of the embedded manifests.</summary>
    private const string ResourcePrefix = "OpenLancer.Data.Games.";

    private readonly Dictionary<string, Lazy<IReadOnlyDictionary<string, ManifestRow>>> manifests;

    private KnownBuilds(Dictionary<string, Func<string>> sources)
    {
        manifests = sources.ToDictionary(s => s.Key,
            s => new Lazy<IReadOnlyDictionary<string, ManifestRow>>(() => Parse(s.Key, s.Value())),
            StringComparer.Ordinal);
    }

    /// <summary>The manifests of the <c>data-manifest</c> folder of the repo, which the build embeds in this
    /// assembly.</summary>
    public static KnownBuilds Embedded { get; } = FromAssembly();

    /// <summary>Builds from manifest text, for example the fake builds of a test.</summary>
    /// <param name="manifests">The build id to the text of its manifest.</param>
    public static KnownBuilds FromText(IReadOnlyDictionary<string, string> manifests) =>
        new(manifests.ToDictionary(m => m.Key, m => (Func<string>)(() => m.Value)));

    /// <summary>The ids of the builds, in ordinal order.</summary>
    public IReadOnlyList<string> Ids => manifests.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The build whose files a build changes, or null. Patch 1.1 replaces code files only. A compare of an English
    /// install with patch 1.1 and the file list of 1.0 showed this (milestone M11). So the data files of 1.1 come from
    /// the manifest of 1.0. HD Edition has no parent, because its manifest lists each file.
    /// </summary>
    internal static string? ParentOf(string build) => build == VanillaOnePointOne ? VanillaOnePointZero : null;

    internal const string VanillaOnePointZero = "vanilla-1.0";
    internal const string VanillaOnePointOne = "vanilla-1.1";

    /// <summary>True when the detector has the manifest of the build.</summary>
    internal bool Has(string build) => manifests.ContainsKey(build);

    /// <summary>The own rows of a build, by path. The manifest parses at its first use.</summary>
    /// <exception cref="InvalidDataException">A line of the manifest is not valid.</exception>
    internal IReadOnlyDictionary<string, ManifestRow> Rows(string build) => manifests[build].Value;

    /// <summary>
    /// The files that a game of the build has: the rows of its parent, with the rows of the build on top. Null when the
    /// detector has no manifest of the build or of its parent.
    /// </summary>
    /// <returns>The rows, and the build whose files they are mostly: the build itself, or its parent when the build has
    /// no manifest.</returns>
    internal (IReadOnlyDictionary<string, ManifestRow> Rows, string Reference, bool FromParent)? Reference(string build)
    {
        var parent = ParentOf(build);
        var hasParent = parent != null && Has(parent);
        if (!Has(build))
            return hasParent ? (Rows(parent!), parent!, true) : null;
        if (!hasParent)
            return (Rows(build), build, false);
        var rows = new Dictionary<string, ManifestRow>(Rows(parent!), StringComparer.Ordinal);
        foreach (var (path, row) in Rows(build))
            rows[path] = row;
        return (rows, build, true);
    }

    /// <exception cref="InvalidDataException">A line is not valid. The message names the line.</exception>
    internal static IReadOnlyDictionary<string, ManifestRow> Parse(string build, string text)
    {
        var rows = new Dictionary<string, ManifestRow>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var columns = line.Split('\t');
            if (columns.Length != 3)
                throw new InvalidDataException($"{build}.tsv line {i + 1}: the line must have 3 columns, path, size and " +
                                               $"SHA-256, with a tab between them. It has {columns.Length}.");
            if (!long.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw new InvalidDataException($"{build}.tsv line {i + 1}: the size '{columns[1]}' is not a number.");
            if (columns[2].Length != 64 || !columns[2].All(Uri.IsHexDigit))
                throw new InvalidDataException($"{build}.tsv line {i + 1}: '{columns[2]}' is not a SHA-256.");
            var path = DataDigest.ManifestPath(columns[0]);
            if (!rows.TryAdd(path, new ManifestRow(path, size, columns[2].ToLowerInvariant())))
                throw new InvalidDataException($"{build}.tsv line {i + 1}: the path {path} is in the file two times.");
        }
        return rows;
    }

    private static KnownBuilds FromAssembly()
    {
        var assembly = typeof(KnownBuilds).Assembly;
        var sources = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !name.EndsWith(".tsv", StringComparison.Ordinal))
                continue;
            var build = name[ResourcePrefix.Length..^".tsv".Length];
            sources[build] = () => Read(assembly, name);
        }
        return new KnownBuilds(sources);
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
