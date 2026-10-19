using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Resources;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the trade lane role of the Scene index (fix S2). The game does not read <c>lane_id</c>, but each
/// zone of a trade lane in the retail data has a <c>lane_id</c> line. So the rule of the game must find exactly the zones
/// with that line. A zone keeps the first role that it gets, so a trade lane zone that is also the exclusion zone of a
/// field or a nebula has the exclusion role.
/// </summary>
public sealed class TradeLaneZoneRetailTests(ITestOutputHelper output)
{
    static TradeLaneZoneRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>Most trade lane zones of the retail game have a text such as a lane name in <c>lane_id</c>.</summary>
    [Fact]
    public void EachRetailTradeLaneZoneHasTheTradeLaneRole() =>
        Assert.Equal(new Counts(129, 91, 93, 36), Roles(GameDataPaths.RequireBase()));

    /// <summary>
    /// The engine reads a text in <c>lane_id</c> of a text file as 0, so a role from <c>lane_id</c> missed these zones in
    /// HD Edition. The test needs <c>OPENLANCER_HDE</c>, a copy of an HD Edition install. It only reads it, and it skips
    /// without it.
    /// </summary>
    [Fact]
    public void EachTradeLaneZoneOfHdEditionHasTheTradeLaneRole() =>
        Assert.Equal(new Counts(129, 91, 91, 38), Roles(GameDataPaths.RequireHde()));

    /// <summary>The counts of the zones of trade lanes.</summary>
    /// <param name="Zones">The zones with a <c>lane_id</c> line.</param>
    /// <param name="Texts">The zones whose <c>lane_id</c> is not a whole number.</param>
    /// <param name="TradeLane">The zones with the trade lane role.</param>
    /// <param name="Exclusion">The zones with the exclusion role.</param>
    private sealed record Counts(int Zones, int Texts, int TradeLane, int Exclusion);

    /// <summary>
    /// Asserts that the zones that the rule of the game finds are the zones with a <c>lane_id</c> line, and that each of
    /// them has the trade lane role or the exclusion role. Returns the counts.
    /// </summary>
    private Counts Roles(string folder)
    {
        using var workspace = new RetailWorkspace(folder, "lanes");
        var session = workspace.Open();
        var withLaneId = new List<string>();
        var texts = 0;
        foreach (var path in RetailChecks.SystemFiles(session))
        {
            foreach (var section in session.Sections(path).Where(s => s.Id.Type.Equals("zone", StringComparison.OrdinalIgnoreCase)))
            {
                if (session.Get(section)!.Lines("lane_id") is [{ Values: [var value, ..] }, ..])
                {
                    withLaneId.Add(section.Id.Key!);
                    texts += int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? 0 : 1;
                }
            }
        }

        var fs = session.EditView.CreateFileSystem();
        var resources = new ServerResourceManager(null, fs);
        // The resource manager is not disposable, but its physics shape pool is.
        using var shapes = resources.ConvexCollection;
        var data = new GameDataManager(new GameItemDb(fs), resources);
        data.LoadData(null);
        var lanes = new List<string>();
        var roles = new List<ZoneRole>();
        foreach (var system in data.Items.Systems)
        {
            var index = new SystemIndex(system, system);
            foreach (var zone in system.Zones.Where(SystemIndex.IsTradeLaneZone))
            {
                lanes.Add(zone.Nickname);
                roles.Add(index.ZoneRole(zone));
            }
        }

        var counts = new Counts(withLaneId.Count, texts, roles.Count(r => r == ZoneRole.TradeLane),
            roles.Count(r => r == ZoneRole.Exclusion));
        output.WriteLine(counts.ToString());
        Assert.Equal(Sorted(withLaneId), Sorted(lanes));
        Assert.All(roles, r => Assert.True(r is ZoneRole.TradeLane or ZoneRole.Exclusion, r.ToString()));
        return counts;
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> nicknames) =>
        nicknames.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal);
}
