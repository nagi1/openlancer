using System;
using LibreLancer.Data.GameData.World;

namespace OpenLancer.Runtime.Scene;

/// <summary>The rules of the retail game that read the values of a zone.</summary>
public static class ZoneRules
{
    /// <summary>
    /// True when the game uses a zone as the zone of a trade lane: its nickname has the word "Tradelane" with this letter
    /// case, and it is a box that is more than 10 times longer on its z axis than on its x axis and on its y axis. The
    /// game does not read <c>lane_id</c> (decision D513 of the console filter design), so the rule does not use it. Most
    /// trade lane zones have a text such as <c>br01_2</c> in that key, and the engine reads such a text in a text file as
    /// 0.
    /// </summary>
    /// <remarks>Retail does not round the products of the sizes to float precision. A product in float precision can round
    /// up to the z size and give another result at the limit. A double holds the exact product.</remarks>
    public static bool IsTradeLaneZone(Zone zone) =>
        zone.Nickname.Contains("Tradelane", StringComparison.Ordinal) && zone.Shape == ShapeKind.Box &&
        (double)zone.Size.X * 10 < zone.Size.Z && (double)zone.Size.Y * 10 < zone.Size.Z;
}
