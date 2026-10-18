using System;
using System.Diagnostics;

namespace OpenLancer.Editor.Play;

/// <summary>
/// The local test server of a play copy (section 8.7 of the M11 design, decisions D315 and D336). It is FLServer of the
/// play copy, started with <c>/c</c> in the program folder. So FLServer uses its saved settings. The user joins it with
/// the game of the same play copy. <see cref="GamePlay"/> starts and stops it.
/// </summary>
/// <remarks>
/// When its main window closes, FLServer saves its characters and stops its server thread, which can take up to 60
/// seconds. So Stop test server closes the window and waits. Force stop is available at once, and the editor ends the
/// process itself only after 60 seconds.
/// </remarks>
internal static class TestServer
{
    /// <summary>The server program in the program folder of a game.</summary>
    public const string ProgramName = "FLServer.exe";

    /// <summary>The switch that starts FLServer with its saved settings, without its start dialog.</summary>
    public const string SettingsSwitch = "/c";

    /// <summary>The time that FLServer has to save and stop after its window closes.</summary>
    public static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(60);

    public const string Missing = "This game has no FLServer.exe. Host test server needs it.";

    public const string Started = "The test server started. The first start shows the dialog of FLServer. Enter a " +
                                  "server name and click OK. Then pick Play > Join test server, or start the game of " +
                                  "the play copy, and join the server in the multiplayer menu.";

    public const string Characters = "The test server saves its characters in Documents\\My Games\\Freelancer\\Accts\\" +
                                     "MultiPlayer, the folder of your normal server. Delete the test characters after " +
                                     "the test.";

    public const string Stopping = "Stopping the test server. FLServer saves its characters first.";

    public const string CloseQuestion = "The test server runs. Stop it and close OpenLancer?";

    public const string NoWindow = "FLServer has no window that OpenLancer can close. Click Force stop to end it.";

    /// <summary>The status text of a running server, for example "Test server: HD Edition 0.7.1".</summary>
    public static string Status(PlayTarget target) => $"Test server: {target.Name}";

    /// <summary>The start of FLServer: the program folder as the working folder, and the switch of the saved
    /// settings.</summary>
    public static ProcessStartInfo Command(string program, string programFolder)
    {
        var info = new ProcessStartInfo(program) { WorkingDirectory = programFolder, UseShellExecute = false };
        info.ArgumentList.Add(SettingsSwitch);
        return info;
    }
}
