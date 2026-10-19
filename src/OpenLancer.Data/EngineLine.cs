using System;
using System.Globalization;
using System.Text.RegularExpressions;
using LibreLancer;
using OpenLancer.Data.IO;

namespace OpenLancer.Data;

/// <summary>
/// One line of FLLog, split into its parts: the severity, the component, the message, and the place in an INI file
/// that the message names (decision D500 of the console filter design).
/// </summary>
/// <remarks>
/// <para>FLLog writes "[Warning: 12:34:56] Ini: message". The time separator depends on the culture. A message can have
/// more lines, for example an exception with its stack trace.</para>
/// <para>The parser knows the places that the engine writes at the end of a message:</para>
/// <list type="bullet">
/// <item>The four forms of upstream <c>IniDiagnostic.FormatLine</c>: " at section S: file, line N",
/// " in section S: file (line not available)", " at file, line N" and " in file (line not available)".</item>
/// <item>The float form of <c>StringValue</c> and <c>LancerStringValue</c>: " in section S: file:N".</item>
/// <item>The two spaces before the place of "Entry without object 'k'" and of <c>IniDiagnostic.Warn</c>, and the
/// section in "Could not add section [S] to parent".</item>
/// </list>
/// <para>A line of a component without a file, for example "Ship: Unrecognised hp_type X in s", has no place.
/// <see cref="EngineLineRules"/> gives its subject.</para>
/// </remarks>
public sealed partial record EngineLine
{
    private EngineLine(LogSeverity severity, string? component, string message)
    {
        Severity = severity;
        Component = component;
        Message = message;
        var place = message.Contains('\n') ? Match.Empty : Place().Match(message);
        if (place.Success)
        {
            Core = place.Groups["core"].Value.TrimEnd();
            File = place.Groups["file"].Value;
            Line = place.Groups["line"].Success
                ? int.Parse(place.Groups["line"].Value, CultureInfo.InvariantCulture)
                : null;
            Section = place.Groups["section"].Success ? place.Groups["section"].Value : null;
        }
        else
        {
            Core = message;
        }

        if (Section == null && ChildFailure().Match(Core) is { Success: true } child)
            Section = child.Groups["section"].Value;
        var path = File == null ? "" : (VfsPath.Normalize(File) ?? File).ToLowerInvariant();
        Key = $"{Severity}|{Component}|{Core}|{Section?.ToLowerInvariant()}|{path}";
    }

    public LogSeverity Severity { get; }

    /// <summary>The component of the line, for example "Ini", or null for a line without one.</summary>
    public string? Component { get; }

    /// <summary>The message after the component, with its place.</summary>
    public string Message { get; }

    /// <summary>The message without its place, for example "Unknown entry x".</summary>
    public string Core { get; }

    /// <summary>The file of the place as the engine wrote it, for example <c>EXE\..\data\ships\shiparch.ini</c>, or
    /// null.</summary>
    public string? File { get; }

    /// <summary>The line in <see cref="File"/>, or null when the engine knows no line, for example in a binary
    /// file.</summary>
    public int? Line { get; }

    /// <summary>The type of the section of the place, for example "Ship", or null.</summary>
    public string? Section { get; }

    /// <summary>
    /// What tells two lines of one problem apart from the lines of other problems: the severity, the component, the
    /// message without its place, the section type and the install path of the file without letter case. It has no
    /// time and no line number. So a load of the same data gives the same key, also when a patch moves the lines of a
    /// file.
    /// </summary>
    public string Key { get; }

    /// <summary>The source of a Console entry: the component, or "Engine" for a line without one.</summary>
    public string Source => Component ?? NoComponent;

    /// <summary>The source of a line without a component.</summary>
    public const string NoComponent = "Engine";

    /// <summary>
    /// The line without its time and its line number, for example "Warning Ini: Unknown entry x in section Ship: a.ini".
    /// Two loads of the same data give the same text.
    /// </summary>
    public string Normalized
    {
        get
        {
            var start = Component == null ? $"{Severity} " : $"{Severity} {Component}: ";
            if (File == null)
                return start + Core;
            return Section == null || ChildFailure().IsMatch(Core)
                ? $"{start}{Core} in {File}"
                : $"{start}{Core} in section {Section}: {File}";
        }
    }

    /// <summary>
    /// Reads a line that FLLog gives to its listener. A line without the "[Severity: time] " prefix keeps its full
    /// text as the message, without a component.
    /// </summary>
    /// <param name="severity">The severity that FLLog gives with the line.</param>
    public static EngineLine Parse(string line, LogSeverity severity)
    {
        var prefix = Prefix().Match(line);
        if (prefix.Success)
            return new EngineLine(severity, prefix.Groups["component"].Value, prefix.Groups["message"].Value);
        var time = TimeOnly().Match(line);
        return new EngineLine(severity, null, time.Success ? time.Groups["message"].Value : line);
    }

    /// <summary>
    /// Reads a line of a log text, for example of the game process of Play. The severity comes from the prefix. Null
    /// for a line without the prefix or without a component, or with an unknown severity.
    /// </summary>
    public static EngineLine? Parse(string line)
    {
        var prefix = Prefix().Match(line);
        if (!prefix.Success || !Enum.TryParse<LogSeverity>(prefix.Groups["severity"].Value, out var severity) ||
            !Enum.IsDefined(severity))
            return null;
        return new EngineLine(severity, prefix.Groups["component"].Value, prefix.Groups["message"].Value);
    }

    // FLLog.Write: "[{severity}: {time}] {component}: {message}".
    [GeneratedRegex(@"^\[(?<severity>\w+): [^\]]*\] (?<component>[^:\r\n]+): (?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex Prefix();

    // The place at the end of a message. The core is the shortest text before " at" or " in", so a file name with a
    // space stays in the file.
    // The float form ":N" comes only after a section.
    [GeneratedRegex(@"^(?<core>.*?)\s+(?:at|in)(?: section (?<section>[^:]*):)? (?<file>\S.*?)" +
                    @"(?:, line (?<line>\d+)| \(line not available\)|(?(section):(?<line>\d+)|(?!)))$")]
    private static partial Regex Place();

    // A line of FLLog without a component.
    [GeneratedRegex(@"^\[\w+: [^\]]*\] (?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex TimeOnly();

    [GeneratedRegex(@"^Could not add section \[(?<section>[^\]]*)\] to parent$")]
    private static partial Regex ChildFailure();
}
