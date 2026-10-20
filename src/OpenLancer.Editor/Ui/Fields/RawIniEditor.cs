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
        var setText = !ReferenceEquals(text, newText) && !string.Equals(text, newText, StringComparison.Ordinal);
        var setMarks = setText || marks == null || !ReferenceEquals(marks, newMarks) && !marks.SequenceEqual(newMarks);
        text = newText;
        marks = newMarks;
        return (setText, setMarks);
    }

    /// <summary>Forgets the text, so that the next frame sets it again.</summary>
    public void Reset() => (text, marks) = (null, null);
}

/// <summary>The names that the INI mode colors. The keywords are for example the actions and conditions of a mission.
/// The declarations are for example the known keys of the section types. They compare without letter case.</summary>
internal sealed record IniVocabulary(IReadOnlyList<string> Keywords, IReadOnlyList<string> Declarations);

/// <summary>
/// The raw INI editor (AGENTS.md UI rule 11, section 11.5 of the M6a design): the vendored color text editor in its INI
/// mode, with the marks of the checks on the lines. A line with an error has the error color, a line with a warning has
/// the warning color, and a line with only base-game problems or with a note has a muted color. The tooltip is the
/// message of the check, the same text as on the field.
/// </summary>
/// <remarks>
/// <para>Each editor has the vocabulary of its data, for example the names of the mission schema. The native INI mode
/// keeps one set of names for all editors. So an editor sets its vocabulary again after another editor set
/// another one.</para>
/// <para>When the loaded <c>cimgui.dll</c> lacks the exports of OL-0060, the editor shows the text in a read-only field
/// with a list of the marks under it, as OL-0019 does for ImGuizmo.</para>
/// </remarks>
/// <param name="vocabulary">The names that the editor colors, or null for none.</param>
internal sealed class RawIniEditor(IniVocabulary? vocabulary = null) : IDisposable
{
    /// <summary>The Console text when the natives lack the exports (section 13.1 of the M6a design).</summary>
    public const string NeedsNewNatives =
        "The raw INI editor needs a newer cimgui.dll. The INI text shows without colors and marks. Build the natives " +
        "again. See \"Build the submodule\" in docs/UPSTREAM.md.";

    private static bool? nativeReady;

    // The vocabulary that the native INI mode has now. It is one set for all editors.
    private static IniVocabulary? nativeVocabulary;

    private readonly RawIniEditorState state = new();
    private readonly RawIniEdit edit = new();
    private ColorTextEdit? editor;
    private bool? readOnly;
    // The content and the commit of the open edit, or of the last draw when no edit is open.
    private (string Key, Action<string, string> Commit)? target;
    private int? goToLine;
    private string fallbackText = "";

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

    /// <summary>True while the user edits the text of an editable editor.</summary>
    public bool IsEditing => edit.IsOpen;

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

        var native = Native(readOnly: true);
        Show(native, text, marks);
        Render(native, id, size);
    }

    /// <summary>
    /// Draws the editor with the text and the marks. The user can edit the text when <paramref name="readOnlyReason"/>
    /// is null (section 8.8 of the M6b design). The edit ends when the editor loses the focus, when the editor draws
    /// another content, or at <see cref="EndEdit"/>. While the user types, the text and the marks of the arguments
    /// wait.
    /// </summary>
    /// <param name="key">The content of the editor, for example the path of a script. The commit of an edit is the
    /// commit of the content at the start of the edit.</param>
    /// <param name="readOnlyReason">Why the user cannot edit the text, or null.</param>
    /// <param name="commit">Gets the start text and the end text of an edit at its end.</param>
    /// <param name="draft">Gives the marks of the text of the editor while the user types, or null.</param>
    public void DrawEditable(string id, string key, RawText text, IReadOnlyList<RawMark> marks, Vector2 size,
        string? readOnlyReason, Action<string, string> commit, Func<string, IReadOnlyList<RawMark>>? draft = null)
    {
        if (!NativeReady)
        {
            Fallback(id, text, marks, size);
            return;
        }

        // A click on another item can show another content before the editor loses the focus.
        if (edit.IsOpen && target?.Key != key)
        {
            EndEdit();
        }

        if (!edit.IsOpen)
        {
            target = (key, commit);
        }

        var native = Native(readOnlyReason != null);
        if (!edit.IsOpen)
        {
            Show(native, text, marks);
        }

        var focused = Render(native, id, size);
        if (readOnlyReason != null)
        {
            ImGui.SetItemTooltip($"{EditorIcons.Lock} {readOnlyReason}");
        }

        var changed = native.TextChanged();
        var ended = edit.Frame(text.Text, changed, native.GetText, focused);
        if (ended is var (start, end))
        {
            Commit(start, end);
        }
        else if (edit.IsOpen && changed && draft != null)
        {
            SetMarks(native, draft(native.GetText()));
        }
    }

    /// <summary>Ends an open edit now, for example before a save, and gives it to the commit of the last draw.</summary>
    public void EndEdit()
    {
        if (editor != null && edit.End(editor.GetText) is var (start, end))
        {
            Commit(start, end);
        }
    }

    private void Commit(string start, string end)
    {
        // The next draw shows the text of the arguments again: the text after the edit, or the text before a refusal.
        state.Reset();
        target?.Commit(start, end);
    }

    /// <summary>The native editor in the read-only state, with the vocabulary of this editor.</summary>
    private ColorTextEdit Native(bool readOnly)
    {
        if (editor == null)
        {
            editor = new ColorTextEdit();
            editor.SetMode(ColorTextEditMode.Ini);
            editor.SetPalette(Palette());
        }

        if (this.readOnly != readOnly)
        {
            editor.SetReadOnly(readOnly);
            this.readOnly = readOnly;
        }

        if (vocabulary != null && !ReferenceEquals(nativeVocabulary, vocabulary))
        {
            editor.SetIdentifiers(ColorTextEditIdentifiers.Keywords, vocabulary.Keywords);
            editor.SetIdentifiers(ColorTextEditIdentifiers.Declarations, vocabulary.Declarations);
            nativeVocabulary = vocabulary;
        }

        return editor;
    }

    /// <summary>Gives the native editor a new text and new marks when they changed.</summary>
    private void Show(ColorTextEdit native, RawText text, IReadOnlyList<RawMark> marks)
    {
        var (setText, setMarks) = state.Plan(text.Text, marks);
        if (setText)
        {
            native.SetText(text.Text);
        }

        if (setMarks)
        {
            SetMarks(native, marks);
        }
    }

    private static void SetMarks(ColorTextEdit native, IReadOnlyList<RawMark> marks)
    {
        native.ClearMarkers();
        foreach (var mark in marks)
        {
            var color = MarkColor(mark);
            native.AddMarker(mark.Line, ImGui.GetColorU32(color), ImGui.GetColorU32(color with { W = 0.18f }),
                mark.Message, mark.Message);
        }
    }

    /// <summary>Renders the editor in a child region. Returns true while the editor has the focus.</summary>
    private bool Render(ColorTextEdit native, string id, Vector2 size)
    {
        if (goToLine is { } line)
        {
            native.SetCursor(line, 0);
            native.SelectLine(line);
            native.ScrollToLine(line);
            goToLine = null;
        }

        var focused = false;
        if (ImGui.BeginChild(id, size))
        {
            native.Render(id);
            focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        }

        ImGui.EndChild();
        return focused;
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

    private static Vector4 MarkColor(RawMark mark) => mark.BaseGame || mark.Note ? EditorTheme.TextDim
        : mark.Severity == DiagnosticSeverity.Error ? EditorTheme.Error
        : EditorTheme.Warning;

    /// <summary>The colors of the editor from the theme of the editor, in the order of the native palette.</summary>
    private static uint[] Palette()
    {
        Vector4[] colors =
        [
            EditorTheme.Text, // text
            EditorTheme.AccentText, // keyword: actions and conditions
            EditorTheme.SyntaxKey, // declaration: known keys
            EditorTheme.SyntaxNumber, // number
            EditorTheme.SyntaxString, // string: text placeholders
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
        readOnly = null;
        state.Reset();
        edit.Reset();
    }
}
