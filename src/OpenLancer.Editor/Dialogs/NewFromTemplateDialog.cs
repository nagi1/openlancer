using System;
using System.IO;
using ImGuiNET;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor.Dialogs;

/// <summary>
/// New from template: asks for the nickname of a new item, and shows the sections that the copy adds (see
/// <see cref="ItemTemplates"/>). The plan is made again for each change of the nickname.
/// </summary>
internal sealed class NewFromTemplateDialog : ModalDialog
{
    private EditSession? session;
    private SectionRef? template;
    private Action<TemplatePlan>? create;
    private string nickname = "";
    private string? plannedNickname;
    private TemplatePlan? plan;

    protected override string Title => "New from template";

    /// <param name="template">The ship or the equipment section to copy.</param>
    /// <param name="create">Gets the plan when the user clicks Create.</param>
    public void Show(EditSession session, SectionRef template, Action<TemplatePlan> create)
    {
        (this.session, this.template, this.create) = (session, template, create);
        (nickname, plannedNickname, plan) = ("", null, null);
        Open();
    }

    protected override void Body()
    {
        ImGui.TextWrapped($"Copy {template!.Id.Key} with the sections that belong to it. Each copy goes into the file of " +
                          "the section that it copies. The copies are in full, so they do not change when a lower mod " +
                          "changes the template.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputTextWithHint("New nickname", template.Id.Key + "_copy", ref nickname, 64);
        if (nickname != plannedNickname)
        {
            plannedNickname = nickname;
            plan = nickname.Trim().Length == 0 ? null : ItemTemplates.Plan(session!, template, nickname.Trim());
        }

        if (plan != null)
        {
            if (plan.Problems.Count > 0)
            {
                foreach (var problem in plan.Problems)
                {
                    Problem(problem.Message);
                }
            }
            else
            {
                ImGui.TextDisabled("The copy adds:");
                foreach (var copy in plan.Copies)
                {
                    var children = copy.Children == 0 ? "" : $" and {copy.Children} child sections";
                    ImGui.BulletText($"{copy.Copy.Id}{children} in {Path.GetFileName(copy.Copy.Path)}");
                }
            }
        }

        ImGui.Spacing();
        if (Button("Create", primary: true, enabled: plan is { Problems.Count: 0 }))
        {
            Close();
            create?.Invoke(plan!);
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }
}
