using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Editor.Ui.Fields;

/// <summary>
/// What the raw INI editor must do in a frame: set a new text, and add the marks again (review finding RV11). The native
/// editor clears all marks when it gets a new text, so new marks come after each new text.
/// </summary>
internal sealed class RawIniEditorState
{
    private string? text;
    private IReadOnlyList<RawMark>? marks;

    /// <summary>The steps for the text and the marks of this frame. A step that it returns counts as done.</summary>
    public (bool SetText, bool SetMarks) Plan(string newText, IReadOnlyList<RawMark> newMarks)
    {
        var setText = !string.Equals(text, newText, StringComparison.Ordinal);
        var setMarks = setText || marks == null || !marks.SequenceEqual(newMarks);
        text = newText;
        marks = newMarks;
        return (setText, setMarks);
    }

    /// <summary>Forgets the text, so that the next frame sets it again.</summary>
    public void Reset() => (text, marks) = (null, null);
}

/// <summary>
/// The raw INI editor (AGENTS.md UI rule 11, section 11.5 of the M6a design): the vendored color text editor in its INI
/// mode, with the marks of the checks on the lines. A line with an error has the error color, a line with a warning has
/// the warning color, and a line with only base-game problems has a muted color. The tooltip is the message of the
/// check, the same text as on the field.
/// </summary>
/// <remarks>When the loaded <c>cimgui.dll</c> lacks the exports of OL-0060, the editor shows the text in a read-only
/// field with a list of the marks under it, as OL-0019 does for ImGuizmo.</remarks>
internal sealed class RawIniEditor : IDisposable
{
    /// <summary>The Console text when the natives lack the exports (section 13.1 of the M6a design).</summary>
    public const string NeedsNewNatives =
        "The raw INI editor needs a newer cimgui.dll. The INI text shows without colors and marks. Build the natives " +
        "again. See \"Build the submodule\" in docs/UPSTREAM.md.";

    private static bool? nativeReady;
    private static bool identifiersSet;

    private readonly RawIniEditorState state = new();
    private ColorTextEdit? editor;
    private int? goToLine;
    private string fallbackText = "";

    /// <summary>The names in the keyword color and the declaration color of all raw editors, or null for none.</summary>
    public static (IEnumerable<string> Keywords, IEnumerable<string> Declarations)? Identifiers { get; set; }

    /// <summary>Raised once when the natives lack the exports of the INI mode.</summary>
    public static event Action? FallbackShown;

    /// <summary>True when the loaded natives have each export that the INI mode needs.</summary>
    public static bool NativeReady => nativeReady ??= HasExports(Probe);

    /// <summary>True when a probe finds each export of the INI mode.</summary>
    internal static bool HasExports(Func<string, bool> probe) => ColorTextEdit.IniExports.All(probe);

    /// <summary>The line of the cursor (from 0), or -1 without a native editor.</summary>
    public int CursorLine => editor?.GetCoordinates().Y ?? -1;

    /// <summary>Moves the cursor to a line, selects it and scrolls to it at the next draw.</summary>
    public void GoToLine(int line) => goToLine = line;

    /// <summary>Draws the editor with the text and the marks, read-only, in a region of the size.</summary>
    /// <param name="id">The id of the editor.</param>
    /// <param name="marks">The marks of the lines, from <see cref="IniLint.Marks"/>.</param>
    public void Draw(string id, RawText text, IReadOnlyList<RawMark> marks, Vector2 size)
    {
        if (!NativeReady)
        {
            Fallback(id, text, marks, size);
            return;
        }

        if (editor == null)
        {
            editor = new ColorTextEdit();
            editor.SetMode(ColorTextEditMode.Ini);
            editor.SetReadOnly(true);
            editor.SetPalette(Palette());
        }

        if (!identifiersSet && Identifiers is var (keywords, declarations))
        {
            editor.SetIdentifiers(ColorTextEditIdentifiers.Keywords, keywords);
            editor.SetIdentifiers(ColorTextEditIdentifiers.Declarations, declarations);
            identifiersSet = true;
        }

        var (setText, setMarks) = state.Plan(text.Text, marks);
        if (setText)
        {
            editor.SetText(text.Text);
        }

        if (setMarks)
        {
            editor.ClearMarkers();
            foreach (var mark in marks)
            {
                var color = MarkColor(mark);
                editor.AddMarker(mark.Line, ImGui.GetColorU32(color), ImGui.GetColorU32(color with { W = 0.18f }),
                    mark.Message, mark.Message);
            }
        }

        if (goToLine is { } line)
        {
            editor.SetCursor(line, 0);
            editor.SelectLine(line);
            editor.ScrollToLine(line);
            goToLine = null;
        }

        if (ImGui.BeginChild(id, size))
        {
            editor.Render(id);
        }

        ImGui.EndChild();
    }

    /// <summary>The read-only field and the list of marks, when the natives lack the exports.</summary>
    private void Fallback(string id, RawText text, IReadOnlyList<RawMark> marks, Vector2 size)
    {
        if (FallbackShown is { } shown)
        {
            FallbackShown = null;
            shown();
        }

        fallbackText = text.Text;
        var listHeight = marks.Count == 0 ? 0 : MathF.Min(size.Y / 3, (marks.Count + 1) * ImGui.GetTextLineHeightWithSpacing());
        ImGui.InputTextMultiline(id, ref fallbackText, (uint)fallbackText.Length + 1,
            new Vector2(size.X, MathF.Max(ImGui.GetFrameHeight(), size.Y - listHeight)), ImGuiInputTextFlags.ReadOnly);
        if (marks.Count == 0 || !ImGui.BeginChild(id + "-marks", new Vector2(size.X, listHeight)))
        {
            if (marks.Count > 0)
            {
                ImGui.EndChild();
            }

            return;
        }

        foreach (var mark in marks)
        {
            ImGui.TextColored(MarkColor(mark), $"Line {mark.Line + 1}: {mark.Message.Split('\n')[0]}");
            ItemTooltip.Show(mark.Message);
        }

        ImGui.EndChild();
    }

    private static Vector4 MarkColor(RawMark mark) => mark.BaseGame ? EditorTheme.TextDim
        : mark.Severity == DiagnosticSeverity.Error ? EditorTheme.Error
        : EditorTheme.Warning;

    /// <summary>The colors of the editor from the theme of the editor, in the order of the native palette.</summary>
    private static uint[] Palette()
    {
        Vector4[] colors =
        [
            EditorTheme.Text, // text
            EditorTheme.AccentText, // keyword: actions and conditions
            new Vector4(0.62f, 0.80f, 0.86f, 1), // declaration: known keys
            new Vector4(0.68f, 0.80f, 0.62f, 1), // number
            new Vector4(0.86f, 0.70f, 0.52f, 1), // string: text placeholders
            EditorTheme.TextDim, // punctuation
            EditorTheme.TextBright, // preprocessor: section headers
            EditorTheme.Text, // identifier
            EditorTheme.Text, // known identifier
            EditorTheme.TextDim with { W = 0.75f }, // comment
            EditorTheme.Field, // background
            EditorTheme.TextBright, // cursor
            EditorTheme.Accent with { W = 0.45f }, // selection
            EditorTheme.InnerLine, // whitespace
            EditorTheme.Accent with { W = 0.25f }, // matching bracket background
            EditorTheme.AccentText, // matching bracket active
            EditorTheme.Text, EditorTheme.Text, EditorTheme.Text, // bracket levels
            EditorTheme.Error, // bracket error
            EditorTheme.TextDim, // line number
            EditorTheme.Text, // current line number
        ];
        return colors.Select(c => ImGui.GetColorU32(c)).ToArray();
    }

    /// <summary>True when the loaded cimgui library has an export.</summary>
    private static bool Probe(string name) =>
        NativeLibrary.TryLoad("cimgui", typeof(ColorTextEdit).Assembly, null, out var handle) &&
        NativeLibrary.TryGetExport(handle, name, out _);

    public void Dispose()
    {
        editor?.Dispose();
        editor = null;
        state.Reset();
    }
}
