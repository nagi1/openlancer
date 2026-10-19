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
            {
                // A binary base, a new file and a whole file of a mod write each line.
                var binary = file.BaseDocument?.IsBinary == true;
                CheckIni(file.Path, file.BaseDocument == null || binary || file.IsModFile, binary, merged.Sections, diff,
                    problems);
            }
            else if (file is { Kind: TargetFileKind.Cutscene, MergedTree: { } tree })
                CheckCutscene(file, tree, problems);
        }
        foreach (var (id, text) in files.StringIds.Infocards.OrderBy(i => i.Key))
            CheckInfocard(InfocardSource, $"Infocard {id}", text, problems);
        return problems;
    }

    /// <summary>
    /// Runs the checks of the INI files and of the infocards for the save gate (section 9.2 of the M11 design). They run
    /// on a view of the data without and with the mods. A save writes no file of a target. So the checks look only at
    /// the lines that the mods change or add. They also look at each line of a new file or of a whole file of a mod.
    /// </summary>
    /// <param name="binary">True for a path whose file without the mods is binary (BINI).</param>
    /// <param name="wholeFile">True for a path that a whole file of a mod gives.</param>
    /// <param name="infocards">The key and the text of each infocard of the mod.</param>
    /// <param name="infocardSource">The file of the infocards, for the problems.</param>
    internal static IReadOnlyList<Diagnostic> Run(CheckView view, Func<string, bool> binary, Func<string, bool> wholeFile,
        IEnumerable<KeyValuePair<string, string>> infocards, string infocardSource)
    {
        var problems = new List<Diagnostic>();
        foreach (var path in view.ChangedPaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (view.Sections(path, true) is not { } merged)
                continue;
            var without = view.Sections(path, false);
            CheckIni(path, without == null || wholeFile(path), binary(path), merged,
                IniSectionDiff.Compare(without ?? [], merged), problems);
        }
        foreach (var (key, text) in infocards)
            CheckInfocard(infocardSource, $"Infocard '{key}'", text, problems);
        return problems;
    }

    /// <summary>The file name of the infocards of a mod, for the problems of a target build.</summary>
    private const string InfocardSource = "strings.json";

    /// <param name="allLines">True to check each line for the writer, also a line that the mods do not change.</param>
    /// <param name="binary">True when the file without the mods is binary (BINI).</param>
    private static void CheckIni(string path, bool allLines, bool binary, IReadOnlyList<Section> merged, FileDiff diff,
        List<Diagnostic> problems)
    {
        var pairs = diff.Matched.ToDictionary(m => m.Merged);
        var ids = SectionIdentity.Identify(merged);
        for (var index = 0; index < merged.Count; index++)
        {
            var section = merged[index];
            var pair = pairs.GetValueOrDefault(index);
            var place = new SectionRef(path, ids[index]);
            var sectionBytes = Encoding(section.Name);
            if (sectionBytes > MaxLineBytes && (allLines || pair == null))
                problems.Add(Problem(true, path, place, null,
                    $"[{section.Name}]: the section name is {sectionBytes} bytes long. The game writes past the end of its buffer."));
            for (var j = 0; j < section.Count; j++)
            {
                var entry = section[j];
                var replaced = pair != null && pair.BaseOf[j] >= 0 ? diff.BaseSections[pair.Base][pair.BaseOf[j]] : null;
                var modLine = pair == null || pair.BaseOf[j] < 0 || pair.ValueChanged[pair.BaseOf[j]];
                if (allLines || modLine)
                    CheckWritten(path, place, entry, binary, problems);
                if (modLine)
                    CheckValues(path, place, section, entry, replaced, problems);
            }
        }
    }

    /// <summary>The checks of a line that the export writes: its length, its buffers and the characters that split
    /// it.</summary>
    private static void CheckWritten(string path, SectionRef place, Entry entry, bool binary, List<Diagnostic> problems)
    {
        var label = $"{place.Id} {entry.Name}";
        var texts = entry.Select(RetailText.Of).ToList();
        var line = IniLines.Line(entry);
        var longest = texts.Select(Encoding).Prepend(Encoding(entry.Name)).Max();
        if (longest > MaxLineBytes)
            problems.Add(Problem(true, path, place, entry.Name,
                $"{label}: a key or a value is {longest} bytes long. The game writes past the end of its buffer."));
        else if (Encoding(line) > MaxLineBytes)
            problems.Add(Problem(true, path, place, entry.Name,
                $"{label}: the line is {Encoding(line)} bytes long. The game reads only the first 1,023 bytes of " +
                "the line. It reads the rest as the next line."));
        foreach (var value in entry.OfType<StringValue>().Select(v => v.ToString()))
        {
            if (value.Contains(';'))
                problems.Add(Problem(true, path, place, entry.Name,
                    $"{label} = {value}: the value contains ';'. The game ignores the text after ';'."));
            if (binary && value.Contains(','))
                problems.Add(Problem(true, path, place, entry.Name,
                    $"{label} = {value}: the value contains ','. The game reads two values in place of one."));
        }
    }

    /// <summary>The checks of the values of a line that the mods change or add.</summary>
    /// <param name="replaced">The base line that the line replaces, or null.</param>
    private static void CheckValues(string path, SectionRef place, Section section, Entry entry, Entry? replaced,
        List<Diagnostic> problems)
    {
        var label = $"{place.Id} {entry.Name}";
        var number = IsNumberKey(section.Name, entry.Name);
        for (var i = 0; i < entry.Count; i++)
        {
            var value = entry[i];
            var old = replaced != null && i < replaced.Count ? replaced[i] : null;
            if (value is BooleanValue && (old is Int32Value or SingleValue || old == null && number))
                problems.Add(Problem(true, path, place, entry.Name, BinaryValueTypes.BooleanInNumber(label)));
            if (value is SingleValue single && !float.IsFinite((float)single))
                problems.Add(Problem(true, path, place, entry.Name,
                    $"{label} = {RetailText.Of(value)}: the value is not a finite number. The game reads 0."));
            var text = RetailText.Of(value);
            if (number && value is StringValue && SuffixNumber().IsMatch(text))
                problems.Add(Problem(false, path, place, entry.Name,
                    $"{label} = {text}: the game reads this number in another base."));
            if (!IniDocument.TextEncoding.GetString(IniDocument.TextEncoding.GetBytes(text)).Equals(text, StringComparison.Ordinal))
                problems.Add(Problem(false, path, place, entry.Name,
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

    /// <param name="label">The name of the infocard in a message, for example "Infocard 524289".</param>
    private static void CheckInfocard(string source, string label, string text, List<Diagnostic> problems)
    {
        try
        {
            new XmlDocument().LoadXml(text);
        }
        catch (XmlException e)
        {
            problems.Add(Diagnostic.Error(source,
                $"{label}: the text is not valid XML ({e.Message}). The game shows no text."));
        }
        if (Regex.Matches(text, "%+").Any(m => m.Length % 2 == 1))
            problems.Add(Diagnostic.Warning(source, $"{label}: the text has a single %. The game can show wrong " +
                                                    "text. Write %% for one %."));
    }

    private static Diagnostic Problem(bool error, string path, SectionRef place, string? key, string message) =>
        (error ? Diagnostic.Error(path, message) : Diagnostic.Warning(path, message)) with
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
