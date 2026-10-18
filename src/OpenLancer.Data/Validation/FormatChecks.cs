using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Validation;

/// <summary>
/// The format checks of a target build (milestone M11): data that the retail text reader reads in another way, or does
/// not accept. Each check looks only at the lines that the export writes: the lines that the mods change or add, and
/// each line of a binary file that the export writes as text. So the checks report only what the mods add.
/// </summary>
/// <remarks>
/// The retail facts: the reader reads a text line in a window of 1,023 bytes, and a longer line splits. <c>;</c> starts a
/// comment, also after a value, and commas separate the values. A section name, a key and the value text each have a
/// buffer of 1,024 bytes. The integer getter reads text with <c>atoi</c>, so it reads <c>true</c> as 0. The float getter
/// reads a number with a trailing <c>d</c>, <c>b</c> or <c>h</c> in another base.
/// </remarks>
public static partial class FormatChecks
{
    /// <summary>The longest line that the retail reader reads in one piece, in bytes.</summary>
    public const int MaxLineBytes = 1023;

    /// <summary>A number with a trailing <c>d</c>, <c>b</c> or <c>h</c>.</summary>
    [GeneratedRegex(@"^[+-]?[0-9][0-9A-Fa-f]*[dbh]$")]
    private static partial Regex SuffixNumber();

    /// <summary>Runs the checks on the files of a target build.</summary>
    public static IReadOnlyList<Diagnostic> Run(TargetFiles files)
    {
        var problems = new List<Diagnostic>();
        foreach (var file in files.Files)
        {
            if (file is { Kind: TargetFileKind.Ini, Merged: { } merged, Diff: { } diff })
                CheckIni(file, merged, diff, problems);
            else if (file is { Kind: TargetFileKind.Cutscene, MergedTree: { } tree })
                CheckCutscene(file, tree, problems);
        }
        foreach (var (id, text) in files.StringIds.Infocards.OrderBy(i => i.Key))
            CheckInfocard(id, text, problems);
        return problems;
    }

    private static void CheckIni(TargetFile file, IniDocument merged, FileDiff diff, List<Diagnostic> problems)
    {
        // A binary base, a new file and a whole file of a mod write each line.
        var allLines = file.BaseDocument == null || file.BaseDocument.IsBinary || file.IsModFile;
        var binary = file.BaseDocument?.IsBinary == true;
        var pairs = diff.Matched.ToDictionary(m => m.Merged);
        foreach (var (section, index) in merged.Sections.Select((s, i) => (s, i)))
        {
            var pair = pairs.GetValueOrDefault(index);
            var identity = SectionIdentity.Of(merged.Sections, index);
            var place = new SectionRef(file.Path, identity);
            var sectionBytes = Encoding(section.Name);
            if (sectionBytes > MaxLineBytes && (allLines || pair == null))
                problems.Add(Problem(true, file, place, null,
                    $"[{section.Name}]: the section name is {sectionBytes} bytes long. The game writes past the end of its buffer."));
            for (var j = 0; j < section.Count; j++)
            {
                var entry = section[j];
                var replaced = pair != null && pair.BaseOf[j] >= 0 ? diff.BaseSections[pair.Base][pair.BaseOf[j]] : null;
                var modLine = pair == null || pair.BaseOf[j] < 0 || pair.ValueChanged[pair.BaseOf[j]];
                if (allLines || modLine)
                    CheckWritten(file, place, section, entry, binary, problems);
                if (modLine)
                    CheckValues(file, place, section, entry, replaced, problems);
            }
        }
    }

    /// <summary>The checks of a line that the export writes: its length, its buffers and the characters that split
    /// it.</summary>
    private static void CheckWritten(TargetFile file, SectionRef place, Section section, Entry entry, bool binary,
        List<Diagnostic> problems)
    {
        var label = $"{place.Id} {entry.Name}";
        var texts = entry.Select(RetailText.Of).ToList();
        var line = IniTextEdit.Line(entry.Name, entry);
        var longest = texts.Select(Encoding).Prepend(Encoding(entry.Name)).Max();
        if (longest > MaxLineBytes)
            problems.Add(Problem(true, file, place, entry.Name,
                $"{label}: a key or a value is {longest} bytes long. The game writes past the end of its buffer."));
        else if (Encoding(line) > MaxLineBytes)
            problems.Add(Problem(true, file, place, entry.Name,
                $"{label}: the line is {Encoding(line)} bytes long. The game reads only the first 1,023 bytes of " +
                "the line. It reads the rest as the next line."));
        foreach (var value in entry.OfType<StringValue>().Select(v => v.ToString()))
        {
            if (value.Contains(';'))
                problems.Add(Problem(true, file, place, entry.Name,
                    $"{label} = {value}: the value contains ';'. The game ignores the text after ';'."));
            if (binary && value.Contains(','))
                problems.Add(Problem(true, file, place, entry.Name,
                    $"{label} = {value}: the value contains ','. The game reads two values in place of one."));
        }
    }

    /// <summary>The checks of the values of a line that the mods change or add.</summary>
    /// <param name="replaced">The base line that the line replaces, or null.</param>
    private static void CheckValues(TargetFile file, SectionRef place, Section section, Entry entry, Entry? replaced,
        List<Diagnostic> problems)
    {
        var label = $"{place.Id} {entry.Name}";
        var number = IsNumberKey(section.Name, entry.Name);
        for (var i = 0; i < entry.Count; i++)
        {
            var value = entry[i];
            var old = replaced != null && i < replaced.Count ? replaced[i] : null;
            if (value is BooleanValue && (old is Int32Value or SingleValue || old == null && number))
                problems.Add(Problem(true, file, place, entry.Name, BinaryValueTypes.BooleanInNumber(label)));
            if (value is SingleValue single && !float.IsFinite((float)single))
                problems.Add(Problem(true, file, place, entry.Name,
                    $"{label} = {RetailText.Of(value)}: the value is not a finite number. The game reads 0."));
            var text = RetailText.Of(value);
            if (number && value is StringValue && SuffixNumber().IsMatch(text))
                problems.Add(Problem(false, file, place, entry.Name,
                    $"{label} = {text}: the game reads this number in another base."));
            if (!IniDocument.TextEncoding.GetString(IniDocument.TextEncoding.GetBytes(text)).Equals(text, StringComparison.Ordinal))
                problems.Add(Problem(false, file, place, entry.Name,
                    $"{label} = {text}: the value has a character outside code page 1252. The game shows another character."));
        }
    }

    private static void CheckCutscene(TargetFile file, ThnTable tree, List<Diagnostic> problems)
    {
        // The export writes the number of each name into a merged cutscene. A whole file of a mod goes into the package
        // as it is.
        var action = file.IsModFile ? "Write its number in the file" : "The export writes its number";
        foreach (var name in ThnRetailNames.Find(tree))
            problems.Add(Diagnostic.Warning(file.Path,
                $"{name}: retail files never use this name. {action}. Check the cutscene in the game."));
    }

    private static void CheckInfocard(int id, string text, List<Diagnostic> problems)
    {
        const string source = "strings.json";
        try
        {
            new XmlDocument().LoadXml(text);
        }
        catch (XmlException e)
        {
            problems.Add(Diagnostic.Error(source,
                $"Infocard {id}: the text is not valid XML ({e.Message}). The game shows no text."));
        }
        if (Regex.Matches(text, "%+").Any(m => m.Length % 2 == 1))
            problems.Add(Diagnostic.Warning(source, $"Infocard {id}: the text has a single %. The game can show wrong " +
                                                    "text. Write %% for one %."));
    }

    private static Diagnostic Problem(bool error, TargetFile file, SectionRef place, string? key, string message) =>
        (error ? Diagnostic.Error(file.Path, message) : Diagnostic.Warning(file.Path, message)) with
        {
            Target = place,
            Key = key,
        };

    private static int Encoding(string text) => IniDocument.TextEncoding.GetByteCount(text);

    /// <summary>True when the engine schema types the key of the section as numbers, for example <c>price</c> of a
    /// <c>[Good]</c>. A key that one schema types as numbers and another as text is not a number key.</summary>
    internal static bool IsNumberKey(string section, string key)
    {
        var types = SchemaCatalog.All
            .Where(s => s.Section.Equals(section, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Schema.Key(key)?.ClrType)
            .Where(t => t != null)
            .ToList();
        return types.Count > 0 && types.All(t => IsNumberType(t!));
    }

    private static bool IsNumberType(Type type)
    {
        if (type.IsArray)
            return IsNumberType(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("ValueRange", StringComparison.Ordinal))
            return IsNumberType(type.GetGenericArguments()[0]);
        return type == typeof(int) || type == typeof(float) || type == typeof(double) || type == typeof(long) ||
               type == typeof(uint) || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) ||
               type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
               type == typeof(Quaternion) || type.Name is "Color3f" or "Color4";
    }
}
