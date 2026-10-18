using System;
using System.ComponentModel;
using System.IO;
using LibreLancer;
using LibreLancer.Dialogs;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor.Games;

/// <summary>The folder picker and the file manager for the game link views. An error goes to the Console.</summary>
internal static class FolderActions
{
    /// <summary>
    /// Opens the folder picker of the system. On Windows it gives the folder before it returns, and on other systems
    /// later. So the folder goes to the UI thread in both cases. Cancel gives no folder.
    /// </summary>
    public static void Choose(EditorWindow window, Action<string> chosen)
    {
        try
        {
            FileDialog.ChooseFolder(path => window.QueueUIThread(() =>
            {
                if (path != null)
                {
                    chosen(path);
                }
            }));
        }
        catch (Exception e)
        {
            // The folder picker of the system can fail, for example without a desktop portal.
            window.Log.Add(ConsoleSeverity.Error, GameLinks.Source, $"The folder picker did not open: {e.Message}");
        }
    }

    /// <summary>Opens a folder in the file manager. A file path would start a program, so only a folder that exists
    /// opens.</summary>
    public static void Open(EditorWindow window, string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Shell.OpenCommand(path);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            window.Log.Add(ConsoleSeverity.Error, GameLinks.Source, $"The folder {path} did not open: {e.Message}");
        }
    }
}
