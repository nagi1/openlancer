using System;
using LibreLancer;
using LibreLancer.ImageLib;

namespace OpenLancer.Runtime;

/// <summary>
/// The OpenLancer icon in the title bar and the task bar of the editor and Play windows (assets/icon/openlancer.svg).
/// </summary>
/// <remarks>
/// On Windows, SDL gives each window the first icon of the program file. That icon has a sharp image for each size,
/// from 16 to 256 pixels. One image from <see cref="Game.SetWindowIcon"/> would replace it, and Windows would scale
/// that image down. So the programs set the icon only on the other systems.
/// </remarks>
public static class ProgramIcon
{
    private const string ResourceName = "OpenLancer.Runtime.openlancer-256.png";

    /// <summary>Gives the window of the game its icon. Call it after the window exists, for example in
    /// <c>Load</c>.</summary>
    public static void Apply(Game game)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var image = Load();
        game.SetWindowIcon(image.Width, image.Height, Bgra8.BufferFromBytes(image.Data));
    }

    /// <summary>The icon image, with its pixels in the byte order of <see cref="Bgra8"/>.</summary>
    internal static Image Load()
    {
        using var stream = typeof(ProgramIcon).Assembly.GetManifestResourceStream(ResourceName) ??
                           throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        return Generic.ImageFromStream(stream);
    }
}
