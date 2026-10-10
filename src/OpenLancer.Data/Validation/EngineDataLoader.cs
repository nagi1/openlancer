using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using LibreLancer.Data.Schema.Goods;

namespace OpenLancer.Data.Validation;

/// <summary>Librelancer could not load the data completely.</summary>
public sealed class EngineLoadException(string message) : Exception(message);

/// <param name="Db">The loaded data, or null when the load threw.</param>
/// <param name="Problems">The engine's warning and error lines, a load failure, and ship packages that it dropped.</param>
/// <param name="PackageNicknames">The ship packages that the engine must resolve from this data.</param>
public sealed record EngineLoadResult(GameItemDb? Db, IReadOnlyList<Diagnostic> Problems, IReadOnlyList<string> PackageNicknames)
{
    public bool HasErrors => Problems.Any(p => p.Severity == DiagnosticSeverity.Error);

    /// <summary>Returns the loaded data, or throws when the load threw or dropped data.</summary>
    /// <exception cref="EngineLoadException">The load did not complete.</exception>
    public GameItemDb RequireComplete()
    {
        var failures = Problems.Where(p => p.Source == EngineDataLoader.LoadSource).ToList();
        if (Db == null || failures.Count > 0)
            throw new EngineLoadException(string.Join(Environment.NewLine, failures.Select(f => f.Message)));
        return Db;
    }
}

/// <summary>
/// Loads game data with Librelancer's <see cref="GameItemDb"/>, as the game does, and checks that the load is
/// complete.
/// </summary>
/// <remarks>
/// <para>The engine throws on some broken data, for example a ship package whose hull good does not exist.
/// That becomes an error here.</para>
/// <para>Upstream <c>GameItemDb.LoadData</c> starts <c>InitGoods</c> without waiting for <c>InitShips</c>, so it can
/// drop ship packages at random. The loader compares the resolved packages with the packages that the INI data
/// defines, and loads again when packages are missing. Packages that are missing in two loads in a row are
/// reported.</para>
/// </remarks>
public static class EngineDataLoader
{
    public const string EngineSource = "engine";
    public const string LoadSource = "engine-load";
    private const int MaxAttempts = 3;

    public static EngineLoadResult Load(FileSystem fs)
    {
        var expected = ExpectedPackages(fs);
        IReadOnlyList<string>? previousMissing = null;
        for (var attempt = 1; ; attempt++)
        {
            var db = new GameItemDb(fs);
            IReadOnlyList<string> lines;
            using (var capture = EngineLogCapture.Start())
            {
                try
                {
                    db.LoadData();
                }
                catch (Exception e)
                {
                    var failed = EngineLogCapture.ToDiagnostics(capture.Stop(), EngineSource).ToList();
                    failed.Add(Diagnostic.Error(LoadSource, "Librelancer cannot load the data: " + Describe(e)));
                    return new EngineLoadResult(null, failed, expected);
                }
                lines = capture.Stop();
            }

            var missing = expected.Where(n => db.GetShipPackage(FLHash.CreateID(n)) == null).ToList();
            var settled = missing.Count == 0 || attempt == MaxAttempts ||
                          (previousMissing != null && previousMissing.SequenceEqual(missing));
            if (!settled)
            {
                previousMissing = missing;
                continue;
            }

            var problems = EngineLogCapture.ToDiagnostics(lines, EngineSource).ToList();
            if (missing.Count > 0)
            {
                problems.Add(Diagnostic.Error(LoadSource,
                    $"Librelancer did not resolve the ship packages {string.Join(", ", missing)}. Check their ship " +
                    "definitions. If the same data loads at other times, the cause is the upstream InitGoods/InitShips race."));
            }
            return new EngineLoadResult(db, problems, expected);
        }
    }

    /// <summary>
    /// The ship package goods whose hull good is a ship hull of a ship in shiparch. The engine resolves exactly these.
    /// </summary>
    private static List<string> ExpectedPackages(FileSystem fs)
    {
        var data = new FreelancerData(new FreelancerIni(fs), fs);
        using (EngineLogCapture.Start())
        {
            // The engine load reports the parse problems. This parse only lists the packages.
            data.LoadData();
        }
        var goods = data.Goods?.Goods.Where(g => g.Nickname != null).ToList() ?? [];
        var ships = (data.Ships?.Ships ?? []).Select(s => s.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hulls = goods.Where(g => g.Category == GoodCategory.ShipHull)
            .GroupBy(g => g.Nickname, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        return goods
            .Where(g => g.Category == GoodCategory.Ship && g.Hull != null &&
                        hulls.TryGetValue(g.Hull, out var hull) && hull.Ship != null && ships.Contains(hull.Ship))
            .Select(g => g.Nickname)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Describe(Exception e) => e is AggregateException aggregate
        ? string.Join("; ", aggregate.Flatten().InnerExceptions.Select(x => $"{x.GetType().Name}: {x.Message}"))
        : $"{e.GetType().Name}: {e.Message}";
}
