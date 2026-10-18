using System;
using System.IO;
using ImGuiNET;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Dialogs;

/// <summary>
/// Mod &gt; New mod: asks for the id and the name of a new mod, makes its folder in the mods folder of the workspace and
/// adds it to the end of the mod list of the profile.
/// </summary>
internal sealed class NewModDialog : ModalDialog
{
    private string id = "";
    private string name = "";
    private string? error;
    private string modsFolder = "";
    private string profilePath = "";
    private string? baseFolder;
    private Action<string>? created;

    protected override string Title => "New mod";

    /// <param name="modsFolder">The folder that gets the folder of the mod.</param>
    /// <param name="profilePath">The profile file that gets the mod.</param>
    /// <param name="baseFolder">The base folder of the profile. OpenLancer does not write into it (decision D204).</param>
    /// <param name="created">Gets the id of the new mod.</param>
    public void Show(string modsFolder, string profilePath, string baseFolder, Action<string> created)
    {
        (this.modsFolder, this.profilePath, this.baseFolder, this.created) = (modsFolder, profilePath, baseFolder, created);
        (id, name, error) = ("", "", null);
        Open();
    }

    protected override void Body()
    {
        ImGui.TextWrapped($"The mod gets a folder in {modsFolder}, and goes at the end of the mod list of the profile. " +
                          "It becomes the active mod.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputTextWithHint("Id", "my-mod", ref id, 64);
        ImGui.SetItemTooltip(ModManifest.IdRule);
        ImGui.InputTextWithHint("Name", "My mod", ref name, 128);
        var idProblem = id.Length > 0 && !ModManifest.IsValidId(id) ? ModManifest.IdRule : null;
        Problem(idProblem ?? error);
        ImGui.Spacing();
        if (Button("Create", primary: true, enabled: id.Length > 0 && idProblem == null))
        {
            Create();
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    private void Create()
    {
        // The mod goes into the profile file, so a profile file in the game folder would change a file of the game
        // (decision D214).
        if (baseFolder != null && GameFolders.Overlaps(profilePath, baseFolder))
        {
            error = GameTexts.ProfileInGame;
            return;
        }

        string folder;
        try
        {
            folder = ModWorkspace.CreateMod(modsFolder, id, name, baseFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ErrorText.Of(e);
            return;
        }

        try
        {
            ModWorkspace.AddToProfile(profilePath, folder);
        }
        catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException)
        {
            // The profile does not list the mod, so its new folder goes again.
            Directory.Delete(folder, recursive: true);
            error = e.Message;
            return;
        }

        Close();
        created?.Invoke(id);
    }
}
