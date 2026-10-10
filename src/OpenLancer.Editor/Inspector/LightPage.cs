using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a light source. It shows only the fields that the kind of the light uses: attenuation for a
/// point light, direction for a directional light and a spotlight, the cone for a spotlight, and the attenuation curve
/// when the light names one.
/// </summary>
internal sealed class LightPage(PropertyGrid grid)
{
    public void Draw(LightSource source)
    {
        if (grid.BeginSection("LightSource.Identity", EditorIcons.Tag, "Identity"))
        {
            grid.Text("Nickname", source.Nickname, "nickname");
            grid.EndSection();
        }

        if (!grid.BeginSection("LightSource.Light", EditorIcons.Lightbulb, "Light"))
        {
            return;
        }

        var light = source.Light;
        grid.Enum("Kind", light.Kind, "type");
        grid.Vector("Position", light.Position, "pos");
        grid.Color("Color", light.Color.ToColor4(), "color");
        grid.Number("Range", light.Range, iniKey: "range");
        switch (light.Kind)
        {
            case LightKind.Point:
                grid.Vector("Attenuation", light.Attenuation, "attenuation");
                break;
            case LightKind.Directional:
                grid.Vector("Direction", light.Direction, "direction");
                break;
            case LightKind.Spotlight:
                grid.Vector("Direction", light.Direction, "direction");
                grid.Number("Falloff", light.Falloff, iniKey: "falloff");
                grid.Number("Theta", light.Theta, iniKey: "theta");
                grid.Number("Phi", light.Phi, iniKey: "phi");
                break;
        }

        // A curve replaces the attenuation values. Directional lights can also name one.
        if (source.AttenuationCurveName != null)
        {
            grid.Text("Attenuation curve", source.AttenuationCurveName, "atten_curve");
        }

        grid.EndSection();
    }
}
