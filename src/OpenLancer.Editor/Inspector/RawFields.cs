using System.Collections;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using LibreLancer;
using LibreLancer.Data;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// A section that shows each public field and property of an object, found by reflection. The "All fields (raw)"
/// section at the end of each Inspector page uses it, so each GameData field shows somewhere, also before a page shows
/// it. The section is closed until the user opens it.
/// </summary>
/// <param name="section">The second part of the section key "Type.Section".</param>
internal sealed class RawFields(PropertyGrid grid, string section, char icon, string title)
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.Instance;

    // A list shows this number of items and then "...".
    private const int ListLimit = 12;

    // The fields of the last item. The reflection runs again only for a different item.
    private object? item;
    private (string Name, string Value)[] fields = [];

    /// <summary>The "All fields (raw)" section of an Inspector page.</summary>
    public RawFields(PropertyGrid grid) : this(grid, "Raw", EditorIcons.Table, "All fields (raw)")
    {
    }

    /// <summary>Forgets the last item. Call it when the profile closes.</summary>
    public void Reset()
    {
        item = null;
        fields = [];
    }

    public void Draw(object selected)
    {
        if (!grid.BeginSection($"{selected.GetType().Name}.{section}", icon, title, false))
        {
            return;
        }

        if (!ReferenceEquals(selected, item))
        {
            item = selected;
            fields = Fields(selected);
        }

        foreach (var (name, value) in fields)
        {
            grid.Text(name, value);
        }

        grid.EndSection();
    }

    private static (string Name, string Value)[] Fields(object item) =>
        item.GetType().GetFields(Members).Select(f => (f.Name, Format(f.GetValue(item))))
            .Concat(item.GetType().GetProperties(Members)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                .Select(p => (p.Name, Format(SafeGet(p, item)))))
            .Where(x => x.Name != nameof(NicknameItem.Nickname))
            .ToArray();

    private static object? SafeGet(PropertyInfo property, object item)
    {
        try
        {
            return property.GetValue(item);
        }
        catch (TargetInvocationException e)
        {
            return $"({e.InnerException?.GetType().Name ?? "error"})";
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        Vector3 v => $"{Format(v.X)}, {Format(v.Y)}, {Format(v.Z)}",
        Vector2 v => $"{Format(v.X)}, {Format(v.Y)}",
        Quaternion q => EulerDegrees(q),
        Color4 c => $"{Format(c.R)}, {Format(c.G)}, {Format(c.B)}, {Format(c.A)}",
        NicknameItem n => n.Nickname,
        IEnumerable e and not IDictionary => $"[{string.Join(", ", e.Cast<object?>().Take(ListLimit).Select(Format))}" +
                                              $"{(e.Cast<object?>().Skip(ListLimit).Any() ? ", ..." : "")}]",
        _ => value.ToString() ?? "",
    };

    private static string EulerDegrees(Quaternion q)
    {
        var euler = q.GetEulerDegrees();
        return $"{Format(euler.X)}, {Format(euler.Y)}, {Format(euler.Z)} (degrees)";
    }
}
