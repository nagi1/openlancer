using System;
using LibreLancer;

namespace OpenLancer.Editor.Cutscene;

/// <summary>The screen ratio of the Shot view: the ratio of the panel, or a fixed ratio of a game screen.</summary>
internal enum ShotAspect
{
    Window,
    FourThree,
    SixteenNine,
}

/// <summary>
/// The rectangles of the Shot view in a panel (section 7.6 of the M8 design). The game draws a cutscene into a
/// letterboxed viewport and gives the camera the ratio of the whole screen (RoomGameplay.cs:1570-1585,
/// SpaceGameplay.cs:1983-1995). The THN camera takes the vertical view from the viewport and widens the horizontal view
/// by the screen ratio (ThnCamera.cs:60-75). So the view copies the rectangles of the game.
/// </summary>
/// <param name="Screen">S: the largest rectangle of the screen ratio in the panel, centered. The rest of the panel is
/// black.</param>
/// <param name="Viewport">The rectangle that the scene draws into. It is also the safe frame.</param>
/// <param name="ScreenAspect">R: the ratio that the view gives to <c>Cutscene.UpdateViewport</c>.</param>
internal readonly record struct ShotViewport(Rectangle Screen, Rectangle Viewport, float ScreenAspect)
{
    /// <summary>The letterbox factor of the game: the cutscene viewport is S.height / 1.39 high (RoomGameplay.cs:1574).
    /// </summary>
    public const double Letterbox = 1.39;

    /// <summary>The names of the ratios for the aspect menu.</summary>
    public static string Name(ShotAspect aspect) => aspect switch
    {
        ShotAspect.FourThree => "4:3",
        ShotAspect.SixteenNine => "16:9",
        _ => "Window",
    };

    /// <summary>
    /// The rectangles in a panel. When a cutscene plays, the viewport is S.width x (S.height / 1.39), centered in S, with
    /// the integer steps of the game. When a room plays without a cutscene, the game sets no letterbox, so the viewport
    /// is S (RoomGameplay.cs:1489-1494).
    /// </summary>
    /// <param name="panel">The panel, in pixels. It has a width and a height of at least 1.</param>
    /// <param name="cutscene">True when a cutscene plays.</param>
    public static ShotViewport Of(Rectangle panel, ShotAspect aspect, bool cutscene)
    {
        if (panel.Width < 1 || panel.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(panel), panel, "The panel must have a width and a height.");
        }

        // A double ratio, so that 16:9 in a panel 800 pixels wide gives 450 pixels, not 449.
        var ratio = aspect switch
        {
            ShotAspect.FourThree => 4.0 / 3.0,
            ShotAspect.SixteenNine => 16.0 / 9.0,
            _ => (double)panel.Width / panel.Height,
        };

        // The largest rectangle of the ratio that fits in the panel. A side of less than 1 pixel becomes 1 pixel.
        int width, height;
        if ((double)panel.Width / panel.Height > ratio)
        {
            height = panel.Height;
            width = Math.Clamp((int)(height * ratio), 1, panel.Width);
        }
        else
        {
            width = panel.Width;
            height = Math.Clamp((int)(width / ratio), 1, panel.Height);
        }

        var screen = new Rectangle(panel.X + (panel.Width - width) / 2, panel.Y + (panel.Height - height) / 2, width,
            height);
        if (!cutscene)
        {
            return new ShotViewport(screen, screen, (float)ratio);
        }

        // The formula of RoomGameplay.cs:1574-1579 with the letterbox amount 1.
        var newRatio = (double)width / height * Letterbox;
        var newHeight = width / newRatio;
        var difference = height - newHeight;
        var viewport = new Rectangle(screen.X, screen.Y + (int)(difference / 2), width,
            Math.Max(1, (int)(height - difference)));
        return new ShotViewport(screen, viewport, (float)ratio);
    }
}
