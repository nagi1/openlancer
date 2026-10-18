using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing.Assets;

/// <summary>A member of a prop group: an object, a zone or a light (section 9.2 of the M23 design).</summary>
/// <param name="Member">The member id, unique in the group without letter case, for example
/// <c>large_transport_2</c>. It can hold a space, as 26 retail nicknames do.</param>
/// <param name="Type">The section type of the system file: <c>Object</c>, <c>zone</c> or <c>LightSource</c>.</param>
/// <param name="Position">The position relative to the anchor of the group.</param>
/// <param name="Lines">The lines of the member in order, without the <c>member</c> line. The <c>pos</c> line holds the
/// position relative to the anchor. A <c>parent</c> or a <c>ring</c> line names a member id.</param>
public sealed record PropMember(string Member, string Type, Vector3 Position, IReadOnlyList<EntryView> Lines);

/// <summary>
/// A prop group of a mod (decisions D283 to D294 of the M23 design): a set of separate objects, zones and lights in the
/// file <c>props/&lt;nickname&gt;.ini</c> of the mod, with its texts in <c>props/&lt;nickname&gt;.strings.json</c>. The
/// merge and the game ignore the folder. A placement copies each member as a separate item into a system.
/// </summary>
public sealed class PropGroup
{
    internal PropGroup(string nickname, string name, string? modId, string file, bool editable,
        IReadOnlyList<PropMember> members, IReadOnlyList<Diagnostic> problems,
        IReadOnlyDictionary<StringPlaceholder, string> texts)
    {
        Nickname = nickname;
        Name = name;
        ModId = modId;
        File = file;
        Editable = editable;
        Members = members;
        Problems = problems;
        Texts = texts;
    }

    /// <summary>The nickname: the file name without its extension.</summary>
    public string Nickname { get; }

    /// <summary>The name for the user, or the nickname when the file gives none.</summary>
    public string Name { get; }

    /// <summary>The mod that has the group, or null for the active mod.</summary>
    public string? ModId { get; }

    /// <summary>The full path of the group file.</summary>
    public string File { get; }

    /// <summary>The full path of the file of the texts of the group. It need not exist.</summary>
    public string TextsFile => PropGroupFile.TextsPath(File);

    /// <summary>True for a group of the active mod, which the user can rename and delete. A group of a lower mod can
    /// only be placed (decision D289).</summary>
    public bool Editable { get; }

    public IReadOnlyList<PropMember> Members { get; }

    /// <summary>The problems of the file, each with its line. A group with a problem does not place.</summary>
    public IReadOnlyList<Diagnostic> Problems { get; }

    /// <summary>The texts of the group, by the placeholder that a member line holds.</summary>
    internal IReadOnlyDictionary<StringPlaceholder, string> Texts { get; }
}

/// <summary>
/// Reads and writes the plain INI file of a prop group (decision D284 of the M23 design). A <c>[PropGroup]</c> header
/// holds <c>nickname</c>, <c>name</c> and <c>format = 1</c>. Then each member has a section with the section type of the
/// system file. A member has <c>member = &lt;id&gt;</c> in place of <c>nickname</c>, and its <c>pos</c> is relative to
/// the anchor. The texts use the format of <c>strings.json</c> in a file of their own.
/// </summary>
internal static class PropGroupFile
{
    public const int Format = 1;
    public const string HeaderType = "PropGroup";
    public const string MemberKey = "member";
    public const string Extension = ".ini";
    public const string TextsExtension = ".strings.json";

    /// <summary>The comment at the top of each group file.</summary>
    public static readonly string[] Comment =
    [
        "; OpenLancer prop group, format 1. The game does not read this file.",
        "; The editor places each member as a separate object, zone or light.",
    ];

    /// <summary>The section types of the members, as a system file spells them.</summary>
    public static readonly string[] MemberTypes = [SceneSections.ObjectType, SceneSections.ZoneType, SceneSections.LightType];

    /// <summary>The file of the texts of a group file: <c>&lt;nickname&gt;.strings.json</c> next to it.</summary>
    public static string TextsPath(string file) =>
        Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file) + TextsExtension);

    /// <summary>The member type as a system file spells it, or null for another section type.</summary>
    public static string? MemberType(string type) =>
        MemberTypes.FirstOrDefault(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a group file and the file of its texts. Each problem of section 9.2 of the M23 design gets its line: no
    /// <c>[PropGroup]</c> section first, a format above 1 (which stops the read), a section type that is not a member
    /// type, a member without a <c>member</c> line, a repeated member id, a <c>pos</c> that is not three numbers, a
    /// <c>parent</c> or a <c>ring</c> that names no member, and a text key that the file of the texts does not have.
    /// </summary>
    /// <param name="modId">The mod of the group, or null for the active mod.</param>
    public static PropGroup Read(string file, string? modId, bool editable)
    {
        var nickname = Path.GetFileNameWithoutExtension(file);
        var name = "";
        var fileName = Path.GetFileName(file);
        var problems = new List<Diagnostic>();
        void Problem(int line, string message) =>
            problems.Add(Diagnostic.Error(file, line > 0 ? $"{fileName}, line {line}: {message}" : $"{fileName}: {message}"));

        byte[] bytes;
        IReadOnlyList<Section> sections;
        try
        {
            bytes = SharedFile.ReadAllBytes(file);
            sections = IniDocument.Parse(file, bytes).Sections;
        }
        catch (Exception e)
        {
            // A file that the parser cannot read gives no members. The group shows the problem and does not place.
            Problem(0, $"The editor cannot read the file: {e.Message}");
            return new PropGroup(nickname, nickname, modId, file, editable, [], problems,
                new Dictionary<StringPlaceholder, string>());
        }

        var raw = IniDocument.TextEncoding.GetString(bytes).Split('\n');
        var texts = ReadTexts(TextsPath(file), out var textProblem);
        if (textProblem != null)
            Problem(0, textProblem);

        var header = sections.FirstOrDefault(s => s.Name.Equals(HeaderType, StringComparison.OrdinalIgnoreCase));
        if (sections.Count == 0 || !ReferenceEquals(header, sections[0]))
            Problem(sections.FirstOrDefault()?.Line ?? 1, $"The file has no [{HeaderType}] section first.");
        if (header != null)
        {
            var format = header.LastOrDefault(e => SceneSections.Is(e.Name, "format") && e.Count > 0);
            if (format != null && format[0].TryToInt32(out var number) && number > Format)
            {
                // A later editor wrote the file, so this editor does not know its rules.
                problems.Clear();
                Problem(format.Line, $"OpenLancer cannot read prop group format {number}. It reads format {Format}.");
                return new PropGroup(nickname, nickname, modId, file, editable, [], problems, texts);
            }
            if (format == null || !format[0].TryToInt32(out number) || number < Format)
                Problem(format?.Line ?? header.Line, $"The [{HeaderType}] section has no 'format = {Format}' line.");
            if (header.LastOrDefault(e => SceneSections.Is(e.Name, "name")) is { } nameLine)
                name = RawValue(raw, nameLine.Line);
        }

        var members = new List<PropMember>();
        var lineOf = new Dictionary<PropMember, Section>();
        foreach (var section in sections)
        {
            if (ReferenceEquals(section, header))
                continue;
            if (MemberType(section.Name) is not { } type)
            {
                Problem(section.Line, $"[{section.Name}] is not an object, a zone or a light. A group holds [Object], " +
                                      "[zone] and [LightSource] sections.");
                continue;
            }
            // The member id and the links between the members keep their text: the parser reads an id such as "14"
            // as a number.
            var memberLine = section.FirstOrDefault(e => SceneSections.Is(e.Name, MemberKey) && e.Count > 0);
            if (memberLine == null || RawValue(raw, memberLine.Line) is not { Length: > 0 } id)
            {
                Problem(section.Line, $"The [{section.Name}] section has no '{MemberKey}' line.");
                continue;
            }
            if (members.Any(m => m.Member.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                Problem(memberLine.Line, $"The member id '{id}' repeats. Each member needs an id of its own.");
                continue;
            }
            var position = Vector3.Zero;
            if (section.LastOrDefault(e => SceneSections.Is(e.Name, "pos")) is { } pos && !TryPosition(pos, out position))
                Problem(pos.Line, $"The 'pos' line of member '{id}' needs three numbers.");
            var lines = section.Where(e => !SceneSections.Is(e.Name, MemberKey))
                .Select(e => IsLink(type, e.Name) && e.Count > 0
                    ? new EntryView(e.Name, [new StringValue(RawFirst(raw, e.Line)), .. e.Skip(1)])
                    : new EntryView(e.Name, e.ToArray()))
                .ToList();
            var member = new PropMember(id, type, position, lines);
            members.Add(member);
            lineOf[member] = section;
        }

        foreach (var member in members)
        {
            var section = lineOf[member];
            foreach (var entry in section)
            {
                if (SceneSections.Is(member.Type, SceneSections.ObjectType) && entry.Count > 0 &&
                    (SceneSections.Is(entry.Name, "parent") || SceneSections.Is(entry.Name, "ring")))
                {
                    var type = SceneSections.Is(entry.Name, "parent") ? SceneSections.ObjectType : SceneSections.ZoneType;
                    var named = RawFirst(raw, entry.Line);
                    if (!members.Any(m => SceneSections.Is(m.Type, type) && m.Member.Equals(named, StringComparison.OrdinalIgnoreCase)))
                        Problem(entry.Line, $"'{entry.Name} = {named}' of member '{member.Member}' names no " +
                                            $"{(type == SceneSections.ObjectType ? "object" : "zone")} of the group.");
                }
                foreach (var value in entry)
                {
                    if (StringPlaceholder.TryParse(value, out var placeholder) && !texts.ContainsKey(placeholder))
                        Problem(entry.Line, $"The text key '{placeholder.Key}' of member '{member.Member}' is not in " +
                                            $"{Path.GetFileName(TextsPath(file))}.");
                }
            }
        }
        return new PropGroup(nickname, name.Length > 0 ? name : nickname, modId, file, editable, members, problems, texts);
    }

    /// <summary>
    /// The bytes of a group file: the comment, the <c>[PropGroup]</c> section, then one section for each member with
    /// its <c>member</c> line first. Each line has the text of the patch writer for a text file.
    /// </summary>
    /// <exception cref="ArgumentException">A line of a member cannot be a line of a text file.</exception>
    public static byte[] Write(string nickname, string name, IReadOnlyList<PropMember> members)
    {
        var text = new StringBuilder();
        foreach (var line in Comment)
            text.Append(line).Append("\r\n");
        text.Append('[').Append(HeaderType).Append("]\r\n");
        text.Append("nickname = ").Append(nickname).Append("\r\n");
        text.Append("name = ").Append(name).Append("\r\n");
        text.Append("format = ").Append(Format.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var member in members)
        {
            text.Append("\r\n[").Append(member.Type).Append("]\r\n");
            text.Append(MemberKey).Append(" = ").Append(member.Member).Append("\r\n");
            foreach (var line in member.Lines)
                text.Append(LineText(member.Type, line) ?? throw new ArgumentException(LineError(member.Type, line)))
                    .Append("\r\n");
        }
        return IniDocument.TextEncoding.GetBytes(text.ToString());
    }

    /// <summary>The bytes of the file of the texts of a group, or null when the group has no text.</summary>
    public static byte[]? WriteTexts(string path, IReadOnlyDictionary<StringPlaceholder, string> texts) =>
        texts.Count == 0
            ? null
            : ModText.From(texts.Where(t => t.Key.Kind == StringKind.String).Select(t => KeyValuePair.Create(t.Key.Key, t.Value)),
                    texts.Where(t => t.Key.Kind == StringKind.Infocard).Select(t => KeyValuePair.Create(t.Key.Key, t.Value)))
                .ToFile(path);

    /// <summary>
    /// The text of a line of a member, as the patch writer writes it for a text file, or null when a text file cannot
    /// hold the line. A <c>parent</c> or a <c>ring</c> line of an object names a member id, which the reader takes as
    /// text. So its first value is the text of the id, also when the parser would read it as a number.
    /// </summary>
    public static string? LineText(string type, EntryView line)
    {
        if (!IsLink(type, line.Name) || line.Values.Count == 0)
            return PatchFormatter.TryFormatLine(EntryOf(type, line), null, false, out var text, out _) ? text : null;
        var rest = new EntryView(line.Name, line.Values.Skip(1).ToList());
        var first = line.Values[0].ToString().Trim();
        if (first.Length == 0 || first.IndexOfAny([',', ';']) >= 0 || first.Any(char.IsControl) || !Ascii.IsValid(first))
            return null;
        if (rest.Values.Count == 0)
            return $"{line.Name} = {first}";
        return PatchFormatter.TryFormatLine(EntryOf(type, rest), null, false, out var restText, out _)
            ? $"{line.Name} = {first}, {restText[(restText.IndexOf('=') + 1)..].Trim()}"
            : null;
    }

    /// <summary>Why a text file cannot hold a line of a member.</summary>
    public static string LineError(string type, EntryView line) =>
        PatchFormatter.TryFormatLine(EntryOf(type, line), null, false, out _, out var error) ? "" : error;

    /// <summary>True for a line of an object that names another member: <c>parent</c> or <c>ring</c>.</summary>
    public static bool IsLink(string type, string key) =>
        SceneSections.Is(type, SceneSections.ObjectType) && (SceneSections.Is(key, "parent") || SceneSections.Is(key, "ring"));

    /// <summary>The values of a line of a member as the merge reads them in a file, binary or text: the line goes through
    /// its text, as a patch line does. Null with the error when the file cannot hold the line.</summary>
    public static IReadOnlyList<ValueBase>? ValuesFor(string type, EntryView line, bool binary, out string? error)
    {
        if (!PatchFormatter.TryFormatLine(EntryOf(type, line), null, false, out var text, out error))
            return null;
        var at = text.IndexOf('=');
        var values = text[(at + 1)..].Trim();
        return PatchFormatter.TryReadLine(line.Name, values, null, binary, out var result, out error) ? result : null;
    }

    private static Entry EntryOf(string type, EntryView line)
    {
        var entry = new Entry(new Section(type), line.Name, line.Values.Count);
        foreach (var value in line.Values)
            entry.Add(value);
        return entry;
    }

    /// <summary>Reads the three numbers of a <c>pos</c> line.</summary>
    internal static bool TryPosition(Entry line, out Vector3 position) => TryPosition(line.ToArray(), out position);

    /// <summary>Reads the three numbers of the values of a <c>pos</c> line.</summary>
    internal static bool TryPosition(IReadOnlyList<ValueBase> values, out Vector3 position)
    {
        position = Vector3.Zero;
        if (values.Count != 3 || !values[0].TryToSingle(out var x) || !values[1].TryToSingle(out var y) ||
            !values[2].TryToSingle(out var z))
            return false;
        position = new Vector3(x, y, z);
        return true;
    }

    /// <summary>The texts of a group from its file of texts, by placeholder. A missing file gives no text.</summary>
    private static Dictionary<StringPlaceholder, string> ReadTexts(string path, out string? problem)
    {
        problem = null;
        var result = new Dictionary<StringPlaceholder, string>();
        try
        {
            var strings = ModStrings.Load(path);
            foreach (var (key, text) in strings.Strings)
                result[new StringPlaceholder(StringKind.String, key)] = text;
            foreach (var (key, text) in strings.Infocards)
                result[new StringPlaceholder(StringKind.Infocard, key)] = text;
        }
        catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException)
        {
            problem = $"The editor cannot read {Path.GetFileName(path)}: {e.Message}";
        }
        return result;
    }

    /// <summary>The first value of a line as its text, before the first comma.</summary>
    private static string RawFirst(string[] lines, int line)
    {
        var text = RawValue(lines, line);
        var comma = text.IndexOf(',');
        return (comma < 0 ? text : text[..comma]).Trim();
    }

    /// <summary>The text of a line after its <c>=</c>, without a comment and without white space at the ends. The name
    /// of a group keeps its commas this way, which the parser reads as separators.</summary>
    private static string RawValue(string[] lines, int line)
    {
        if (line < 1 || line > lines.Length)
            return "";
        var text = lines[line - 1];
        var at = text.IndexOf('=');
        if (at < 0)
            return "";
        text = text[(at + 1)..];
        var comment = text.IndexOf(';');
        return (comment < 0 ? text : text[..comment]).Trim();
    }
}
