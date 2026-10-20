using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Missions.Prefabs;

/// <summary>
/// The template of a prefab in prefab mode (section 5.7 of the M6b design): its sections in the role form, which the
/// trigger, line and link commands edit as a script. The <c>[Prefab]</c>, <c>[Field]</c> and <c>[Port]</c> sections stay
/// as they are. A template section has its role as its nickname, and each value that names a member names its role.
/// So <c>trg:prefab:mod:ol_gate#spawn</c> names the section of the role <c>spawn</c>.
/// </summary>
/// <remarks>The slots stay text: a value <c>{delay}</c> and a line <c>{spawnWhen} =</c> are lines of the role form, as the
/// template has them.</remarks>
public static class PrefabMode
{
    /// <summary>The section types of a template that are not mission sections.</summary>
    private static readonly HashSet<string> HeaderTypes = new(StringComparer.OrdinalIgnoreCase) { "Prefab", "Field", "Port" };

    /// <summary>True for a section type of the header of a template: <c>[Prefab]</c>, <c>[Field]</c> or
    /// <c>[Port]</c>.</summary>
    public static bool IsHeader(string type) => HeaderTypes.Contains(type);

    /// <summary>The made nickname line of a role: <c>{instance}_&lt;role&gt;</c>.</summary>
    public static string PatternOf(string role) => $"{{{PrefabSlot.Instance}}}_{role}";

    /// <summary>
    /// The sections of a template in the role form: the header sections with their lines, then each template section
    /// with its role as its nickname. A value that is the made nickname of a member gives its role.
    /// </summary>
    public static IReadOnlyList<Section> RoleForm(PrefabTemplate template)
    {
        var text = new StringBuilder();
        var full = template.Write();
        var body = full[(full.IndexOf("[Prefab]", StringComparison.Ordinal))..];
        var headerEnd = template.Sections.Count > 0 ? HeaderLength(body) : body.Length;
        text.Append(body[..headerEnd]);
        var patterns = template.Sections.ToDictionary(s => s.NicknamePattern ?? PatternOf(s.Role), s => s.Role,
            StringComparer.OrdinalIgnoreCase);
        var result = Parse(text.ToString());
        foreach (var section in template.Sections)
        {
            var made = new Section(section.Type);
            var nickname = true;
            foreach (var line in section.Lines)
            {
                if (nickname && line.Key.Equals("nickname", StringComparison.OrdinalIgnoreCase))
                {
                    nickname = false;
                    made.Add(Entry(made, line.Key, [section.Role]));
                    continue;
                }
                var texts = PrefabRow.Split(line.Value).Select(t => patterns.GetValueOrDefault(t) ?? t).ToList();
                made.Add(Entry(made, line.Key, texts));
            }
            result.Add(made);
        }
        return result;
    }

    /// <summary>
    /// The text of a template from its role form: the header sections as they are, and each template section with its
    /// role line first, its nickname line with the made nickname, and each value that names a member role with the made
    /// nickname of the member. A role that the template had keeps its nickname line.
    /// </summary>
    /// <param name="before">The template before the edit, for the nickname lines of the roles that it had.</param>
    public static string TemplateText(IReadOnlyList<Section> sections, PrefabTemplate before)
    {
        var schema = MissionSchema.Retail;
        var roles = sections.Where(s => !IsHeader(s.Name)).Select(s => (Type: s.Name, Role: RoleOf(s)))
            .Where(r => r.Role != null).ToList();
        var patterns = roles.ToDictionary(r => r.Role!, r => before.Section(r.Role!)?.NicknamePattern ?? PatternOf(r.Role!),
            StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        text.Append(PrefabTemplate.Header).Append('\n');
        foreach (var section in sections)
        {
            text.Append('[').Append(section.Name).Append("]\n");
            if (IsHeader(section.Name))
            {
                foreach (var entry in section)
                    Line(text, entry.Name, MissionValues.TextsOf(entry));
                text.Append('\n');
                continue;
            }
            var role = RoleOf(section) ?? "";
            text.Append("role = ").Append(role).Append('\n');
            // The values that name a member: by the reference kinds of the schema, as a rename finds them.
            var named = new HashSet<(int Row, int Value)>();
            var replace = new Dictionary<(int Row, int Value), string>();
            foreach (var (type, other) in roles)
            {
                foreach (var (_, row, value) in MissionLinks.Naming(schema, [section], MissionLinks.KindsOf(type), other!))
                {
                    if (named.Add((row, value)))
                        replace[(row, value)] = patterns[other!];
                }
            }
            var nickname = NicknameLine(section);
            for (var r = 0; r < section.Count; r++)
            {
                var entry = section[r];
                if (r == nickname)
                {
                    Line(text, entry.Name, [patterns.GetValueOrDefault(role) ?? PatternOf(role)]);
                    continue;
                }
                var texts = MissionValues.TextsOf(entry).Select((t, v) => replace.GetValueOrDefault((r, v)) ?? t).ToList();
                Line(text, entry.Name, texts);
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>The role of a template section in the role form: its nickname.</summary>
    public static string? RoleOf(Section section)
    {
        if (MissionIndex.Is(section.Name, "Trigger"))
            return TriggerRules.Nickname(section);
        return section.FirstOrDefault(e => MissionIndex.Is(e.Name, "nickname")) is { Count: > 0 } line
            ? MissionIndex.Text(line[0])
            : null;
    }

    /// <summary>The index of the nickname line of a section, or -1.</summary>
    private static int NicknameLine(Section section) =>
        MissionIndex.Is(section.Name, "Trigger") ? TriggerRules.NicknameLine(section)
            : section.ToList().FindIndex(e => MissionIndex.Is(e.Name, "nickname"));

    /// <summary>The length of the header sections in the canonical text of a template: up to its first template
    /// section.</summary>
    private static int HeaderLength(string body)
    {
        var at = 0;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.IndexOf(']') is > 1 and var close &&
                !IsHeader(trimmed[1..close]))
                return at;
            at += line.Length + 1;
        }
        return body.Length;
    }

    /// <summary>The sections of the text of header sections.</summary>
    private static List<Section> Parse(string text)
    {
        var sections = new List<Section>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line[0] == '[')
            {
                sections.Add(new Section(line[1..line.IndexOf(']')].Trim()));
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals < 0 || sections.Count == 0)
                continue;
            var section = sections[^1];
            section.Add(Entry(section, line[..equals].Trim(), [line[(equals + 1)..].Trim()]));
        }
        return sections;
    }

    /// <summary>A line of the role form with its value texts. Each text is one string value, so a slot and a number keep
    /// their text.</summary>
    internal static Entry Entry(Section section, string key, IReadOnlyList<string> texts)
    {
        var entry = new Entry(section, key);
        foreach (var text in texts.Where(t => t.Length > 0 || texts.Count > 1))
            entry.Add(ValueOf(text));
        return entry;
    }

    /// <summary>The value of a text as the text parser reads it, or a string value for a text that it does not read
    /// as one value. A text with ';' stays whole, because the parser reads ';' as the start of a comment, but a header
    /// text such as a help text can have one.</summary>
    private static ValueBase ValueOf(string text)
    {
        if (text.Contains(';'))
            return new StringValue(text);
        var parsed = IniDocument.ParseTextLine("v = " + text);
        return parsed is { Count: 1 } ? parsed[0] : new StringValue(text);
    }

    private static void Line(StringBuilder text, string key, IReadOnlyList<string> texts)
    {
        text.Append(key).Append(" =");
        if (texts.Count > 0)
            text.Append(' ').Append(string.Join(", ", texts));
        text.Append('\n');
    }
}
