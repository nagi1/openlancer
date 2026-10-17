using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// The color picker of a color row: the form of a key, the picker color of a line, and the numbers that a picker
/// color gives (section 8 of the M20 design).
/// </summary>
/// <remarks>
/// The picker works in colors from 0 to 1. A Byte line gets whole numbers from 0 to 255, and a Unit line gets numbers
/// from 0 to 1 with 3 decimals. A channel whose rounded value does not change keeps its start value, also a value that
/// is not on the rounding grid. So the float noise of the picker does not change a channel that the user did not
/// touch, and the color of the opening gives the start line back.
/// </remarks>
internal static class ColorForms
{
    private const int Channels = 3;
    private const int UnitDecimals = 3;

    /// <summary>The form of a color key, or null when the key gets no picker: a key that is not a color, for example
    /// the <c>Vector3</c> <c>color_shift</c>, and a multiline color key.</summary>
    public static ColorForm? Of(KeySchema? key) => key switch
    {
        { Multiline: true } or null => null,
        _ when key.ClrType == typeof(Color4) => ColorForm.Byte,
        _ when key.ClrType == typeof(Color3f) => key.FloatColor ? ColorForm.Unit : ColorForm.Byte,
        _ => null,
    };

    /// <summary>The highest value of a channel in a form.</summary>
    public static float Maximum(ColorForm form) => form == ColorForm.Byte ? 255 : 1;

    /// <summary>
    /// The picker color of a line, or the reason that the line has no picker. The line needs 3 values or more, and its
    /// first 3 values must be finite numbers inside the range of the form. A fourth value can be any value, because the
    /// picker does not change it.
    /// </summary>
    public static (Vector3? Color, string? Reason) ToPicker(IReadOnlyList<ValueBase> line, ColorForm form)
    {
        if (line.Count < Channels)
            return (null, "The line has fewer than 3 values. The engine ignores it.");
        return FieldDrag.NumbersOf(line, Channels, out var numbers) is { } reason ? (null, reason) : ToPicker(numbers, form);
    }

    /// <summary>The picker color of the first 3 numbers of a line, or the reason that the line has no picker: a
    /// number outside the range of the form.</summary>
    public static (Vector3? Color, string? Reason) ToPicker(IReadOnlyList<float> numbers, ColorForm form)
    {
        for (var i = 0; i < Channels; i++)
        {
            if (numbers[i] < 0 || numbers[i] > Maximum(form))
            {
                return (null, $"Value {i + 1} is outside the range from 0 to {Maximum(form)}. The picker writes only " +
                              "values in that range. Correct the line in the field.");
            }
        }

        return (ColorOf(numbers, form), null);
    }

    /// <summary>The picker color of the first 3 numbers of a line: each number divided by 255 in the Byte form, and
    /// as it is in the Unit form.</summary>
    public static Vector3 ColorOf(IReadOnlyList<float> numbers, ColorForm form)
    {
        var divisor = form == ColorForm.Byte ? 255f : 1f;
        return new Vector3(numbers[0] / divisor, numbers[1] / divisor, numbers[2] / divisor);
    }

    /// <summary>
    /// The 3 numbers that a picker color gives. A channel whose rounded value equals the rounded value of the start
    /// color writes its start number. Each other channel writes its rounded value: Byte form
    /// <c>clamp(round(f x 255), 0, 255)</c>, Unit form <c>clamp(round(f, 3), 0, 1)</c>, a half away from 0.
    /// </summary>
    /// <param name="color">The color of the picker now.</param>
    /// <param name="startColor">The picker color of the start line (see <see cref="ColorOf"/>).</param>
    /// <param name="start">The first 3 numbers of the start line.</param>
    public static float[] FromPicker(Vector3 color, Vector3 startColor, IReadOnlyList<float> start, ColorForm form)
    {
        var result = new float[Channels];
        for (var i = 0; i < Channels; i++)
        {
            var rounded = Round(Channel(color, i), form);
            result[i] = rounded == Round(Channel(startColor, i), form) ? start[i] : rounded;
        }

        return result;
    }

    /// <summary>The rounded value of a picker channel in a form.</summary>
    private static float Round(float channel, ColorForm form)
    {
        var f = float.IsNaN(channel) ? 0 : Math.Clamp(channel, 0f, 1f);
        if (form == ColorForm.Byte)
            return (float)Math.Clamp(Math.Round(f * 255d, MidpointRounding.AwayFromZero), 0, 255);
        // The exact text of the float, so that a value of 3 decimals rounds to itself.
        var exact = FieldDrag.Exact(f) ?? (decimal)f;
        var rounded = Math.Clamp(decimal.Round(exact, UnitDecimals, MidpointRounding.AwayFromZero), 0m, 1m);
        return float.Parse(rounded.ToString(CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }

    private static float Channel(Vector3 color, int index) => index switch
    {
        0 => color.X,
        1 => color.Y,
        _ => color.Z,
    };
}

/// <summary>
/// The state of the one color picker of the Inspector: one opening of the popup is one field edit (section 8.4 of the
/// M20 design). It gives the events of the field edit (see <see cref="FieldEditRouter"/>).
/// </summary>
/// <remarks>
/// <para>The editor reads Escape itself, because ImGui does not close a popup on Escape without keyboard navigation.
/// Escape always puts back the color of the opening, also while an input of the picker is active, and it wins over a
/// close in the same frame. A click outside the popup closes it and keeps the color.</para>
/// <para>Every color row of the grid calls <see cref="Frame"/> in each frame. A call of another row changes nothing,
/// unless the popup of that row is open. A row that did not draw in the last frame lost its session.</para>
/// </remarks>
internal sealed class PickerSession
{
    private static readonly FieldEvents None = default;

    private bool isOpen;
    private uint id;
    private int lastFrame;
    private int openFrame;

    /// <summary>True while the session of the row with this popup id is open.</summary>
    public bool IsOpenFor(uint popup) => isOpen && id == popup;

    /// <summary>Takes the state of the popup of one row in one frame. Returns the events for the field edits.</summary>
    /// <param name="popup">The ImGui id of the popup of the row.</param>
    /// <param name="frame">The number of the frame.</param>
    /// <param name="open">The popup is open in this frame (<c>BeginPopup</c> returned true).</param>
    /// <param name="escape">The user pressed Escape in this frame.</param>
    /// <param name="changed">The picker changed the color in this frame.</param>
    /// <param name="owned">The open field edit belongs to this popup, before the routing of this frame.</param>
    public FieldEvents Frame(uint popup, int frame, bool open, bool escape, bool changed, bool owned)
    {
        if (isOpen && popup != id)
        {
            if (!open)
                return None;
            isOpen = false;
        }

        if (isOpen && frame > lastFrame + 1)
            isOpen = false;

        id = popup;
        lastFrame = frame;
        if (!isOpen)
        {
            if (!open)
                return None;
            isOpen = true;
            openFrame = frame;
            return new FieldEvents(Activated: true, Active: true, Changed: false, Deactivated: false, Cancelled: false);
        }

        if (frame > openFrame && !owned)
        {
            // The edit ended from outside, for example at the close prompt, or it did not start.
            isOpen = false;
            return None;
        }

        if (escape)
        {
            isOpen = false;
            return new FieldEvents(Activated: false, Active: false, Changed: false, Deactivated: true, Cancelled: true);
        }

        if (!open)
        {
            isOpen = false;
            return new FieldEvents(Activated: false, Active: false, Changed: false, Deactivated: true, Cancelled: false);
        }

        return new FieldEvents(Activated: false, Active: true, Changed: changed, Deactivated: false, Cancelled: false);
    }
}
