using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using LibreLancer.Dialogs;
using LibreLancer.ImUI;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>
/// The first-run screen on the Welcome page (section 7 of the M11a design): link the game folder, check it, and make a
/// profile with an empty mod that opens ready to edit. Screen A asks for the folder, B checks it, C shows the game and
/// the new profile, and D tells why a folder holds no game.
/// </summary>
internal sealed class FirstRunCard(EditorWindow window)
{
    private const float ButtonHeight = 32;
    private const int MaxFindings = 5;

    private readonly NewProfileForm form = new(window);
    private Screen screen = Screen.Link;
    private string folder = "";
    private GameReport? report;
    private CancellationTokenSource? cancel;

    private enum Screen
    {
        Link,
        Check,
        Found,
        NoGame,
    }

    /// <summary>True after Skip. The normal Welcome card then shows until the next start.</summary>
    public bool Skipped { get; private set; }

    /// <summary>Checks a folder from the folder picker or from a drop on the window: screen B, then C or D.</summary>
    public void Check(string path)
    {
        cancel?.Cancel();
        var token = new CancellationTokenSource();
        cancel = token;
        folder = path;
        screen = Screen.Check;
        window.Links.Detect(path, token.Token, found =>
        {
            if (token != cancel)
            {
                return;
            }

            report = found;
            screen = found.IsGame ? Screen.Found : Screen.NoGame;
            if (found.IsGame)
            {
                form.Reset([found]);
            }
        });
    }

    /// <summary>Draws the screen in the Welcome card.</summary>
    public void Draw()
    {
        switch (screen)
        {
            case Screen.Check:
                CheckScreen();
                break;
            case Screen.Found when report != null:
                FoundScreen(report);
                break;
            case Screen.NoGame when report != null:
                NoGameScreen(report);
                break;
            default:
                LinkScreen();
                break;
        }
    }

    /// <summary>Screen A: link the game.</summary>
    private void LinkScreen()
    {
        Title("Link your Freelancer game");
        Lines(
            "OpenLancer needs the files of your own copy of Freelancer (2003).",
            "OpenLancer does not include game files.",
            "Select the folder of your game. This folder contains the EXE and DATA folders.",
            @"Example: C:\Program Files (x86)\Microsoft Games\Freelancer",
            "OpenLancer reads the files in this folder. It does not change them.");
        Gap();
        if (PrimaryButton($"{EditorIcons.Folder}  Select game folder..."))
        {
            ChooseFolder();
        }

        Caption("You can also drag the game folder to this window.");
        Gap();
        Lines("OpenLancer can identify Freelancer 1.0 and 1.1, with or without JFLP, and Freelancer: HD Edition.");
        Gap();
        if (ImGui.TextLink("Open a profile file..."))
        {
            window.OpenProfileDialog();
        }

        ImGui.SameLine(0, MathF.Truncate(16 * ImGuiHelper.Scale));
        SkipLink();
    }

    /// <summary>Screen B: the check runs.</summary>
    private void CheckScreen()
    {
        Title("Game folder check");
        Lines($"OpenLancer reads the files in {folder}. This can take some seconds.");
        Gap();
        ImGui.ProgressBar(-(float)ImGui.GetTime(), new Vector2(MathF.Truncate(240 * ImGuiHelper.Scale), 0), "");
        ImGuiHelper.AnimatingElement();
        Gap();
        if (ImGui.Button("Cancel"))
        {
            cancel?.Cancel();
            cancel = null;
            screen = Screen.Link;
        }
    }

    /// <summary>Screen C: the game, its findings and the new profile.</summary>
    private void FoundScreen(GameReport found)
    {
        Title(found.Severity == GameSeverity.Ok ? "Game found" : "Game found, with warnings");
        var (glyph, color) = GameLook.Of(found.Severity);
        ImGui.TextColored(color, glyph.ToString());
        ImGui.SameLine();
        ImGui.Text(found.Name);
        Lines($"Folder: {found.Root}");
        foreach (var finding in found.Findings.Take(MaxFindings))
        {
            Finding(finding);
        }

        if (found.Findings.Count > MaxFindings)
        {
            Lines($"Details shows {found.Findings.Count - MaxFindings} more lines.");
        }

        if (found is { Data: DataState.NotChanged, Findings.Count: 0, Reference: { } reference })
        {
            Lines($"The INI files are the same as the files of {GameDetector.BuildName(reference)}.");
        }

        Details(found);
        if (window.Links.Find(found.Root!) != null)
        {
            Lines("OpenLancer has a link to this game already. You can make one more profile for it.");
        }

        Gap();
        Heading("New profile");
        form.Draw();
        Gap();
        if (PrimaryButton("Create profile and open", form.CanCreate))
        {
            form.Create();
        }

        ImGui.SameLine();
        if (ImGui.Button("Select another folder", new Vector2(0, MathF.Truncate(ButtonHeight * ImGuiHelper.Scale))))
        {
            ChooseFolder();
        }
    }

    /// <summary>Screen D: no game in the folder.</summary>
    private void NoGameScreen(GameReport found)
    {
        Title("No game in this folder");
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.TimesCircle}  {found.Error}");
        ImGui.PopTextWrapPos();
        Lines("Select the folder that contains the EXE and DATA folders of the game.");
        Gap();
        if (PrimaryButton($"{EditorIcons.Folder}  Select another folder"))
        {
            ChooseFolder();
        }

        Gap();
        SkipLink();
    }

    private void SkipLink()
    {
        if (ImGui.TextLink("Skip"))
        {
            cancel?.Cancel();
            Skipped = true;
        }

        ImGui.SetItemTooltip("Shows the normal Welcome page for this start.");
    }

    /// <summary>
    /// Opens the folder picker of the system. On Windows it gives the folder before it returns, and on other systems
    /// later. So the folder goes to the UI thread in both cases.
    /// </summary>
    private void ChooseFolder()
    {
        try
        {
            FileDialog.ChooseFolder(path => window.QueueUIThread(() =>
            {
                if (path != null)
                {
                    Check(path);
                }
            }));
        }
        catch (Exception e)
        {
            window.Log.Add(ConsoleSeverity.Error, GameLinks.Source, $"The folder picker did not open: {e.Message}");
        }
    }

    /// <summary>The collapsed Details section: the findings and the evidence of the check.</summary>
    internal static void Details(GameReport found)
    {
        if (!ImGui.TreeNodeEx("Details", ImGuiTreeNodeFlags.NoTreePushOnOpen))
        {
            return;
        }

        ImGui.Indent();
        foreach (var finding in found.Findings)
        {
            Finding(finding);
        }

        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
        ImGui.PushTextWrapPos(0);
        foreach (var line in found.Evidence)
        {
            ImGui.TextWrapped(line);
        }

        foreach (var file in found.DataFiles)
        {
            ImGui.TextWrapped(file);
        }

        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
        ImGui.Unindent();
    }

    /// <summary>One finding with the glyph of its severity.</summary>
    internal static void Finding(GameFinding finding)
    {
        var (glyph, color) = GameLook.Of(finding.Severity);
        ImGui.TextColored(color, glyph.ToString());
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0);
        ImGui.TextWrapped(finding.Text);
        ImGui.PopTextWrapPos();
    }

    private static void Title(string text)
    {
        ImGui.PushFont(null, EditorTheme.HeroSize);
        ImGui.Text(text);
        ImGui.PopFont();
        Gap();
    }

    private static void Heading(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.SeparatorText(text);
        ImGui.PopStyleColor();
    }

    private static void Lines(params string[] lines)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.PushTextWrapPos(0);
        foreach (var line in lines)
        {
            ImGui.TextWrapped(line);
        }

        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static void Caption(string text)
    {
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        ImGui.TextDisabled(text);
        ImGui.PopFont();
    }

    private static void Gap() => ImGui.Dummy(new Vector2(0, MathF.Truncate(8 * ImGuiHelper.Scale)));

    /// <summary>The primary button of a screen.</summary>
    private static bool PrimaryButton(string label, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        ImGui.PushStyleColor(ImGuiCol.Button, EditorTheme.AccentStrong);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, EditorTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, EditorTheme.Pressed);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        var clicked = ImGui.Button(label, new Vector2(0, MathF.Truncate(ButtonHeight * ImGuiHelper.Scale)));
        ImGui.PopStyleColor(4);
        ImGui.EndDisabled();
        return clicked;
    }
}
