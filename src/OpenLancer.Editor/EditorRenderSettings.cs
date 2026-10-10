using LibreLancer.Graphics;
using LibreLancer.Render;

namespace OpenLancer.Editor;

/// <summary>
/// Render settings for the scene view. A high LOD multiplier keeps far solars visible, because an editor shows the
/// whole system.
/// </summary>
internal sealed class EditorRenderSettings : IRendererSettings
{
    public AntialiasMode SelectedAA => AntialiasMode.MSAA4x;
    public TextureFiltering SelectedFiltering => TextureFiltering.Trilinear;
    public int SelectedAnisotropy => 0;
    public float LodMultiplier => 4;
    public bool PerPixelLighting => true;
}
