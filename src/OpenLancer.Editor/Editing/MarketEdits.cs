using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// The edits of the <c>marketgood</c> lines that sell a good. Format version 1 cannot change a line of a lower layer,
/// so the edits change only the <c>[BaseGood]</c> sections that the active mod adds (see <c>docs/MOD_FORMAT.md</c>,
/// "Markets").
/// </summary>
internal static class MarketEdits
{
    private const string MarketGood = "marketgood";

    /// <summary>
    /// The values of a new line after the good: rank, reputation, minimum, maximum, preserve flag and price multiplier.
    /// A ship package gets the form of the retail ship lines, which sell one ship. The engine sells a ship only when
    /// the minimum or the maximum is not 0.
    /// </summary>
    public static string DefaultValues(string? category) =>
        string.Equals(category, "ship", StringComparison.OrdinalIgnoreCase) ? "0, -1, 1, 1, 0, 1, 1" : "0, -1, 10, 10, 0, 1";

    /// <summary>The text of the values of a line after the good.</summary>
    public static string ValuesText(MarketLine line) => FieldEdit.FormatLine(line.Values.Skip(1).ToList());

    /// <summary>The key of the field of a line in the "Sold at" section.</summary>
    public static FieldKey LineKey(MarketLine line) => new(line.Section, MarketGood, line.Line);

    /// <summary>True when the active mod adds the section of the line, so an edit can change the line.</summary>
    public static bool CanChange(EditSession session, MarketLine line) =>
        session.Get(line.Section) is { IsNew: true, ReadOnlyReason: null };

    /// <summary>
    /// The edit that makes a base sell a good. The line goes into a <c>[BaseGood]</c> of that base that the active mod
    /// adds. Without one, a new section goes into the market file in which the base sells goods of the same category,
    /// else into the first market file of the base, else into the first market file.
    /// </summary>
    /// <param name="values">The values after the good (see <see cref="DefaultValues"/>).</param>
    /// <exception cref="InvalidOperationException">A market file already sells the good at the base, or the edit view
    /// has no market file.</exception>
    public static EditCommand AddToBase(EditSession session, string good, string baseNickname, string values)
    {
        if (session.MarketLines(good).Any(l => Is(l.Base, baseNickname)))
            throw new InvalidOperationException($"The base {baseNickname} already sells {good}.");
        var line = $"{good}, {values}";
        var markets = session.List(ItemFileKind.Markets);
        foreach (var market in markets)
        {
            if (session.Get(market) is { IsNew: true, ReadOnlyReason: null } view && Is(Base(view), baseNickname))
                return EditCommand.SetLinesFromText(market, MarketGood, [.. Lines(view), line]);
        }

        var path = MarketFile(session, markets, good, baseNickname);
        // A new section goes at the end of its file, after the other [BaseGood] sections.
        var ordinal = markets.Count(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && Is(m.Id.Type, "BaseGood"));
        var added = new SectionRef(path, new SectionIdentity("BaseGood", null, null, ordinal));
        return EditCommand.Group($"Sell {good} at {baseNickname}",
        [
            EditCommand.AddSection(path, "BaseGood", [new EntryView("base", [new StringValue(baseNickname)])]),
            EditCommand.SetLinesFromText(added, MarketGood, [line]),
        ]);
    }

    /// <summary>The edit that sets the values of a line after the good, or removes the line when
    /// <paramref name="values"/> is null. A section without lines goes.</summary>
    /// <exception cref="InvalidOperationException">The active mod does not add the section of the line.</exception>
    public static EditCommand SetLine(EditSession session, MarketLine line, string? values)
    {
        var view = session.Get(line.Section);
        if (view is not { IsNew: true, ReadOnlyReason: null })
            throw new InvalidOperationException(
                "Format version 1 cannot change a market line of a lower layer. Use a whole-file override for that.");
        var lines = Lines(view);
        if (values == null)
            lines.RemoveAt(line.Line);
        else
            lines[line.Line] = $"{line.Values[0]}, {values}";
        return lines.Count == 0
            ? EditCommand.DeleteSection(line.Section)
            : EditCommand.SetLinesFromText(line.Section, MarketGood, lines);
    }

    private static List<string> Lines(SectionView view) =>
        view.Lines(MarketGood).Select(l => FieldEdit.FormatLine(l.Values)).ToList();

    private static string MarketFile(EditSession session, IReadOnlyList<SectionRef> markets, string good, string baseNickname)
    {
        var category = Category(session, good);
        string? withBase = null;
        foreach (var market in markets)
        {
            if (session.Get(market) is not { } view || !Is(Base(view), baseNickname))
                continue;
            withBase ??= market.Path;
            if (category != null && view.Lines(MarketGood).Any(l => l.Values.Count > 0 &&
                                                                   Is(Category(session, l.Values[0].ToString()), category)))
                return market.Path;
        }
        return withBase ?? markets.FirstOrDefault()?.Path ??
            throw new InvalidOperationException("The edit view has no market file.");
    }

    /// <summary>The category of a good, for example <c>equipment</c> or <c>ship</c>, or null.</summary>
    public static string? Category(EditSession session, string good) =>
        session.Find(ItemFileKind.Goods, good) is { } section ? First(session.Get(section), "category") : null;

    private static string? Base(SectionView view) => First(view, "base");

    private static string? First(SectionView? view, string name) =>
        view?.Lines(name) is [{ Values: [var value, ..] }, ..] ? value.ToString() : null;

    private static bool Is(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
