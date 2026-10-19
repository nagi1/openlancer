using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Publishing;

/// <summary>The part of a version that "Increase version" changes.</summary>
public enum VersionPart
{
    /// <summary>Fixes only. Players can install the version over the version before.</summary>
    Patch,

    /// <summary>New content. Old saves still load.</summary>
    Minor,

    /// <summary>Changes that break old saves or other mods.</summary>
    Major,
}

/// <summary>
/// The version of a mod (decisions D563 and D585 of the M11c design): Semantic Versioning 2.0 without build metadata.
/// The form is <c>MAJOR.MINOR.PATCH</c>, with an optional pre-release such as <c>1.0.0-beta.1</c>.
/// </summary>
/// <remarks>
/// <para>A numeric part has no leading zero and at most 9 digits. A pre-release has dot-separated parts of lowercase
/// letters, digits and <c>-</c>, because Windows uses the same file name for uppercase and lowercase letters.</para>
/// <para>The mod loader accepts any version text. Only the export needs a version that is safe in a path
/// (<see cref="IsSafe"/>). A safe version that does not follow the rule gives a warning.</para>
/// </remarks>
public sealed class ModVersion : IComparable<ModVersion>
{
    /// <summary>The longest safe version (decision D585).</summary>
    public const int MaxSafeLength = 64;

    private const int MaxDigits = 9;

    private ModVersion(int major, int minor, int patch, IReadOnlyList<string> preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The parts of the pre-release, or an empty list for a release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>Compares two version texts as the history does: without letter case (decision D585).</summary>
    public static StringComparer TextComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>The version, or null when the text does not follow the rule.</summary>
    public static ModVersion? Parse(string? text) => TryParse(text, out var version) ? version : null;

    public static bool TryParse(string? text, out ModVersion version)
    {
        version = null!;
        if (string.IsNullOrEmpty(text))
            return false;
        var dash = text.IndexOf('-');
        var core = (dash < 0 ? text : text[..dash]).Split('.');
        if (core.Length != 3 || !Number(core[0], out var major) || !Number(core[1], out var minor) ||
            !Number(core[2], out var patch))
            return false;
        IReadOnlyList<string> pre = [];
        if (dash >= 0)
        {
            var parts = text[(dash + 1)..].Split('.');
            if (parts.Any(p => !PreReleasePart(p)))
                return false;
            pre = parts;
        }
        version = new ModVersion(major, minor, patch, pre);
        return true;
    }

    /// <summary>
    /// True when the version is safe in a file name and a folder name (decision D585): 1 to 64 characters from
    /// <c>0-9</c>, <c>A-Z</c>, <c>a-z</c>, <c>.</c> and <c>-</c>. It starts with a letter or a digit, and it has no
    /// <c>..</c>. An old version such as <c>1.0</c> is safe.
    /// </summary>
    /// <remarks>Windows also refuses a final <c>.</c>, which it removes from a folder name, and a device name such as
    /// <c>con</c> or <c>nul.1.0</c>. These names open a device and not a folder.</remarks>
    public static bool IsSafe(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= MaxSafeLength && char.IsAsciiLetterOrDigit(text[0]) &&
        text[^1] != '.' && !text.Contains("..", StringComparison.Ordinal) &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') && !IsDeviceName(text.Split('.')[0]);

    /// <summary>True for a name that Windows keeps for a device, without letter case: CON, PRN, AUX, NUL, and COM or
    /// LPT with a digit.</summary>
    private static bool IsDeviceName(string name) =>
        name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" ||
        name.Length == 4 && char.IsAsciiDigit(name[3]) && name[..3].ToUpperInvariant() is "COM" or "LPT";

    /// <summary>The error of a version that the export cannot use, or null when the version is safe.</summary>
    public static string? SafeProblem(string? text) => IsSafe(text) ? null : PublishMessages.VersionUnsafe(text ?? "");

    /// <summary>
    /// The problem of a version for the publish page, or null: the error of an unsafe version, or the warning of a
    /// version that does not follow the rule.
    /// </summary>
    public static (DiagnosticSeverity Severity, string Message)? Problem(string? text)
    {
        if (SafeProblem(text) is { } unsafeText)
            return (DiagnosticSeverity.Error, unsafeText);
        if (TryParse(text, out _))
            return null;
        if (TryParse(text!.ToLowerInvariant(), out _))
            return (DiagnosticSeverity.Warning, PublishMessages.VersionLetterCase);
        return (DiagnosticSeverity.Warning, PublishMessages.VersionRule);
    }

    /// <summary>The order of Semantic Versioning: <c>1.0.0-beta.1</c> &lt; <c>1.0.0</c> &lt; <c>1.0.1</c>.</summary>
    public int CompareTo(ModVersion? other)
    {
        if (other == null)
            return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
            return core;
        if (IsPreRelease != other.IsPreRelease)
            return IsPreRelease ? -1 : 1;
        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var part = ComparePart(PreRelease[i], other.PreRelease[i]);
            if (part != 0)
                return part;
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    /// <summary>
    /// The next version of a part. A patch increase of a pre-release gives its release. A minor or major increase of a
    /// pre-release of the first version of that part gives that release too.
    /// </summary>
    public ModVersion Increase(VersionPart part) => part switch
    {
        VersionPart.Patch => IsPreRelease ? Release() : new ModVersion(Major, Minor, Patch + 1, []),
        VersionPart.Minor => IsPreRelease && Patch == 0 ? Release() : new ModVersion(Major, Minor + 1, 0, []),
        _ => IsPreRelease && Patch == 0 && Minor == 0 ? Release() : new ModVersion(Major + 1, 0, 0, []),
    };

    /// <summary>
    /// The next version of a part that is not in <paramref name="taken"/>, for example the versions of the publish
    /// history. From <c>1.0.0</c> with <c>1.0.1</c> in the history, a patch increase gives <c>1.0.2</c>. Null when the
    /// current version does not follow the rule.
    /// </summary>
    public static string? Next(string current, VersionPart part, IEnumerable<string> taken)
    {
        if (!TryParse(current, out var version))
            return null;
        var used = new HashSet<string>(taken, TextComparer);
        var next = version.Increase(part);
        while (used.Contains(next.ToString()))
            next = next.Increase(part);
        return next.ToString();
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPreRelease ? "-" + string.Join('.', PreRelease) : "");

    /// <summary>Compares two version texts by precedence. A text that does not follow the rule is lower than each
    /// version that follows it, and two such texts compare as text.</summary>
    public static int Compare(string a, string b)
    {
        var (left, right) = (Parse(a), Parse(b));
        if (left != null && right != null)
            return left.CompareTo(right);
        if (left != null || right != null)
            return left != null ? 1 : -1;
        return TextComparer.Compare(a, b);
    }

    private ModVersion Release() => new(Major, Minor, Patch, []);

    private static bool Number(string text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > MaxDigits || !text.All(char.IsAsciiDigit) || text.Length > 1 && text[0] == '0')
            return false;
        value = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool PreReleasePart(string part)
    {
        if (part.Length == 0 || !part.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'z' || c == '-'))
            return false;
        return !part.All(char.IsAsciiDigit) || Number(part, out _);
    }

    private static int ComparePart(string a, string b)
    {
        var (numberA, numberB) = (a.All(char.IsAsciiDigit), b.All(char.IsAsciiDigit));
        if (numberA && numberB)
            return int.Parse(a, System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(int.Parse(b, System.Globalization.CultureInfo.InvariantCulture));
        if (numberA != numberB)
            return numberA ? -1 : 1;
        return string.CompareOrdinal(a, b);
    }
}
