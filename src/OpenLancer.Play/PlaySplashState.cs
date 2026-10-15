using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.ImageLib;

namespace OpenLancer.Play;

/// <summary>
/// A state of the play that shows the splash image while it waits, as <c>LoadingDataState</c> shows it: before the
/// start of the play and during a reload.
/// </summary>
/// <remarks>
/// The state loads the image itself and frees it at its unload. <c>GameDataManager.GetSplashScreen</c> gives a texture of
/// the resource manager, and <c>SpaceGameplay</c> clears those textures when it loads. After that, the resource manager
/// cannot load the image again: it reads the image file as a UTF file and throws. So a reload after a flight in space
/// would end the game.
/// </remarks>
internal abstract class PlaySplashState : GameState
{
    /// <summary>The splash images of the game data, the large one first, as <c>GetSplashScreen</c> looks for them.</summary>
    private static readonly string[] SplashFiles = ["startupscreen_1280.tga", "startupscreen.tga"];

    private readonly Texture2D? splash;

    protected PlaySplashState(FreelancerGame game) : base(game) => splash = LoadSplash(game);

    public override void Draw(double delta)
    {
        Game.RenderContext.ClearColor = Color4.Black;
        Game.RenderContext.ClearAll();
        if (splash == null)
            return;
        var list = Game.RenderContext.Renderer2D.CreateDrawList();
        list.DrawImageStretched(splash, new Rectangle(0, 0, Game.Width, Game.Height), Color4.White, true);
        list.Render();
    }

    protected override void OnUnload() => splash?.Dispose();

    /// <summary>The first splash image that the game data has, or null.</summary>
    private static Texture2D? LoadSplash(FreelancerGame game)
    {
        var items = game.GameData.Items;
        foreach (var name in SplashFiles)
        {
            var path = items.Ini.Freelancer.DataPath + "INTERFACE/INTRO/IMAGES/" + name;
            if (!items.VFS.FileExists(path))
                continue;
            using var stream = items.VFS.Open(path);
            return Generic.TextureFromStream(game.RenderContext, stream) as Texture2D;
        }
        return null;
    }
}
