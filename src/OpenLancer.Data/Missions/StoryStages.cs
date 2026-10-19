using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Schema.Storyline;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The 43 story stages of the game code, in order (section 14.6 of the M6a design). They come from the storyline of
/// the engine, which holds the stages of the game code. News items and rumors name them.
/// </summary>
public static class StoryStages
{
    private static readonly Lazy<StorylineIni> Storyline = new(() =>
    {
        var storyline = new StorylineIni();
        storyline.AddDefault();
        return storyline;
    });

    /// <summary>The storyline of the engine.</summary>
    public static StorylineIni Default => Storyline.Value;

    /// <summary>The stage names in order. The index of a name is its stage number.</summary>
    public static IReadOnlyList<string> Names { get; } = Default.Items.Select(i => i.Nickname).ToList();

    /// <summary>The name of a stage number, or an empty text for a number outside the stages.</summary>
    public static string NameOf(int stage) => stage >= 0 && stage < Names.Count ? Names[stage] : "";
}
