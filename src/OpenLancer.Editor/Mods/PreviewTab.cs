using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// The Preview tab of the Mods window (section 10.5 of the M11c design). "Nexus page" shows the parts of a Nexus page
/// in the order of the page, each with Copy, which copies the exact text of <c>nexus-page.txt</c> for that field. The
/// README of each package, the FLMM script header of each target and <c>CHECKLIST.txt</c> need the target build, so
/// they come from the last check.
/// </summary>
/// <remarks>The texts come from the same <see cref="PackageTexts"/> code as the export, with the applied settings of
/// the mod.</remarks>
internal sealed class PreviewTab(EditorWindow window, ModsWindow mods)
{
    private const string NexusPage = "Nexus page";

    // Sizes in unscaled points.
    private const float ImageHeight = 90;

    private string shown = NexusPage;

    public void Draw(ModRow row)
    {
        if (row.Manifest is not { } manifest)
        {
            return;
        }

        var check = mods.Checks.ResultOf(row.Folder);
        var previews = check?.Previews ?? [];
        if (shown != NexusPage && previews.All(p => p.Name != shown))
        {
            shown = NexusPage;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Show");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Truncate(320 * ImGuiHelper.Scale));
        if (ImGui.BeginCombo("##show", shown))
        {
            foreach (var name in previews.Select(p => p.Name).Prepend(NexusPage))
            {
                if (ImGui.Selectable(name, name == shown))
                {
                    shown = name;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip("The text that the preview shows. The README, the FLMM header and the checklist come from the " +
                             "last check.");
        ImGui.SameLine();
        var checking = mods.Checks.IsChecking(row.Folder);
        ImGui.BeginDisabled(checking);
        if (ImGui.Button(checking ? "Checking..." : "Check now"))
        {
            mods.CheckNow(row);
        }

        ImGui.EndDisabled();
        if (check == null)
        {
            CardLayout.Dim("OpenLancer did not check this mod in this session. Click Check now to make the previews of " +
                           "the README files, the FLMM script header and CHECKLIST.txt.");
        }
        else if (check.Content != row.Content || check.Page != row.Page)
        {
            CardLayout.Note(EditorIcons.ExclamationTriangle, EditorTheme.Warning, PublishMessages.CheckOld(check.Time));
        }

        ImGui.Separator();
        if (!ImGui.BeginChild("##text"))
        {
            ImGui.EndChild();
            return;
        }

        if (shown == NexusPage)
        {
            Nexus(row, manifest);
        }
        else if (previews.FirstOrDefault(p => p.Name == shown) is { } preview)
        {
            Text(preview.Text);
        }

        ImGui.EndChild();
    }

    /// <summary>A text of a file, read-only, so that the user can select and copy a part of it.</summary>
    private static void Text(string text)
    {
        var shown = text.Replace("\r\n", "\n");
        if (ImGui.Button($"{EditorIcons.Copy}  Copy"))
        {
            ImGui.SetClipboardText(text);
        }

        ImGui.SetItemTooltip("Copy the whole text.");
        ImGui.InputTextMultiline("##file", ref shown, (uint)System.Text.Encoding.UTF8.GetByteCount(shown) + 1,
            new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
    }

    /// <summary>The parts of the Nexus page in the order of the page.</summary>
    private void Nexus(ModRow row, ModManifest manifest)
    {
        var texts = new PublishTexts(manifest, row.Settings);
        var packages = Packages(manifest, window.Settings.ExportOf(row.Folder));
        var images = row.Settings.Screenshots.Select((s, i) => ($"{i + 1:00}-{Path.GetFileName(s.File)}", s.Caption)).ToList();
        var parts = Parts(PackageTexts.NexusPage(texts, packages, images));
        CardLayout.Dim("The preview follows the parts of a Nexus page. It does not copy the look of the site.");
        ImGui.Spacing();

        Part(parts, "Mod name", () =>
        {
            ImGui.PushFont(null, EditorTheme.HeroSize);
            CardLayout.Bold(manifest.Name);
            ImGui.PopFont();
        });
        Part(parts, "Author", () => Lines(parts, "Author"));
        Part(parts, "Version", () => Lines(parts, "Version"));
        Part(parts, "Summary", () => Lines(parts, "Summary"));
        Images(row);
        Part(parts, "Description (BBCode)", () => Description(manifest, row.Settings, packages));
        Part(parts, "Requirements", () => Lines(parts, "Requirements"));
        Part(parts, "Files", () => Lines(parts, "Files"));
        Part(parts, "Changelog", () => Lines(parts, "Changelog"));
        Part(parts, "Permissions and credits", () => Lines(parts, "Permissions and credits"));
        Part(parts, "Category", () => Lines(parts, "Category"));
        Part(parts, "Tags", () => Lines(parts, "Tags"));
    }

    /// <summary>The image strip: the thumbnail of each screenshot with its caption.</summary>
    private void Images(ModRow row)
    {
        Heading("Images");
        ImGui.Separator();
        if (row.Settings.Screenshots.Count == 0)
        {
            ImGui.TextDisabled("No screenshots");
            return;
        }

        var height = MathF.Truncate(ImageHeight * ImGuiHelper.Scale);
        foreach (var shot in row.Settings.Screenshots)
        {
            var path = Path.Combine(row.Folder, shot.File.Replace('/', Path.DirectorySeparatorChar));
            if (mods.Thumbnails.Get(path) is { } image)
            {
                var (uv0, uv1) = ScreenshotThumbnails.Uv;
                ImGui.Image(image.Ref, new Vector2(MathF.Round(height * image.Size.X / MathF.Max(1, image.Size.Y)), height),
                    uv0, uv1);
            }
            else
            {
                ImGui.Dummy(new Vector2(height, height));
            }

            ImGui.SetItemTooltip(shot.Caption.Length > 0 ? $"{shot.File}\n{shot.Caption}" : shot.File);
            ImGui.SameLine();
        }

        ImGui.NewLine();
    }

    /// <summary>The description as Nexus shows it: the text of the mod, the install steps of each form of the export
    /// and the notice.</summary>
    private void Description(ModManifest manifest, PublishSettings settings, IReadOnlyList<ExportedPackage> packages)
    {
        if (settings.Description.Trim().Length > 0)
        {
            PageTextView.Draw(window, settings.Description);
            ImGui.Spacing();
        }

        ImGui.PushFont(null, EditorTheme.TitleSize);
        CardLayout.Bold("Install");
        ImGui.PopFont();
        foreach (var form in new[] { PackageForm.DropIn, PackageForm.Flmm, PackageForm.ModFolder })
        {
            if (packages.All(p => p.Form != form))
            {
                continue;
            }

            var (name, steps) = PackageTexts.Install(manifest, form);
            CardLayout.Bold(name);
            for (var i = 0; i < steps.Count; i++)
            {
                ImGui.TextWrapped($"{i + 1}. {steps[i]}");
            }
        }

        ImGui.Spacing();
        CardLayout.Dim(PackageTexts.Notice(manifest));
    }

    /// <summary>The packages that an export with the saved choices of the Export dialog writes, for the Files part and
    /// the install steps.</summary>
    internal static IReadOnlyList<ExportedPackage> Packages(ModManifest manifest, ExportChoices? choices)
    {
        var packages = new List<ExportedPackage>();
        foreach (var target in ExportTarget.All.Where(t => manifest.Targets.Contains(t.Id) && t.Refusal(manifest) == null))
        {
            if (!target.IsRetail)
            {
                packages.Add(new ExportedPackage(target, ModZip.FileName(manifest), null) { Form = PackageForm.ModFolder });
                continue;
            }

            if (choices?.DropIn ?? true)
            {
                packages.Add(new ExportedPackage(target, DropInPackage.FileName(manifest, target), null) { Form = PackageForm.DropIn });
            }

            if (choices?.Flmm ?? true)
            {
                packages.Add(new ExportedPackage(target, FlmmPackage.FileName(manifest, target, false), null) { Form = PackageForm.Flmm });
            }
        }

        return packages;
    }

    /// <summary>The parts of the text of <c>nexus-page.txt</c>: the lines of each block "== name ==", by name.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Parts(string page)
    {
        var parts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        string? name = null;
        var lines = new List<string>();
        foreach (var line in page.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        {
            if (line.StartsWith("== ", StringComparison.Ordinal) && line.EndsWith(" ==", StringComparison.Ordinal) &&
                line.Length > 6)
            {
                Close();
                name = line[3..^3];
                lines = [];
                continue;
            }

            lines.Add(line);
        }

        Close();
        return parts;

        void Close()
        {
            if (name == null)
            {
                return;
            }

            // The empty line before the next block belongs to no part.
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            parts[name] = lines;
        }
    }

    /// <summary>A part with its name, Copy, and its content.</summary>
    private static void Part(IReadOnlyDictionary<string, IReadOnlyList<string>> parts, string name, Action content)
    {
        ImGui.PushID(name);
        Heading(name);
        if (parts.TryGetValue(name, out var lines))
        {
            ImGui.SameLine();
            if (CardLayout.IconButton(EditorIcons.Copy, $"Copy the text of the Nexus field \"{name}\"."))
            {
                ImGui.SetClipboardText(string.Join("\r\n", lines));
            }
        }

        ImGui.Separator();
        content();
        ImGui.PopID();
    }

    /// <summary>The name of a part in the label color, after a gap.</summary>
    private static void Heading(string name)
    {
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(EditorTheme.Label, name);
    }

    /// <summary>The lines of a part as plain text, or a dim "None" for an empty part.</summary>
    private static void Lines(IReadOnlyDictionary<string, IReadOnlyList<string>> parts, string name)
    {
        if (!parts.TryGetValue(name, out var lines) || lines.All(l => l.Trim().Length == 0))
        {
            ImGui.TextDisabled("None");
            return;
        }

        foreach (var line in lines)
        {
            ImGui.TextWrapped(line.Length == 0 ? " " : line);
        }
    }
}
