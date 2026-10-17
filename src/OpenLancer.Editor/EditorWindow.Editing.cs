using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor;

/// <summary>The edits of the active mod: the Edit and Mod menus, their keys and their dialogs, and the save prompt
/// when the window closes.</summary>
internal sealed partial class EditorWindow : IClosePromptHost
{
    private readonly NewModDialog newModDialog = new();
    private readonly NewFromTemplateDialog templateDialog = new();

    // The close prompt has its own dialog. When another dialog replaces it, its cancel action still runs, so the next
    // close request asks again.
    private readonly ChoiceDialog closeDialog = new();
    private readonly ClosePrompt closePrompt;

    void IClosePromptHost.EndEdits() => EndOpenEdits();

    string? IClosePromptHost.UnsavedMod =>
        Session is { Edits: { IsDirty: true, ActiveMod: { } mod } } ? mod.Manifest.Name : null;

    void IClosePromptHost.Save(Action saved) => Edits.Save(saved);

    void IClosePromptHost.DiscardRecovery() => Edits.DiscardRecovery();

    void IClosePromptHost.Close() => Exit();

    void IClosePromptHost.Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel) =>
        closeDialog.Show(title, message, choices, cancel);

    /// <summary>
    /// The section of the edit view that an item shows: the <c>[Ship]</c> of a ship, the section of equipment, the
    /// <c>[system]</c> of a system in <c>universe.ini</c>, the section of an object, a zone or a light of the open
    /// system, or the section itself. Null for other items, and when no section or more than one section has the
    /// nickname.
    /// </summary>
    public SectionRef? SectionOf(object? item) => item switch
    {
        null => null,
        SectionRef section => section,
        Ship ship => Session?.Edits.Find(ItemFileKind.Ships, ship.Nickname),
        Equipment equipment => Session?.Edits.Find(ItemFileKind.Equipment, equipment.Nickname),
        StarSystem system => Session?.Edits.SystemSection(system.Nickname),
        _ => Scene.SectionOf(item),
    };

    /// <summary>True when New from template can copy the section: a ship or equipment, with a session that takes
    /// edits.</summary>
    public bool CanCopy(SectionRef? section) =>
        section?.Id.Key is { } key && Session is { CanEdit: true } session &&
        (section.Id.Type.Equals("Ship", StringComparison.OrdinalIgnoreCase) &&
         section.Equals(session.Edits.Find(ItemFileKind.Ships, key)) ||
         section.Equals(session.Edits.Find(ItemFileKind.Equipment, key)));

    /// <summary>True when Revert can put back the section as the lower layer has it.</summary>
    public bool CanRevert(SectionRef? section) =>
        section != null && Session is { CanEdit: true } session &&
        session.Edits.Get(section) is { IsChanged: true, IsNew: false, ReadOnlyReason: null } &&
        FieldBinding.RevertLock(session.Edits, section) == null;

    /// <summary>The tooltip of Revert: what it does, or why it cannot run for the section.</summary>
    public string RevertTooltip(SectionRef? section) =>
        (section == null || Session is not { } session ? null : FieldBinding.RevertLock(session.Edits, section)) ??
        "Puts back the section as the base data and the mods before the active mod have it.";

    /// <summary>Asks for the nickname of a copy of a ship or of equipment, and adds the copy. The copy is then
    /// selected.</summary>
    public void NewFromTemplate(SectionRef template)
    {
        if (Session is not { } session)
        {
            return;
        }

        templateDialog.Show(session.Edits, template, plan =>
        {
            if (Edits.Execute(() => ItemTemplates.Build(plan)))
            {
                Selection = plan.Copies.First(c => c.Source.Equals(plan.Template)).Copy;
                Log.Add(ConsoleSeverity.Info, EditController.Source,
                    $"Added {plan.Nickname} from {plan.Template.Id.Key}: " +
                    string.Join(", ", plan.Copies.Select(c => c.Copy.Id.ToString())) + ". Save to use it in the game data.");
            }
        });
    }

    public void Revert(SectionRef section) => Edits.Execute(EditCommand.Revert(section));

    /// <summary>
    /// Shows the game data that loaded again after a save. A selection of the game data becomes the item with the same
    /// nickname in the new data. A scene item stays, because the open system stays, and a section stays as it is. The
    /// open cutscene stays too.
    /// </summary>
    public void GameDataReplaced(GameDataManager data)
    {
        Selection = Selection switch
        {
            Ship ship => data.Items.Ships.Get(ship.Nickname),
            Equipment equipment => data.Items.Equipment.Get(equipment.Nickname),
            StarSystem system => data.Items.Systems.Get(system.Nickname) ?? (object)system,
            var other => other,
        };
        // The preview builds again with the new data (section 8.4 of the M8 design).
        Cutscenes.DataReplaced();
    }

    /// <summary>The Console warnings of the active mod at the open: the later mods (decision D2) and a mod in the
    /// OpenLancer repo (decision D5).</summary>
    private void ModWarnings(EditorSession session)
    {
        if (session.Edits.ActiveMod is not { } mod)
        {
            return;
        }

        if (LaterModsWarning(session) is { } later)
        {
            Log.Add(ConsoleSeverity.Warning, EditController.Source, later);
        }

        if (session.RepoRoot != null)
        {
            Log.Add(ConsoleSeverity.Warning, EditController.Source,
                $"The active mod '{mod.Id}' is part of the OpenLancer repo at {session.RepoRoot}. Its files are tracked " +
                "files, so the Inspector asks one time before the first edit.");
        }

        if (session.Edits.ReadOnlyReason is { } reason && !session.Edits.HasRecovery)
        {
            Log.Add(ConsoleSeverity.Warning, EditController.Source, $"The mod '{mod.Id}' takes no edits: {reason}");
        }
    }

    /// <summary>The warning about the mods that load after the active mod, or null when there are none.</summary>
    private static string? LaterModsWarning(EditorSession session) =>
        session.Edits.LaterMods.Count == 0
            ? null
            : $"The editor shows the data up to the active mod. These mods load later and can change the same values in " +
              $"the game: {string.Join(", ", session.Edits.LaterMods.Select(m => m.Id))}. A warning glyph marks such a value.";

    private void EditMenu()
    {
        var session = Session;
        var edits = session?.Edits;
        var idle = !IsEditing;
        if (ImGui.MenuItem($"{EditorIcons.Undo}   {(edits?.UndoLabel is { } undo ? $"Undo {undo}" : "Undo")}", "Ctrl+Z",
                false, edits is { CanUndo: true } && idle))
        {
            Edits.Undo();
        }

        if (ImGui.MenuItem($"{EditorIcons.Redo}   {(edits?.RedoLabel is { } redo ? $"Redo {redo}" : "Redo")}", "Ctrl+Y",
                false, edits is { CanRedo: true } && idle))
        {
            Edits.Redo();
        }

        ImGui.Separator();
        if (ImGui.MenuItem($"{EditorIcons.Save}   Save", "Ctrl+S", false, session is { CanEdit: true } && idle))
        {
            Edits.Save();
        }

        ImGui.SetItemTooltip(session?.NoEditReason ?? (edits?.ActiveMod is { } mod
            ? $"Writes the edits as patches of the mod '{mod.Id}'."
            : "No profile is open."));
        ImGui.Separator();
        var section = SectionOf(Selection);
        if (Theme.IconMenuItem(EditorIcons.Clone, "New from template...", CanCopy(section)))
        {
            NewFromTemplate(section!);
        }

        ImGui.SetItemTooltip("Copies the selected ship or equipment with the sections that belong to it.");
        if (Theme.IconMenuItem(EditorIcons.Undo, "Revert selected item", CanRevert(section)))
        {
            Revert(section!);
        }

        ImGui.SetItemTooltip(RevertTooltip(section));
    }

    private void ModMenu()
    {
        if (Session is not { } session)
        {
            ImGui.MenuItem("No profile is open", false);
            return;
        }

        var edits = session.Edits;
        var active = edits.ActiveMod?.Id;
        ImGui.TextDisabled("Active mod");
        if (ImGui.MenuItem("None (read-only)", "", active == null, CanChangeProfile))
        {
            SetActiveMod(null);
        }

        foreach (var mod in edits.Profile.Mods)
        {
            if (ImGui.MenuItem($"{EditorIcons.LayerGroup}   {mod.Id}", mod.Manifest.Name, mod.Id == active,
                    CanChangeProfile))
            {
                SetActiveMod(mod.Id);
            }

            ImGui.SetItemTooltip(mod.Folder);
        }

        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.Plus, "New mod...", CanChangeProfile))
        {
            NewMod(session);
        }

        if (edits.HasRecovery && Theme.IconMenuItem(EditorIcons.Undo, "Unsaved edits of an earlier session...", true))
        {
            Edits.OfferRecovery();
        }

        if (session.RepoRoot != null)
        {
            var allowed = session.RepoEditsAllowed;
            if (ImGui.MenuItem("Allow edits of this repo mod", "", ref allowed, !allowed))
            {
                session.RepoEditsAllowed = allowed;
            }
        }

        var warnings = new[]
        {
            LaterModsWarning(session),
            session.RepoRoot == null
                ? null
                : $"The mod '{active}' is part of the OpenLancer repo at {session.RepoRoot}. Its files are tracked files.",
        }.OfType<string>().ToList();
        if (warnings.Count > 0)
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360 * ImGuiHelper.Scale);
            foreach (var warning in warnings)
            {
                ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle}  {warning}");
            }

            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>Makes another mod the active mod. The profile opens again, because the edit view changes.</summary>
    private void SetActiveMod(string? modId)
    {
        if (Session is not { } session || session.Edits.ActiveMod?.Id == modId)
        {
            return;
        }

        var path = session.Profile.FilePath;
        Edits.Leave("changing the active mod", () =>
        {
            Settings.SetActiveMod(path, modId);
            Settings.Save();
            OpenNow(path);
        });
    }

    /// <summary>
    /// Makes a new mod in the mods folder next to the profiles folder, adds it to the end of the profile, and makes it
    /// the active mod. The new mod loads after the active mod, so the unsaved edits of the session can still be saved.
    /// </summary>
    private void NewMod(EditorSession session)
    {
        var path = session.Profile.FilePath;
        var modsFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "mods"));
        newModDialog.Show(modsFolder, path, id =>
        {
            Log.Add(ConsoleSeverity.Info, EditController.Source, $"Made mod '{id}' in {modsFolder} and added it to the profile.");
            Edits.Leave("making the new mod the active mod", () =>
            {
                Settings.SetActiveMod(path, id);
                Settings.Save();
                OpenNow(path);
            });
        });
    }

    private void DrawDialogs()
    {
        Choice.Draw();
        newModDialog.Draw();
        templateDialog.Draw();
        closeDialog.Draw();
    }

    /// <summary>True while a dialog of the editor is open.</summary>
    private bool DialogOpen => Choice.IsOpen || newModDialog.IsOpen || templateDialog.IsOpen || closeDialog.IsOpen;

    /// <summary>
    /// The keys of the Edit menu: Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z, and Ctrl+S. The keys of Play: F5 and Shift+F5. They
    /// wait while a text field or a dialog takes the keys.
    /// </summary>
    private void Shortcuts()
    {
        if (Session == null || ImGui.GetIO().WantTextInput || IsEditing || DialogOpen)
        {
            return;
        }

        const ImGuiInputFlags global = ImGuiInputFlags.RouteGlobal;
        if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.Z), global))
        {
            Edits.Undo();
        }
        else if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.Y), global) ||
                 ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.ImGuiMod_Shift | ImGuiKey.Z), global))
        {
            Edits.Redo();
        }
        else if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.S), global))
        {
            Edits.Save();
        }
        else if (ImGui.Shortcut((int)ImGuiKey.F5, global))
        {
            playMenu.PlayFromCamera();
        }
        else if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Shift | ImGuiKey.F5), global))
        {
            playMenu.Stop();
        }
    }
}
