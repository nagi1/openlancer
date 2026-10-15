namespace OpenLancer.Runtime.Play;

/// <summary>
/// The exit codes of the play process (section 6.6 of the M4 design). The play process gives them, and the editor
/// reads them. An error event comes before each code other than <see cref="Ok"/>. Another code is a crash that ended
/// the process: an unhandled exception in a Debug build or on a thread other than the UI thread. An error event with
/// <c>crash</c> usually comes before it. A stack overflow or a crash in native code ends the process without the
/// event.
/// </summary>
public static class PlayExitCodes
{
    /// <summary>The game ended normally: stop, the end of the input, the window close or Exit in the game.</summary>
    public const int Ok = 0;

    /// <summary>An exception on the UI thread ended the game, and the crash window of a Release build closed.</summary>
    public const int Crash = 1;

    /// <summary>The arguments are not valid. The editor made them, so this is a bug.</summary>
    public const int Arguments = 2;

    /// <summary>The data cannot start the play: merge errors, a save of a mod that is not complete, or an unknown
    /// mod, system, base or ship package.</summary>
    public const int Data = 3;

    /// <summary>A blocking wait of the play host did not end in its limit (section 10.5): the autosave or the server
    /// stop of a reload. A server thread that does not end cannot continue the play.</summary>
    public const int Timeout = 4;

    /// <summary>The engine returned before the play ran: a system library is missing (the engine then shows a message
    /// window), or SDL, the game window or its graphics context did not start.</summary>
    public const int NotStarted = 5;
}
