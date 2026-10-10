using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Ini;

/// <summary>
/// An INI file as a list of sections, parsed and written in its own format (binary BINI or text).
/// </summary>
/// <remarks>
/// The upstream parser reads the file, so the sections are what the engine sees. Text files lose their
/// comments and layout when they are written again. Only patched files are written again.
/// </remarks>
internal sealed class IniDocument
{
    private static readonly byte[] BiniMagic = "BINI"u8.ToArray();

    /// <summary>
    /// The code page of text INI files. The engine's text parser and writer use it. Each of the 256 byte values
    /// decodes to a character that encodes back to the same byte.
    /// </summary>
    internal static readonly Encoding TextEncoding = CodePages(1252);

    private IniDocument(string path, bool isBinary, List<Section> sections)
    {
        Path = path;
        IsBinary = isBinary;
        Sections = sections;
    }

    public string Path { get; }

    public bool IsBinary { get; }

    public List<Section> Sections { get; }

    public static IniDocument Parse(string path, byte[] data)
    {
        var isBinary = data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(BiniMagic);
        var sections = IniFile.ParseFile(path, new MemoryStream(data, false), preparse: true).ToList();
        return new IniDocument(path, isBinary, sections);
    }

    public static IReadOnlyList<Section> ParseFile(string path) => Parse(path, File.ReadAllBytes(path)).Sections;

    /// <summary>
    /// A deep copy: new sections and new entries. The copy shares the values, because a value cannot change.
    /// </summary>
    public IniDocument Clone() => WithSections(Sections.Select(CopySection));

    /// <summary>A document with the same path and format, and other sections. The sections are not copied.</summary>
    public IniDocument WithSections(IEnumerable<Section> sections) => new(Path, IsBinary, sections.ToList());

    /// <summary>
    /// The text that <see cref="ToBytes"/> writes for a value of a text file. A large integer (see
    /// <see cref="BinaryValueTypes.IsLargeInteger"/>) writes its exact number, which the engine's text parser reads
    /// back as the same float and the same long. Other values write their <c>ToString</c> text. The writer encodes
    /// the text in the code page of text files, so a character outside the code page changes.
    /// </summary>
    public static string TextOf(ValueBase value)
    {
        var text = BinaryValueTypes.IsLargeInteger(value, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : value.ToString();
        return TextEncoding.GetString(TextEncoding.GetBytes(text));
    }

    /// <summary>
    /// Parses text, for example <c>name = 1, 2</c>, as a line of a text file. The engine's text parser reads it.
    /// Returns null when the text does not give exactly one entry.
    /// </summary>
    public static Entry? ParseTextLine(string line)
    {
        var data = TextEncoding.GetBytes("[Line]\n" + line);
        var entries = IniFile.ParseFile("line", new MemoryStream(data, false), preparse: true).SelectMany(s => s).ToList();
        return entries.Count == 1 ? entries[0] : null;
    }

    /// <summary>Writes the file in its own format. The sections do not change.</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        if (IsBinary)
            IniWriter.WriteBini(stream, Sections);
        else
            IniWriter.WriteIni(stream, Sections.Select(TextCopy));
        return stream.ToArray();
    }

    /// <summary>
    /// A copy of a section for the text writer, with the text of <see cref="TextOf"/> for each value. The text writer
    /// writes only the float of a large integer, for example of a hash such as <c>locked_gate = 2314954114</c>. So the
    /// copy holds such a value as its exact text. (For a binary file, <see cref="IniPatcher"/> chooses the type when it
    /// copies the value.)
    /// </summary>
    private static Section TextCopy(Section section) => CopySection(section, (value, line) =>
        BinaryValueTypes.IsLargeInteger(value, out _) ? new StringValue(TextOf(value)) { Entry = line } : value);

    private static Section CopySection(Section section) => CopySection(section, (value, _) => value);

    /// <param name="copyValue">Gives the value for the copy of an entry from the original value and the copy.</param>
    private static Section CopySection(Section section, Func<ValueBase, Entry, ValueBase> copyValue)
    {
        var copy = new Section(section.Name, section.Count) { File = section.File, Line = section.Line };
        foreach (var entry in section)
        {
            var line = new Entry(copy, entry.Name, entry.Count) { Line = entry.Line };
            foreach (var value in entry)
                line.Add(copyValue(value, line));
            copy.Add(line);
        }
        return copy;
    }

    private static Encoding CodePages(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }
}
