using System;
using LibreLancer;

namespace OpenLancer.Editor;

internal static class Program
{
    /// <summary>Starts the editor. The optional argument is a profile file (.json) to open.</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        EditorWindow? window = null;
        AppHandler.Run(() =>
        {
            window = new EditorWindow(args.Length > 0 ? args[0] : null);
            window.Run();
        }, () => window?.Crashed());
    }
}
