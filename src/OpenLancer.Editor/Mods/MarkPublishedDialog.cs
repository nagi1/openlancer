using System;
using ImGuiNET;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// "Mark as published..." of the History tab (section 10.6 of the M11c design): the version, the page link and OK. OK
/// writes the mark into the history. A link that differs from the link of the publish settings goes into the draft as
/// an unapplied change with the blue mark (finding R14). The user then clicks Apply.
/// </summary>
internal sealed class MarkPublishedDialog(EditorWindow window, ModsWindow mods) : ModalDialog
{
    private ModRow? row;
    private string version = "";
    private string link = "";

    protected override string Title => "Mark as published###markpublished";

    public void Show(ModRow mod, string markedVersion)
    {
        row = mod;
        version = markedVersion;
        link = mods.Draft is { } draft && Data.Games.GameFolders.Same(draft.ModFolder, mod.Folder)
            ? draft.Values.Settings.PageUrl
            : mod.Settings.PageUrl;
        Open();
    }

    protected override void Body()
    {
        if (row == null)
        {
            Close();
            return;
        }

        ImGui.TextWrapped($"Mark version {version} of {row.Name} as published on Nexus? After the mark, an export of " +
                          $"version {version} with other game files asks for a new version.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputTextWithHint("Page link", "https://www.nexusmods.com/...", ref link, 512);
        ImGui.SetItemTooltip("The link of the page of the mod on Nexus. It can be empty.");
        var problem = link.Trim().Length > 0 && !PublishSettings.IsHttps(link.Trim()) ? PublishMessages.PageLink : null;
        Problem(problem);
        ImGui.Spacing();
        if (Button("OK", primary: true, enabled: problem == null))
        {
            Close();
            Mark(row, link.Trim());
            return;
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    private void Mark(ModRow mod, string page)
    {
        mods.Mark(mod, version, page);
        if (mods.Draft is { } draft && Data.Games.GameFolders.Same(draft.ModFolder, mod.Folder) && draft.TakePageLink(page))
        {
            window.Log.Add(ConsoleSeverity.Info, ModsWindow.Source,
                $"The page link of {mod.Name} is in the publish settings now. Click Apply to write it.");
            mods.GoTo(PublishField.PageUrl);
        }
    }
}
