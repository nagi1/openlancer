using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using ZoneShape = LibreLancer.Data.Schema.Universe.ZoneShape;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// The actions on the items of the open system (milestone M3): Create, Duplicate, Rename and Delete. Each action makes a
/// plan (see <see cref="ScenePlans"/>) and runs its command as one entry of the edit history. The Hierarchy, the Scene,
/// the Inspector and the Edit menu start them, with the keys of Unity: F2 Rename, Ctrl+D Duplicate and Delete.
/// </summary>
/// <remarks>
/// <para>Create of an object opens the picker of the archetypes of its template, and a station then opens the picker of
/// the bases (decisions D233 and D263). A delete runs at once when it changes nothing else, and asks otherwise (decision
/// D238). A rename starts in the Hierarchy row (decision D242), and asks only when the plan has warnings.</para>
/// <para>The selection follows Unity (decision D249): Create and Duplicate select the new item, Delete clears the
/// selection, and a rename keeps it.</para>
/// </remarks>
internal sealed class SceneEdits(EditorWindow window)
{
    private const string Source = EditController.Source;

    private static readonly (string Label, ZoneShape Shape)[] Shapes =
    [
        ("Sphere", ZoneShape.SPHERE), ("Ellipsoid", ZoneShape.ELLIPSOID), ("Box", ZoneShape.BOX),
        ("Cylinder", ZoneShape.CYLINDER), ("Ring", ZoneShape.RING),
    ];

    private readonly PickDialog picker = new();
    private readonly PlanDialog dialog = new();

    /// <summary>The section whose Hierarchy row shows the rename field, or null.</summary>
    public SectionRef? Renaming { get; private set; }

    /// <summary>The text of the rename field.</summary>
    public string RenameText = "";

    /// <summary>True in the first frame of a rename, so the field takes the focus.</summary>
    public bool RenameStarted { get; set; }

    /// <summary>True while a dialog of the actions is open.</summary>
    public bool IsOpen => picker.IsOpen || dialog.IsOpen;

    public void Draw()
    {
        picker.Draw();
        dialog.Draw();
    }

    /// <summary>The section of an item of the open system that the actions take: an object, a zone, a light, a nebula or
    /// a field. Null for another item.</summary>
    public SectionRef? SectionOf(object? item) =>
        item is SystemObject or Zone or LightSource or Nebula or AsteroidField && window.Scene.Contains(item!)
            ? window.Scene.SectionOf(item!)
            : null;

    /// <summary>Why an action cannot run on an item now, or null. The menus show it as the tooltip of a disabled
    /// item.</summary>
    public string? Unavailable(SceneAction action, object? item)
    {
        if (window.Session is not { } session)
            return "No profile is open.";
        if (session.NoEditReason is { } reason)
            return reason;
        if (window.IsEditing)
            return "Wait until the field edit ends.";
        if (action == SceneAction.Create)
            return window.Scene.System == null ? "Open a system first." : null;
        if (SectionOf(item) is not { } section)
            return "Select an object, a zone, a light, a nebula or an asteroid field of the open system.";
        return ScenePlans.Unavailable(session.Edits, action, section);
    }

    /// <summary>The keys of the Hierarchy and the Scene: F2 Rename, Ctrl+D Duplicate and Delete. They act only on an
    /// item of the open system, while no text field is active, no field edit is open and no dialog shows (decision D255,
    /// review finding R32).</summary>
    public void Keys(object? selection)
    {
        if (ImGui.GetIO().WantTextInput || window.IsEditing || IsOpen || Renaming != null || SectionOf(selection) is not { } section)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.F2, false))
            BeginRename(section);
        else if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.D)))
            Duplicate(section);
        else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
            Delete(section);
    }

    /// <summary>
    /// The items of the Create menu: the object templates, the zone shapes and the light (decision D255). A connection
    /// template is not in the menu.
    /// </summary>
    /// <param name="row">The object of a Hierarchy row, whose position and parent a new object takes, or null.</param>
    public void CreateMenu(SectionRef? row)
    {
        var unavailable = Unavailable(SceneAction.Create, null);
        if (window.Session is not { } session)
            return;
        foreach (var (template, label) in ScenePlans.ObjectTemplates(session.Edits))
        {
            if (Theme.IconMenuItem(Kind(template).Icon, label + "...", unavailable == null))
                Create(template, row);
        }

        ImGui.Separator();
        if (ImGui.BeginMenu($"{ObjectKinds.Of(ZoneRole.Other).Icon}  Zone", unavailable == null))
        {
            foreach (var (label, shape) in Shapes)
            {
                if (ImGui.MenuItem(label))
                    Create(SceneTemplate.Zone, row, shape);
            }

            ImGui.EndMenu();
        }

        if (Theme.IconMenuItem(ObjectKinds.Light.Icon, "Light", unavailable == null))
            Create(SceneTemplate.Light, row);
        if (unavailable != null)
            ImGui.SetItemTooltip(unavailable);
        ImGui.Separator();
        ImGui.TextDisabled("Jump gates, jump holes and trade lane rings come with milestone M27.");
    }

    /// <summary>The items of the actions of a selected item for a menu: Duplicate, Rename and Delete, each disabled with
    /// its reason when it cannot run.</summary>
    public void ItemMenu(object? item)
    {
        if (MenuItem(EditorIcons.Clone, "Duplicate", "Ctrl+D", SceneAction.Duplicate, item))
            Duplicate(SectionOf(item)!);
        if (MenuItem(EditorIcons.Pen, "Rename", "F2", SceneAction.Rename, item))
            BeginRename(SectionOf(item)!);
        if (MenuItem(EditorIcons.TrashAlt, "Delete", "Del", SceneAction.Delete, item))
            Delete(SectionOf(item)!);
    }

    private bool MenuItem(char icon, string label, string shortcut, SceneAction action, object? item)
    {
        var unavailable = Unavailable(action, item);
        var clicked = ImGui.MenuItem($"{icon}   {label}", shortcut, false, unavailable == null);
        if (unavailable != null)
            ImGui.SetItemTooltip(unavailable);
        return clicked;
    }

    /// <summary>
    /// Creates an item (decisions D233 to D235): an object after the pick of its archetype, and of its base for a station,
    /// or a zone or a light at once. The item goes to the pivot of the Scene camera, or to the position of the object of
    /// the row. Then the new item is selected and its Hierarchy row starts its rename.
    /// </summary>
    public void Create(SceneTemplate template, SectionRef? row, ZoneShape shape = ZoneShape.SPHERE)
    {
        if (window.Session is not { } session || window.Scene.System is not { } system)
            return;
        var edits = session.Edits;
        var parent = row is { Id.Type: var type } && type.Equals("Object", StringComparison.OrdinalIgnoreCase) ? row : null;
        var position = parent != null && window.Scene.ItemOf(parent) is SystemObject obj ? obj.Position : window.Scene.Pivot;
        if (template is SceneTemplate.Zone or SceneTemplate.Light)
        {
            Run(ScenePlans.Create(edits, system.Nickname, template, null, position, null, shape: shape));
            return;
        }

        var archetypes = ScenePlans.Archetypes(edits, template);
        picker.Show($"Create: {template}", "Pick the archetype of the new object.", Kind(template),
            archetypes.Select(a => (a.Entry, session.ItemName(a.Entry.Section!) is { Length: > 0 } name ? name : a.Nickname,
                a.Type)).ToList(),
            archetype =>
            {
                if (!ScenePlans.AsksForBase(edits, template))
                {
                    Run(ScenePlans.Create(edits, system.Nickname, template, archetype.Value, position, parent));
                    return;
                }

                var bases = ScenePlans.Bases(edits, system.Nickname);
                picker.Show("Create: base", $"Pick the base that {archetype.Value} docks with. Esc makes the station " +
                                            "without a base.", ObjectKinds.Station,
                    bases.Select(b => (b, BaseName(b), BaseSystem(edits, b))).ToList(),
                    picked => Run(ScenePlans.Create(edits, system.Nickname, template, archetype.Value, position, parent,
                        picked.Value)),
                    "No base",
                    () => Run(ScenePlans.Create(edits, system.Nickname, template, archetype.Value, position, parent)));
            });

        string BaseName(ListEntry entry) =>
            entry.Section != null && session.ItemName(entry.Section) is { Length: > 0 } name ? name : entry.Value;
    }

    /// <summary>
    /// Adds a component to an object after the pick of its value (decision D244): a loadout, a base or a faction. The base
    /// picker lists the bases of the system first. One add is one entry of the edit history.
    /// </summary>
    public void AddComponent(SectionRef section, ObjectComponent kind)
    {
        if (window.Session is not { } session || window.Scene.System is not { } system)
            return;
        var edits = session.Edits;
        var list = ObjectComponents.ListOf(kind);
        var entries = list == "base"
            ? ScenePlans.Bases(edits, system.Nickname)
            : edits.Lists.List(list)?.Entries ?? [];
        picker.Show($"Add {ObjectComponents.Name(kind)}", $"Pick the {list} of {section.Id.Key}.", ObjectKinds.OfList(list),
            entries.Select(e => (e, e.Section != null && session.ItemName(e.Section) is { Length: > 0 } name ? name : e.Value,
                list == "base" ? BaseSystem(edits, e) : e.Detail)).ToList(),
            picked => window.Edits.Execute(ObjectComponents.Add(section, kind, picked.Value)));
    }

    /// <summary>Removes a component of an object (decision D244). One remove is one entry of the edit history.</summary>
    public void RemoveComponent(SectionRef section, ObjectComponent kind) =>
        window.Edits.Execute(() => ObjectComponents.Remove(section, kind));

    /// <summary>Copies an item with its children (decisions D241 and D262). Unity does not ask, so the warnings go to the
    /// Console.</summary>
    public void Duplicate(SectionRef section)
    {
        if (window.Session is not { } session)
            return;
        Run(ScenePlans.Duplicate(session.Edits, [section]));
    }

    /// <summary>Deletes an item (decisions D81 and D238): at once when nothing else changes, else after the dialog. A
    /// refused delete shows its reason.</summary>
    public void Delete(SectionRef section)
    {
        if (window.Session is not { } session)
            return;
        var plan = ScenePlans.Delete(session.Edits, [section]);
        if (plan.Command == null || plan.NeedsConfirmation)
        {
            dialog.Show(plan, () => Run(plan));
            return;
        }

        Run(plan);
    }

    /// <summary>Starts the rename field of the Hierarchy row of an item (decision D242). The Hierarchy shows the
    /// panel.</summary>
    public void BeginRename(SectionRef section)
    {
        if (window.Session is not { } session)
            return;
        // The item of a new section shows after the next flush of the scene, so the check reads the section.
        var reason = session.NoEditReason ?? (window.IsEditing ? "Wait until the field edit ends." : null) ??
                     ScenePlans.Unavailable(session.Edits, SceneAction.Rename, section);
        if (reason != null)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, reason, section);
            return;
        }

        Renaming = section;
        RenameText = ScenePlans.RenameTarget(session.Edits, section).Id.Key ?? "";
        RenameStarted = true;
        window.ShowPanel("hierarchy");
    }

    /// <summary>Ends the rename field without a rename.</summary>
    public void CancelRename() => Renaming = null;

    /// <summary>Why the text of the rename field cannot be the new nickname, or null.</summary>
    public string? RenameProblem() =>
        Renaming != null && window.Session is { } session
            ? ScenePlans.RenameProblem(session.Edits, Renaming, RenameText)
            : null;

    /// <summary>Runs the rename of the field: at once, or after the dialog of its warnings. A text that is the old
    /// nickname ends the field and changes nothing.</summary>
    public void CommitRename()
    {
        if (Renaming is not { } section || window.Session is not { } session)
            return;
        Renaming = null;
        var target = ScenePlans.RenameTarget(session.Edits, section);
        if (string.Equals(target.Id.Key, RenameText.Trim(), StringComparison.Ordinal))
            return;
        var plan = ScenePlans.Rename(session.Edits, section, RenameText);
        if (plan.Command == null)
        {
            foreach (var refusal in plan.Refusals)
                window.Log.Add(ConsoleSeverity.Error, Source, refusal.Message, refusal.Target);
            return;
        }

        if (plan.NeedsConfirmation)
            dialog.Show(plan, () => Run(plan));
        else
            Run(plan);
    }

    /// <summary>Runs the command of a plan, or tells why it cannot run. The warnings go to the Console. The selection
    /// follows Unity (decision D249).</summary>
    private void Run(ScenePlan plan)
    {
        if (plan.Command == null)
        {
            foreach (var refusal in plan.Refusals)
                window.Log.Add(ConsoleSeverity.Error, Source, refusal.Message, refusal.Target);
            return;
        }

        if (!window.Edits.Execute(plan.Command))
            return;
        foreach (var warning in plan.Warnings)
            window.Log.Add(ConsoleSeverity.Warning, Source, warning.Message, warning.Target);
        switch (plan.Action)
        {
            case SceneAction.Delete:
                window.Selection = null;
                break;
            case SceneAction.Create or SceneAction.Duplicate when plan.SelectAfter is { } added:
                window.Scene.SelectWhenShown(added);
                if (plan.Action == SceneAction.Create)
                    BeginRename(added);
                break;
            case SceneAction.Rename when plan.SelectAfter is { } renamed && window.Selection is SectionRef:
                window.Selection = renamed;
                break;
        }

        if (plan.Action == SceneAction.Delete && plan.Deletes.Count > 0)
            deleted[plan.Label] = plan.Deletes[0].Section;
    }

    /// <summary>The first target of each delete, by the label of its entry of the edit history, so the undo of a delete
    /// selects the item again (decision D249).</summary>
    private readonly System.Collections.Generic.Dictionary<string, SectionRef> deleted = [];

    /// <summary>Selects the item of a delete again after the undo of the delete, as Unity does. Other undos change no
    /// selection.</summary>
    /// <param name="label">The label of the entry that the undo took back, or null.</param>
    public void Undone(string? label)
    {
        if (label != null && deleted.TryGetValue(label, out var section) && window.Session?.Edits.Get(section) != null)
            window.Scene.SelectWhenShown(section);
    }

    /// <summary>The system of a base, for the detail of the base picker.</summary>
    private static string BaseSystem(EditSession edits, ListEntry entry) =>
        entry.Section != null && edits.Get(entry.Section)?.Lines("system").LastOrDefault()?.Values is [var system, ..]
            ? system.ToString()
            : "";

    /// <summary>The kind of an object template, for its glyph.</summary>
    private static ObjectKind Kind(SceneTemplate template) => template switch
    {
        SceneTemplate.Station => ObjectKinds.Station,
        SceneTemplate.Planet => ObjectKinds.Planet,
        SceneTemplate.Sun => ObjectKinds.Sun,
        SceneTemplate.Satellite => ObjectKinds.Satellite,
        _ => ObjectKinds.OtherObject,
    };
}
