using System;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// One edit of a text field, from the activation of its widget to its deactivation (see <see cref="FieldEditRouter"/>).
/// The edit becomes at most one entry of the edit history.
/// </summary>
internal abstract class FieldGesture : IDisposable
{
    /// <param name="startText">The text of the field at the start of the edit.</param>
    protected FieldGesture(FieldKey key, string startText)
    {
        Key = key;
        StartText = startText;
    }

    /// <summary>The key that the field edits.</summary>
    public FieldKey Key { get; }

    /// <summary>The text of the field at the start of the edit.</summary>
    public string StartText { get; }

    /// <summary>Why the last text cannot be the value, or null.</summary>
    public string? Error { get; protected set; }

    /// <summary>Takes the new text of the field.</summary>
    public abstract void Change(string text);

    /// <summary>
    /// Ends the edit. Returns the error when the edit keeps nothing because its last text is not valid, else null.
    /// </summary>
    /// <param name="keep">False to discard the edit, for example after Escape.</param>
    /// <param name="interactive">False when the editor cannot ask a question, for example when the window closes.
    /// Then the edit takes the choice that changes the least.</param>
    public abstract string? End(bool keep, bool interactive);

    /// <summary>Discards the edit when it did not end.</summary>
    public abstract void Dispose();
}

/// <summary>
/// The edit of the lines of a bound key: one transaction. Each change of the text shows at once in the edit view.
/// </summary>
/// <remarks>
/// A text that equals the value at the start puts back the edit view as it was, so the edit removes no pin and adds
/// no entry. A text that is not valid keeps the last valid state and sets <see cref="FieldGesture.Error"/>.
/// </remarks>
internal sealed class LineGesture : FieldGesture
{
    private readonly EditTransaction transaction;

    /// <exception cref="InvalidOperationException">The session takes no edits, or a transaction is open.</exception>
    public LineGesture(EditSession session, FieldBinding binding) : base(binding.FieldKey, binding.Text)
    {
        Binding = binding;
        StartSection = session.Get(binding.Section);
        transaction = session.Begin($"Set {binding.Name}");
    }

    /// <summary>The key as it was at the start of the edit.</summary>
    public FieldBinding Binding { get; }

    /// <summary>The section of the key as it was at the start of the edit, or null when the edit view did not have
    /// it.</summary>
    public SectionView? StartSection { get; }

    /// <summary>Shows the text of the field in the edit view, or sets <see cref="FieldGesture.Error"/>.</summary>
    public override void Change(string text)
    {
        var parse = FieldEdit.Parse(Binding.Key, text);
        if (parse.Lines == null)
        {
            Error = parse.Error;
            return;
        }
        try
        {
            transaction.Apply(Binding.Holds(parse.Lines) ? EditCommand.Group("", []) : Binding.EditLines(parse.Lines));
            Error = null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // The transaction is back at the state before the edit.
            Error = e.Message;
        }
    }

    /// <summary>
    /// Ends the edit. When the last text is valid, the edit becomes one entry of the history. Otherwise the edit view
    /// goes back to its state before the edit, and the result is the error.
    /// </summary>
    public override string? End(bool keep, bool interactive)
    {
        if (!keep || Error != null)
        {
            transaction.Cancel();
            return keep ? Error : null;
        }
        transaction.Commit();
        return null;
    }

    public override void Dispose() => transaction.Cancel();
}

/// <summary>
/// The edit of a field whose command reads the edit view when it is made, for example the text of a name or a market
/// line. The edit changes nothing while the field is active. When the field ends with a text that differs from its
/// start text, the edit runs its commit action one time.
/// </summary>
/// <remarks>
/// A text edit refuses to run on a newer state than the state that it was made from (see
/// <see cref="ItemEdits.SetText"/>). So the command is made only at the end.
/// </remarks>
/// <param name="commit">Runs the edit with the last text. Its second argument is the interactive flag of
/// <see cref="FieldGesture.End"/>. It returns why the text cannot be the value, or null.</param>
internal sealed class CommitGesture(FieldKey key, string startText, Func<string, bool, string?> commit)
    : FieldGesture(key, startText)
{
    private string text = startText;
    private bool ended;

    public override void Change(string text) => this.text = text;

    public override string? End(bool keep, bool interactive)
    {
        if (ended)
        {
            return null;
        }

        ended = true;
        return keep && !string.Equals(text, StartText, StringComparison.Ordinal) ? commit(text, interactive) : null;
    }

    public override void Dispose() => ended = true;
}
