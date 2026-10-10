using System;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// One edit of a bound field, from the activation of its widget to its deactivation. The edit is one transaction, so
/// it is one entry of the edit history. Each change of the text shows at once in the edit view.
/// </summary>
/// <remarks>
/// A text that equals the value at the start puts back the edit view as it was, so the edit removes no pin and adds
/// no entry. A text that is not valid keeps the last valid state and sets <see cref="Error"/>.
/// </remarks>
internal sealed class FieldGesture : IDisposable
{
    private readonly EditTransaction transaction;

    /// <exception cref="InvalidOperationException">The session takes no edits, or a transaction is open.</exception>
    public FieldGesture(EditSession session, FieldBinding binding)
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

    /// <summary>Why the last text cannot be the value, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Shows the text of the field in the edit view, or sets <see cref="Error"/>.</summary>
    public void Change(string text)
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
    /// <param name="keep">False to put back the state before the edit, for example after Escape (see
    /// <see cref="FieldEditRouter"/>).</param>
    public string? End(bool keep)
    {
        if (!keep || Error != null)
        {
            transaction.Cancel();
            return keep ? Error : null;
        }
        transaction.Commit();
        return null;
    }

    public void Dispose() => transaction.Cancel();
}
