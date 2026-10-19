namespace OpenLancer.Data;

/// <summary>Counts in texts for a user.</summary>
public static class Plural
{
    /// <summary>A number with a noun that takes "s" in the plural, for example "1 error" or "3 errors".</summary>
    public static string Count(int number, string noun) => number == 1 ? $"1 {noun}" : $"{number} {noun}s";
}
