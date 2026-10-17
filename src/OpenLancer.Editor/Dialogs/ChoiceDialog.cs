using System;
using System.Collections.Generic;
using ImGuiNET;

namespace OpenLancer.Editor.Dialogs;

/// <summary>One button of a <see cref="ChoiceDialog"/>.</summary>
/// <param name="Action">What the button does. The dialog closes first.</param>
internal sealed record Choice(string Label, Action Action);

/// <summary>
/// A message with buttons, for example Save, Discard and Cancel. The first button is the default button. Escape and the
/// close button of the title bar run the cancel action.
/// </summary>
internal sealed class ChoiceDialog : ModalDialog
{
    private string title = "##choice";
    private string message = "";
    private IReadOnlyList<Choice> choices = [];
    private Action? cancel;

    protected override string Title => title;

    /// <param name="title">The title. Two dialogs that can show at the same time need different titles.</param>
    /// <param name="cancel">What Escape and the close button of the title bar do, or null for nothing.</param>
    public void Show(string title, string message, IReadOnlyList<Choice> choices, Action? cancel = null)
    {
        this.title = title;
        this.message = message;
        this.choices = choices;
        this.cancel = cancel;
        Open();
    }

    protected override void Body()
    {
        ImGui.TextWrapped(message);
        ImGui.Spacing();
        for (var i = 0; i < choices.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            if (Button(choices[i].Label, primary: i == 0))
            {
                Close();
                choices[i].Action();
                return;
            }
        }
    }

    protected override void Cancelled() => cancel?.Invoke();
}
