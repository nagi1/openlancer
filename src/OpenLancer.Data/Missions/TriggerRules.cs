using System;
using System.Collections.Generic;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The retail rules of a <c>[Trigger]</c> section that the mission checks and the target checks of M11 share (section
/// 12.1 of the M6a design, decision D556). The retail game reads the lines of a trigger from its first
/// <c>nickname</c> line. It ignores each line before it (TR2), and the last condition line counts (TR1).
/// </summary>
public static class TriggerRules
{
    /// <summary>The prefix of a condition key.</summary>
    public const string ConditionPrefix = "Cnd_";

    /// <summary>The prefix of an action key.</summary>
    public const string ActionPrefix = "Act_";

    /// <summary>The index of the first <c>nickname</c> line of a section, or -1.</summary>
    public static int NicknameLine(Section section)
    {
        for (var i = 0; i < section.Count; i++)
        {
            if (section[i].Name.Equals("nickname", StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>The value of the first <c>nickname</c> line of a section, without blanks at its ends. Null when the
    /// section has no such line or the line has no value. The game skips a trigger without a nickname.</summary>
    public static string? Nickname(Section section) =>
        NicknameLine(section) is >= 0 and var line && section[line].Count > 0 &&
        MissionIndex.Text(section[line][0]) is { Length: > 0 } name
            ? name
            : null;

    /// <summary>The indexes of the condition lines after the first <c>nickname</c> line, in file order. A section
    /// without a nickname has none.</summary>
    public static IReadOnlyList<int> ConditionLines(Section section)
    {
        var result = new List<int>();
        var nickname = NicknameLine(section);
        if (nickname < 0)
            return result;
        for (var i = nickname + 1; i < section.Count; i++)
        {
            if (IsCondition(section[i].Name))
                result.Add(i);
        }
        return result;
    }

    /// <summary>The index of the condition line that the game uses: the last condition line after the first
    /// <c>nickname</c> line, or -1 when the trigger has none.</summary>
    public static int EffectiveCondition(Section section)
    {
        var lines = ConditionLines(section);
        return lines.Count > 0 ? lines[^1] : -1;
    }

    /// <summary>True for a condition key.</summary>
    public static bool IsCondition(string key) => key.StartsWith(ConditionPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for an action key.</summary>
    public static bool IsAction(string key) => key.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The message of rule M01. The target check of M11 gives the same text.</summary>
    public static string NoConditionMessage(string trigger) =>
        $"Trigger '{trigger}' has no condition. The game stops when this trigger starts. Add one condition, for example " +
        "Cnd_True = no_params.";

    /// <summary>True for a text that the boolean getter of the retail game reads: true, 1, false or 0, in any letter
    /// case.</summary>
    public static bool ReadsAsBool(string text) =>
        text.Trim().ToLowerInvariant() is "true" or "false" or "1" or "0";

    /// <summary>The message of rule M14. The target check of M11 gives the same text.</summary>
    public static string BooleanMessage(string value) =>
        $"'{value}' is not true or false. The game reads only true, 1, false and 0. For another value, it uses false. " +
        "Pick true or false.";
}
