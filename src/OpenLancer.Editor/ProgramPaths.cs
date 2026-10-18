using System;
using System.IO;
using LibreLancer;
using OpenLancer.Data;

namespace OpenLancer.Editor;

/// <summary>
/// The programs that the editor starts. The build puts each one next to the editor, because the editor project
/// references its project.
/// </summary>
internal static class ProgramPaths
{
    private const string PlayName = "OpenLancer.Play";

    /// <summary>oltool, which validates a save.</summary>
    public static string Oltool => NextToEditor("oltool");

    /// <summary>The game process of Play.</summary>
    public static string Play => NextToEditor(PlayName);

    /// <summary>
    /// The log file of the game process of Play. <see cref="AppHandler"/> writes it with the name of the program, in
    /// the logs folder of the product.
    /// </summary>
    public static string PlayLog => Path.Combine(AppHandler.LogFolder(Product.Name), PlayName + ".log.txt");

    private static string NextToEditor(string name) =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
}
