using System;
using System.Text.Json;

namespace OpenLancer.Data.Editing.Fields;

/// <summary>
/// The text of an info icon (AGENTS.md UI rule 10, section 11.2 of the M6a design): what a high-level item is, and what it
/// writes to the game files. The rule data holds the texts next to each item (decision D539).
/// </summary>
/// <param name="Title">The title of the popup, for example "Spawn wave".</param>
/// <param name="WhatItIs">What the item is, in one or two sentences.</param>
/// <param name="WhatItWrites">What the item writes to the game files.</param>
/// <param name="Example">A retail example, or null. The editor finds it in the data of the user at run time, so the rule
/// data holds no retail name.</param>
public sealed record HelpText(string Title, string WhatItIs, string WhatItWrites, string? Example = null)
{
    /// <summary>The first sentence of <see cref="WhatItIs"/>, for the hover tooltip of the icon.</summary>
    public string FirstSentence => FirstSentenceOf(WhatItIs);

    /// <summary>The text up to and with the first sentence end: a period, a question mark or an exclamation mark
    /// before a space or the end of the text.</summary>
    public static string FirstSentenceOf(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '?' or '!' && (i + 1 == text.Length || text[i + 1] == ' '))
                return text[..(i + 1)];
        }
        return text;
    }

    /// <summary>Reads a help object of the rule data: <c>title</c>, <c>whatItIs</c>, <c>whatItWrites</c> and an optional
    /// <c>example</c>.</summary>
    /// <exception cref="FormatException">A text is missing.</exception>
    internal static HelpText Read(JsonElement e) => new(
        Text(e, "title"), Text(e, "whatItIs"), Text(e, "whatItWrites"),
        e.TryGetProperty("example", out var example) ? example.GetString() : null);

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"A help text has no '{name}'.");
}
