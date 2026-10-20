using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Missions.Run;

/// <summary>The place of a start save: a base with its system, or a point in a system with a heading.</summary>
/// <param name="System">The nickname of the system.</param>
/// <param name="Base">The nickname of the base of a base start, else null.</param>
public sealed record StartSavePlace(string System, string? Base, Vector3 Position, Quaternion Orientation)
{
    /// <summary>A start docked at a base of a system.</summary>
    public static StartSavePlace AtBase(string system, string dock) =>
        new(system, dock, Vector3.Zero, Quaternion.Identity);
}

/// <summary>
/// The start save of a play from a trigger (section 5.5 of the M6c design, decisions D750 and D753), as plain text. Each
/// line comes from the one canonical line writer (<see cref="IniLines"/>). The OpenLancer form (F5) has a short
/// <c>[Player]</c> section, then the mission part: <c>[StoryInfo]</c>, <c>[MissionState]</c>, each
/// <c>[TriggerSave]</c>, and <c>[BStoryMissionDone]</c>. The reader of the mission state stops at that last header, so
/// the mission part always comes before it.
/// </summary>
public static class StartSaveText
{
    /// <summary>The value of <c>delta_worth</c> that the game reads as "no worth rule".</summary>
    private const string NoWorth = "-1";

    /// <summary>The text of the OpenLancer form of a plan that can start.</summary>
    /// <param name="time">The time of the write: <c>tstamp</c>, so the load menu lists the file first.</param>
    /// <exception cref="ArgumentException">The plan cannot start, or it has no story slot.</exception>
    public static string OpenLancer(StartPlan plan, StartSavePlace place, DateTime time) =>
        Text([PlayerSection(plan, place, time), .. MissionPart(plan)]);

    /// <summary>The text of the description: "OpenLancer start: {slot}, {start}" (section 5.5 of the M6c
    /// design).</summary>
    public static string Description(StartPlan plan) =>
        $"OpenLancer start: {plan.Slot?.Slot ?? "test script"}, {plan.StartName}";

    /// <summary>The hex form of a text that a save reads: four hex digits for each UTF-16 unit, high byte first. The text
    /// of the description has a comma, so the plain form cannot hold it.</summary>
    public static string Hex(string text) => Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));

    /// <summary>
    /// The sections of the mission part, in this order: <c>[StoryInfo]</c> with <c>Mission</c> before
    /// <c>MissionNum</c>, <c>[MissionState]</c> (always, also before the accept), one <c>[TriggerSave]</c> for each
    /// saved trigger, and <c>[BStoryMissionDone]</c>. A trigger id is <c>FLHash.CreateID</c> of the name as an unsigned
    /// decimal number, the form that HD Edition writes.
    /// </summary>
    /// <exception cref="ArgumentException">The plan cannot start, or it has no story slot.</exception>
    public static IReadOnlyList<(string Header, IReadOnlyList<string> Lines)> MissionPart(StartPlan plan)
    {
        if (!plan.CanStart || plan.Slot is not { } slot)
            throw new ArgumentException("Only a plan that can start has a start save.", nameof(plan));
        var state = new List<string> { Line("mission_accepted", plan.Accepted ? "1" : "0") };
        if (plan.State.Title != 0)
            state.Add(Line("msn_title", Number(plan.State.Title)));
        if (plan.State.Offer != 0)
            state.Add(Line("msn_offer", Number(plan.State.Offer)));
        state.AddRange(plan.State.Scenes.Select(s => Line("rtc", s.File, s.Repeats ? "true" : "false")));
        state.AddRange(plan.State.Ambients.Select(a =>
            Line("ambi_scene", a.File, Id(a.Room), Id(a.Base))));
        var sections = new List<(string, IReadOnlyList<string>)>
        {
            ("StoryInfo", [Line("Mission", slot.Controller), Line("MissionNum", Number(plan.Stage)), Line("delta_worth", NoWorth)]),
            ("MissionState", state),
        };
        sections.AddRange(plan.SavedTriggers.Select(t => ("TriggerSave", (IReadOnlyList<string>)[Line("trigger", Id(t))])));
        sections.Add(("BStoryMissionDone", []));
        return sections;
    }

    /// <summary>The short <c>[Player]</c> section of the OpenLancer form: the description, the time, the system, and the
    /// base or the point with the heading as Euler angles in degrees.</summary>
    private static (string, IReadOnlyList<string>) PlayerSection(StartPlan plan, StartSavePlace place, DateTime time)
    {
        var fileTime = time.ToFileTime();
        var lines = new List<string>
        {
            Line("description", Hex(Description(plan))),
            Line("tstamp", Number((uint)(fileTime >> 32)), Number((uint)(fileTime & 0xFFFFFFFF))),
            Line("system", place.System),
        };
        if (place.Base is { } dock)
        {
            lines.Add(Line("base", dock));
        }
        else
        {
            var angles = place.Orientation.GetEulerDegrees();
            lines.Add(Line("pos", Number(place.Position.X), Number(place.Position.Y), Number(place.Position.Z)));
            lines.Add(Line("rotate", Number(angles.X), Number(angles.Y), Number(angles.Z)));
        }
        return ("Player", lines);
    }

    /// <summary>The text of sections with the line end of canonical text, an empty line between two sections.</summary>
    private static string Text(IEnumerable<(string Header, IReadOnlyList<string> Lines)> sections)
    {
        var text = new StringBuilder();
        foreach (var (header, lines) in sections)
        {
            if (text.Length > 0)
                text.Append(IniLines.LineEnd);
            text.Append('[').Append(header).Append(']').Append(IniLines.LineEnd);
            foreach (var line in lines)
                text.Append(line).Append(IniLines.LineEnd);
        }
        return text.ToString();
    }

    private static string Line(string key, params string[] values) =>
        IniLines.Line(key, values.Select(v => (ValueBase)new StringValue(v)));

    /// <summary>The game id of a name as an unsigned decimal number.</summary>
    private static string Id(string name) => Number(FLHash.CreateID(name.Trim()));

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A coordinate or an angle with at most three decimals, and 0 for a value that rounds to 0.</summary>
    private static string Number(float value)
    {
        var rounded = MathF.Round(value, 3);
        return (rounded == 0 ? 0 : rounded).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
