using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Ui;
using LibreLancer.ImUI;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>Selects and previews animation clips on the target of a motion event.</summary>
internal sealed class ThnAnimationPicker(EditorWindow window) : IDisposable
{
    private static readonly string[] Characters = ["All", "Juni", "Trent", "Orillion", "King", "Female", "Male"];
    private static readonly string[] Parts = ["All", "Body", "Head", "Hands", "Other"];
    private object? loadedData;
    private IReadOnlyList<AnimationClip> clips = [];
    private string? error;
    private string search = "";
    private int character;
    private int part;
    private AnimationClip? hoveredClip;
    private ThnRef? previewEvent;
    private string? eventTarget;
    private bool manualCharacter;
    private readonly ThnAnimationPreview preview = new(window);

    /// <summary>Draws the picker and the preview button.</summary>
    public void Draw(CutsceneWorkspace workspace, ThnFieldState field, ThnTable table)
    {
        Load();
        var current = field.Value is ThnText text ? text.Value : "";
        var targetName = TargetName(table);
        if (!Equals(previewEvent, field.Target) || !string.Equals(eventTarget, targetName, StringComparison.OrdinalIgnoreCase))
        {
            previewEvent = field.Target;
            eventTarget = targetName;
            manualCharacter = false;
            character = 0;
            hoveredClip = clips.FirstOrDefault(c => string.Equals(current, c.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (!manualCharacter && workspace.Visible is { } player)
        {
            var actor = player.CharacterOf(targetName);
            var voice = player.VoiceOf(targetName);
            var auto = CharacterIndex(targetName, voice?.Actor, voice?.Voice, actor?.Skeleton.Head?.Path, actor?.Skeleton.Body.Path);
            if (auto != 0)
                character = auto;
        }
        ImGui.BeginDisabled(field.ReadOnly != null || loadedData == null);
        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - 76));
        ImGui.SetNextWindowSizeConstraints(new Vector2(940 * ImGuiHelper.Scale, 0),
            new Vector2(float.MaxValue, 700 * ImGuiHelper.Scale));
        if (ImGui.BeginCombo("##clip", current.Length == 0 ? "Choose an animation" : current))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##search", "Search animations", ref search, 128);
            ImGui.Text("Character");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##character", ref character, Characters, Characters.Length))
                manualCharacter = true;
            ImGui.SetItemTooltip("The event target sets this filter. You can select another character.");
            ImGui.Text("Body part");
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##part", ref part, Parts, Parts.Length);
            ImGui.SetItemTooltip("Filter by body part.");
            if (error != null)
                ImGui.TextWrapped(error);
            ImGui.TextWrapped("Hover over an animation to preview it. Click to use it.");
            ImGui.TextDisabled("Labels are suggestions from file names. Preview each clip to check it.");
            var previewWidth = 390 * ImGuiHelper.Scale;
            var listWidth = Math.Max(200 * ImGuiHelper.Scale, ImGui.GetContentRegionAvail().X - previewWidth - ImGui.GetStyle().ItemSpacing.X);
            ImGui.BeginChild("##clips", new Vector2(listWidth, 460 * ImGuiHelper.Scale), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
            foreach (var clip in clips.Where(c => AnimationCatalog.Matches(c, Characters[character], Parts[part], search)))
            {
                ImGui.PushID(clip.Archive + "/" + clip.Name);
                if (ImGui.Selectable(AnimationCatalog.Label(clip.Name) + "  -  " + clip.Name,
                        string.Equals(current, clip.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    window.Edits.Execute(() => ThnEdits.Set(field.Target, field.Path, new ThnText(clip.Name)));
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered())
                    hoveredClip = clip;
                ImGui.PopID();
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("##animation-preview", new Vector2(previewWidth, 460 * ImGuiHelper.Scale),
                ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
            if (hoveredClip is { } shown)
            {
                ImGui.Text(AnimationCatalog.Label(shown.Name) + " - " + AnimationCatalog.Part(shown));
                ImGui.TextDisabled(shown.Name);
                ImGui.TextDisabled("Scroll: zoom. Drag: rotate.");
                ImGui.TextDisabled("Right drag or Shift + drag: move.");
                var chosen = PreviewTarget(workspace, targetName, character);
                if (chosen == null)
                    ImGui.TextWrapped("This character is not in the scene. Select another character or use All.");
                else
                {
                    ImGui.Text("Character: " + chosen);
                    if (!string.Equals(chosen, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        ImGui.TextWrapped("The event targets " + targetName + ".");
                        if (table.List.Length > 2 && table.List[2] is ThnTable { List.Length: > 0 } targets &&
                            ImGui.Button("Use this character for the event"))
                            window.Edits.Execute(() => ThnEdits.Set(field.Target, new ThnPath("targets"),
                                ThnFieldEdit.TargetsValue(targets, 0, chosen)));
                    }
                    preview.Draw(workspace.Visible?.CharacterOf(chosen), shown);
                }
            }
            else
            {
                ImGui.TextWrapped("Hover over an animation to see it here.");
            }
            ImGui.EndChild();
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(current.Length == 0 || workspace.Transport == null);
        if (ImGui.Button("Preview"))
        {
            var time = table.List.Length > 0 && table.List[0] is ThnNumber number ? number.Value : 0;
            workspace.Transport!.Seek(time);
            workspace.Transport.Play();
        }
        ImGui.EndDisabled();
        ImGui.SetItemTooltip("Play the cutscene from this event. The target plays the selected animation.");
        ImGui.EndDisabled();
    }

    private static string? PreviewTarget(CutsceneWorkspace workspace, string target, int choice)
    {
        if (choice is 0 or 5 or 6)
            return target;
        var player = workspace.Visible;
        if (player == null)
            return target;
        int CharacterOf(string name)
        {
            var source = player.CharacterOf(name);
            var voice = player.VoiceOf(name);
            return source == null ? 0 : CharacterIndex(name, voice?.Actor, voice?.Voice,
                source.Skeleton.Head?.Path, source.Skeleton.Body.Path);
        }
        if (CharacterOf(target) == choice)
            return target;
        return workspace.Working?.Entities.Select(e => ThnScriptModel.NameOf(e.Table)).OfType<string>()
            .FirstOrDefault(name => CharacterOf(name) == choice);
    }

    internal static int CharacterIndex(params string?[] hints)
    {
        var text = string.Join(" ", hints).ToLowerInvariant();
        if (text.Contains("juni")) return 1;
        if (text.Contains("trent")) return 2;
        if (text.Contains("orillion") || text.Contains("orilion")) return 3;
        if (Regex.IsMatch(text, @"(?<![a-z0-9])king(?![a-z0-9])")) return 4;
        if (text.Contains("female")) return 5;
        if (text.Contains("male")) return 6;
        return 0;
    }

    internal static string TargetName(ThnTable table) =>
        table.List.Length > 2 && table.List[2] is ThnTable targets &&
        targets.List.Length > 0 && targets.List[0] is ThnText name ? name.Value : "";

    public void Dispose() => preview.Dispose();

    private void Load()
    {
        var data = window.Session?.GameData;
        if (ReferenceEquals(data, loadedData))
            return;
        loadedData = data;
        clips = [];
        error = null;
        if (data == null)
            return;
        try
        {
            var files = data.Items.Ini.Bodyparts.Animations.Select(data.Items.DataPath).OfType<string>();
            clips = AnimationCatalog.Read(data.Items.VFS, files);
            if (clips.Count == 0)
                error = "No animation clips were found in the game data.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = "Cannot read the animation files: " + e.Message;
        }
    }
}
