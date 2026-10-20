using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenLancer.Tests;

/// <summary>
/// The STE lint of the texts of OpenLancer (AGENTS.md, the rules of Simplified Technical English): no semicolon, no
/// contraction, no word that STE replaces with a short common word, no marketing word, and no sentence above a word
/// limit. The help texts of the missions (milestone M6a) and the texts of the agent tools (milestone MCP1) use it.
/// </summary>
internal static partial class SteLint
{
    /// <summary>The most words of a sentence of a description.</summary>
    public const int DescriptionWords = 25;

    /// <summary>The most words of a sentence of an instruction.</summary>
    public const int InstructionWords = 20;

    /// <summary>The words that STE replaces with a short common word, and the marketing words (AGENTS.md STE rules 6
    /// and 8).</summary>
    private static readonly string[] Banned =
    [
        "utilize", "leverage", "ensure", "prior to", "regarding", "obtain", "demonstrate", "additionally", "initiate",
        "seamless", "robust", "powerful", "effortless",
    ];

    /// <summary>The STE problems of a text: semicolons, contractions, banned words and sentences of more than
    /// <paramref name="words"/> words.</summary>
    public static List<string> Problems(string text, int words = DescriptionWords)
    {
        var problems = new List<string>();
        if (text.Contains(';'))
            problems.Add("a semicolon");
        if (Contraction().Match(text) is { Success: true } contraction)
            problems.Add($"the contraction '{contraction.Value}'");
        problems.AddRange(Banned.Where(w => Regex.IsMatch(text, $@"\b{w}\b", RegexOptions.IgnoreCase))
            .Select(w => $"the word '{w}'"));
        foreach (var sentence in SentenceEnd().Split(text))
        {
            var count = sentence.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length;
            if (count > words)
                problems.Add($"a sentence of {count} words");
        }
        return problems;
    }

    [GeneratedRegex(@"\b\w+n't\b|\w'(re|ve|ll|m|d)\b|\b(it|that|there|what|here|who|let)'s\b", RegexOptions.IgnoreCase)]
    private static partial Regex Contraction();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceEnd();
}
