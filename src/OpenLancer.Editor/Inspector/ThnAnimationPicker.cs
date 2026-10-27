using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData;
using LibreLancer.Data.Schema;
using LibreLancer.ImUI;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Animation row of a START_MOTION event whose first target is a character (a DEFORMABLE entity), as section 12.1
/// of the M8b design plans it. The field opens the list picker with the clips of the game data, grouped by the
/// skeletons of the costume of the target. The side pane plays the hovered clip on a character of the scene: the clip
/// preview. The <b>Preview</b> button plays the cutscene preview from the time of the event.
/// </summary>
/// <remarks>A COMPOUND plays the scripts of its own model, not the clips of the <c>.anm</c> files (GameObject.cs:602-605),
/// so the row of another target keeps its text field (risk R5 of the M8b design).</remarks>
internal sealed class ThnAnimationPicker(EditorWindow window, PropertyGrid grid) : IDisposable
{
    /// <summary>The path of the animation of a START_MOTION event.</summary>
    public const string AnimationPath = "props.animation";

    /// <summary>The warning of a clip name that the game does not have.</summary>
    public const string UnknownClip = "The game has no animation with this name. The character does not move.";

    private const string Character = "DEFORMABLE";
    private const string NotSet = "Not set";

    private const string Note =
        "The clip that the character plays (StartMotionEvent.cs:34). The list has the clips of the files that the " +
        "[Animations] lines of bodyparts.ini name. Hover over a clip to play it in the clip preview.";

    // The size of the side pane in unscaled points.
    private const float PaneWidth = 420;
    private const float PaneHeight = 540;

    private static readonly string[] PartNames = Enum.GetNames<ClipPart>();

    private readonly AnimationClipsCache<GameDataManager> cache = new(AnimationClips.Read);
    private readonly ThnAnimationPreview preview = new(window);
    private ClipPart part;

    // The event of the last draw, and the character of the clip preview that the user chose for it, or null.
    private ThnRef? shownEvent;
    private string? chosen;

    /// <summary>True when the row of a value gets the picker: the animation of a START_MOTION event whose first target
    /// is a DEFORMABLE entity of the script.</summary>
    /// <param name="targetType">The type of the entity of the first target, or null when the target names no
    /// entity.</param>
    public static bool Applies(EventTypes? eventType, string path, string? targetType) =>
        eventType == EventTypes.StartMotion && path == AnimationPath && targetType == Character;

    /// <summary>
    /// Draws the Animation row. When the clip list is not available, the row keeps its text field, and its note gives
    /// the reason.
    /// </summary>
    /// <param name="note">The note of the schema row, or null.</param>
    /// <param name="parse">Gives the value of a text.</param>
    public void Draw(CutsceneWorkspace workspace, string label, ThnFieldState field, TimelineEvent info, string? note,
        Func<string, ThnFieldParse> parse)
    {
        var data = window.Session?.GameData;
        if (cache.Get(data, out var reason) is not { } clips)
        {
            grid.ThnTextRow(label, field, note == null ? reason : $"{reason}\n\n{note}", parse, NotSet);
            return;
        }

        if (!Equals(shownEvent, field.Target))
        {
            shownEvent = field.Target;
            chosen = null;
        }

        var target = info.Targets.Count > 0 ? info.Targets[0] : "";
        var current = ThnFieldEdit.Format(field.Kind, field.Value).Trim();
        var clip = clips.Find(current);
        grid.ThnRow(label, field, note ?? Note, () =>
        {
            var style = ImGui.GetStyle();
            var button = ImGui.CalcTextSize("Preview").X + 2 * style.FramePadding.X;
            var (text, dim, color) = current.Length == 0 ? (NotSet, "", EditorTheme.TextDim)
                : clip != null ? (current, AnimationClips.Label(clip.Name), EditorTheme.Text)
                : ($"Missing ({ObjectKinds.Thn.Clip.Label}): {current}", "", EditorTheme.Warning);
            ImGui.BeginDisabled(field.ReadOnly != null);
            var clicked = PropertyGrid.PickerField(text, dim, color, false, clip != null ? ObjectKinds.Thn.Clip : null,
                -(button + style.ItemInnerSpacing.X));
            ImGui.EndDisabled();
            ImGui.SetItemTooltip(clip != null ? $"{clip.Name}\n{clips.SkeletonText(clip)}\n\nClick to pick a clip."
                : current.Length > 0 ? $"{UnknownClip}\n\nClick to pick a clip."
                : "Click to pick a clip.");
            grid.ThnListPopup(field, clicked,
                () => clips.Model(current, AnimationClips.Bodyparts(CostumeOf(workspace, data, target)), target),
                ObjectKinds.Thn.Clip, parse, new ListPickerPane(PaneWidth, PaneHeight, PartFilter,
                    row => clips.Shows(row, part), row => Pane(workspace, data, clips, row, target)));
            ImGui.SameLine(0, style.ItemInnerSpacing.X);
            // The button only plays the preview, so it stays active in the read-only state.
            ImGui.BeginDisabled(current.Length == 0 || workspace.Transport == null);
            if (ImGui.Button("Preview"))
            {
                workspace.Transport!.Seek(info.Time);
                workspace.Transport.Play();
            }

            ImGui.EndDisabled();
            ImGui.SetItemTooltip("Plays the cutscene preview from the time of this event.");
            if (current.Length > 0 && clip == null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
                ImGui.TextWrapped(UnknownClip);
                ImGui.PopStyleColor();
            }
        });
    }

    /// <summary>Forgets the game data and the choices of the session, and frees the clip preview. Call it when the
    /// profile closes.</summary>
    public void Reset()
    {
        cache.Reset();
        preview.Dispose();
        shownEvent = null;
        chosen = null;
        part = ClipPart.All;
    }

    public void Dispose() => preview.Dispose();

    /// <summary>The body part filter below the search field. Returns true when it changed.</summary>
    private bool PartFilter()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Body part");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Round(160 * ImGuiHelper.Scale));
        var index = (int)part;
        EditorTheme.PushMenuColors();
        var changed = ImGui.Combo("##part", ref index, PartNames, PartNames.Length);
        EditorTheme.PopMenuColors();
        ImGui.SetItemTooltip("Shows the clips of the files of the skeletons with this body part. Other shows the clips " +
                             "of the files that no skeleton of bodyparts.ini lists.");
        part = (ClipPart)index;
        return changed;
    }

    /// <summary>The side pane: the hovered clip, its file and its skeletons, the character of the clip preview, the
    /// hints, and the clip preview.</summary>
    private void Pane(CutsceneWorkspace workspace, GameDataManager? data, AnimationClips clips, ListPickerRow? row,
        string target)
    {
        if (row == null)
        {
            ImGui.TextDisabled("Hover over a clip to see it here.");
            return;
        }

        if (clips.Find(row.Value) is not { } clip)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
            ImGui.TextWrapped($"{row.Value}: {UnknownClip}");
            ImGui.PopStyleColor();
            return;
        }

        if (row.Note.Length > 0)
        {
            ImGui.Text(row.Note);
        }

        ImGui.Text(clip.Name);
        ImGui.TextDisabled($"File: {Path.GetFileName(clip.Archive.Replace('\\', '/'))}");
        ImGui.TextDisabled(clips.SkeletonText(clip));
        var characters = Characters(workspace);
        var on = AnimationClips.PreviewOn(characters, chosen, target);
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Preview on");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##preview-on", on ?? "No character"))
        {
            foreach (var name in characters)
            {
                var selected = name.Equals(on, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(name, selected) && !selected)
                {
                    chosen = name;
                }
            }

            ImGui.EndCombo();
        }

        EditorTheme.PopMenuColors();
        ImGui.SetItemTooltip("The character of the scene that plays the clip preview. The event keeps its target.");
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
        ImGui.TextWrapped("Labels come from words in the clip names. Preview each clip to check it.");
        ImGui.TextWrapped(ThnAnimationPreview.Controls);
        ImGui.PopStyleColor();
        if (on == null)
        {
            ImGui.TextWrapped("The script has no character for the clip preview.");
            return;
        }

        var sex = CostumeOf(workspace, data, on)?.Body?.Sex ?? FLGender.unset;
        preview.Draw(workspace.Visible?.CharacterOf(on), clip.Name, clips.IdleFor(clip, sex));
    }

    /// <summary>The names of the characters (DEFORMABLE entities) of the open script.</summary>
    private static List<string> Characters(CutsceneWorkspace workspace) =>
        workspace.Working?.Entities
            .Where(e => ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, CutsceneWorkspace.Symbols) == Character)
            .Select(e => ThnScriptModel.NameOf(e.Table)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    /// <summary>The costume of the entity with a name: its <c>template_name</c> in the costumes of the game data, or
    /// null.</summary>
    private static Costume? CostumeOf(CutsceneWorkspace workspace, GameDataManager? data, string name) =>
        workspace.Working?.EntityNamed(name)?.Table["template_name"] is ThnText { Value: var template }
            ? data?.Items.Costumes.Get(template)
            : null;
}
