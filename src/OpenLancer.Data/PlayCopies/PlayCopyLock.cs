using System;
using System.IO;

namespace OpenLancer.Data.PlayCopies;

/// <summary>
/// The lock of a play copy, <c>.openlancer-playcopy.lock</c> in its root (section 8.2 of the M11 design, rule 10). The
/// file stays open with no sharing while the editor or oltool changes or checks the play copy. It also stays open while
/// the game or the test server of the play copy runs. So a second writer cannot open it.
/// </summary>
/// <remarks>The lock opens the file for read only. It never writes into a name of the play copy.</remarks>
internal sealed class PlayCopyLock : IDisposable
{
    public const string FileName = ".openlancer-playcopy.lock";

    private readonly FileStream stream;

    private PlayCopyLock(FileStream stream) => this.stream = stream;

    /// <summary>Takes the lock of a play copy root. The root must exist.</summary>
    /// <exception cref="PlayCopyException">Another window or program holds the lock.</exception>
    public static PlayCopyLock Take(string root)
    {
        try
        {
            return new PlayCopyLock(new FileStream(Path.Combine(root, FileName), FileMode.OpenOrCreate, FileAccess.Read,
                FileShare.None));
        }
        catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException))
        {
            throw new PlayCopyException(PlayCopyTexts.Locked(root));
        }
    }

    public void Dispose() => stream.Dispose();
}
