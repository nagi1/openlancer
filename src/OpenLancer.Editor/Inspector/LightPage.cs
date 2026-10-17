using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a light source. It shows only the fields that the kind of the light uses: attenuation for a
/// point light, direction for a directional light and a spotlight, the cone for a spotlight, and the attenuation curve
/// when the light names one. With the section of the light in W, the rows edit its keys, and the page ends with the keys
/// that no row shows, and the panel adds the raw INI block (see <see cref="SectionExtras"/>).
/// </summary>
/// <remarks>In a bound scope, the attenuation, the direction and the curve show when the section has a line of their
/// key, and not from the kind of the light (see <see cref="Rows"/>). An INI light is a point or a directional light
/// (Schema/Universe/LightType.cs).</remarks>
internal sealed class LightPage(EditorWindow window, PropertyGrid grid)
{
    private readonly SectionExtras extras = new(window, grid);

    /// <param name="section">The section of the light in W, or null when the edit view has none.</param>
    public void Draw(LightSource source, SectionRef? section)
    {
        using (grid.Bind(section))
        {
            Identity(source);
            Light(source);
        }

        if (section != null)
        {
            extras.Draw([section], "LightSource", moreKeys: true);
        }
    }

    private void Identity(LightSource source)
    {
        if (!grid.BeginSection("LightSource.Identity", EditorIcons.Tag, "Identity"))
        {
            return;
        }

        grid.Text("Nickname", source.Nickname, "nickname");
        grid.EndSection();
    }

    private void Light(LightSource source)
    {
        if (!grid.BeginSection("LightSource.Light", EditorIcons.Lightbulb, "Light"))
        {
            return;
        }

        var light = source.Light;
        var rows = Rows(grid.Layout, source);
        // A bound row shows the INI type, and the engine reads a light without a type as a point light
        // (GameItemDb.cs:1503-1506).
        grid.Text("Kind", grid.IsBound ? "POINT" : light.Kind.ToString(), "type");
        grid.Vector("Position", light.Position, "pos");
        grid.Color("Color", light.Color.ToColor4(), "color");
        grid.Number("Range", light.Range, iniKey: "range");
        if (rows.Attenuation)
        {
            grid.Vector("Attenuation", light.Attenuation, "attenuation");
        }

        if (rows.Direction)
        {
            grid.Vector("Direction", light.Direction, "direction");
        }

        if (light.Kind == LightKind.Spotlight)
        {
            // No INI key sets the cone.
            using (grid.Bind(null))
            {
                grid.Number("Falloff", light.Falloff, iniKey: "falloff");
                grid.Number("Theta", light.Theta, iniKey: "theta");
                grid.Number("Phi", light.Phi, iniKey: "phi");
            }
        }

        // A curve replaces the attenuation values. Directional lights can also name one.
        if (rows.Curve)
        {
            grid.Text("Attenuation curve", source.AttenuationCurveName, "atten_curve");
        }

        grid.EndSection();
    }

    /// <summary>
    /// The rows of the light that not every light has: the attenuation of a point light, the direction of a directional
    /// light or a spotlight, and the attenuation curve. Without a bound section, the light of the game data decides. In
    /// a bound scope, only the lines of the keys decide (see <see cref="PropertyGrid.Shows(bool, string[])"/>), because
    /// an edit of a key changes the light of the scene at once. For example, the curve row would come when the text of
    /// an <c>atten_curve</c> field in "More keys" names a graph, and that field would end.
    /// </summary>
    /// <param name="layout">The section that a bound page lays out its rows from, or null without a bound
    /// section.</param>
    internal static (bool Attenuation, bool Direction, bool Curve) Rows(SectionView? layout, LightSource source) =>
    (
        PropertyGrid.Shows(layout, source.Light.Kind == LightKind.Point, "attenuation"),
        PropertyGrid.Shows(layout, source.Light.Kind is LightKind.Directional or LightKind.Spotlight, "direction"),
        PropertyGrid.Shows(layout, source.AttenuationCurveName != null, "atten_curve")
    );
}
